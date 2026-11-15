using System.Security.Cryptography;
using System.Text;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>
/// Account-scoped encrypted key/value record API. Names are never written to disk:
/// record identifiers are keyed, domain-separated digests of account and record name.
/// The caller supplies a uniformly random 256-bit vault key obtained from an approved
/// key-management flow; this class does not derive keys from passwords or sign transactions.
/// </summary>
public sealed class StellarAccountVaultDatabase
{
    private const string RecordDomain = "StellarVault/account-record/v1";
    private const string ContextDomain = "StellarVault/account-manifest/v1";
    private const string FileTreePointerPrefix = "SVFT3:";
    private readonly VaultEncryptedRecordService _records;
    private readonly IVaultChunkObjectStore _fileObjects;
    private readonly IVaultRecordIndex _pointers;
    private readonly string _accountId;

    /// <summary>Encrypted directory for listing and logically deleting named records.</summary>
    public StellarVaultRecordCatalog Catalog => new(this);

    /// <summary>Typed encrypted JSON document operations for application data.</summary>
    public StellarVaultJsonDocuments Documents => new(this);

    public StellarAccountVaultDatabase(IVaultChunkStore objects, IVaultRecordIndex pointers, string stellarAccountId)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pointers);
        ArgumentException.ThrowIfNullOrWhiteSpace(stellarAccountId);
        StellarAccountAddress.Validate(stellarAccountId);
        _accountId = stellarAccountId;
        _pointers = pointers;
        _records = new VaultEncryptedRecordService(objects, pointers);
        _fileObjects = new VaultChunkObjectStoreAdapter(objects);
    }

    /// <summary>Derive an opaque, account-bound record identifier without exposing the record name.</summary>
    public Guid GetRecordId(string name, ReadOnlySpan<byte> vaultKey)
    {
        ValidateName(name);
        ValidateKey(vaultKey);
        byte[] message = Encoding.UTF8.GetBytes(RecordDomain + "\0" + _accountId + "\0" + name);
        try
        {
            Span<byte> digest = stackalloc byte[32];
            HMACSHA256.HashData(vaultKey, message, digest);
            // A deterministic 128-bit ID is sufficient for private keyed record addressing.
            return new Guid(digest[..16]);
        }
        finally { CryptographicOperations.ZeroMemory(message); }
    }

    /// <summary>Read the currently committed record generation, if one exists.</summary>
    public Task<VaultRecordPointer?> GetVersionAsync(string name, ReadOnlyMemory<byte> vaultKey,
        CancellationToken cancellationToken = default) =>
        _pointers.ReadAsync(GetRecordId(name, vaultKey.Span), cancellationToken);

    /// <summary>Write or replace a named record using optimistic concurrency.</summary>
    public Task<VaultRecordPointer> PutAsync(string name, VaultRecordPointer? expectedVersion,
        Stream plaintext, ReadOnlyMemory<byte> vaultKey, CancellationToken cancellationToken = default)
    {
        var recordId = GetRecordId(name, vaultKey.Span);
        byte[] context = BuildManifestContext(recordId);
        return WriteWithContextAsync(recordId, expectedVersion, plaintext, vaultKey, context, cancellationToken);
    }

    /// <summary>Write a named record and then register it in the encrypted catalog.
    /// These are two separate commits; if catalog registration fails the record remains
    /// addressable by name but may be absent from listings until RegisterAsync is retried.</summary>
    public async Task<VaultRecordPointer> PutIndexedAsync(string name, VaultRecordPointer? expectedVersion,
        Stream plaintext, ReadOnlyMemory<byte> vaultKey, string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        if (name.StartsWith("__svault/", StringComparison.Ordinal))
            throw new ArgumentException("Reserved system record name.", nameof(name));
        var pointer = await PutAsync(name, expectedVersion, plaintext, vaultKey, cancellationToken);
        await Catalog.RegisterAsync(name, contentType, vaultKey, cancellationToken);
        return pointer;
    }

    /// <summary>Read a named record into caller-owned private staging. Do not publish staging until success.</summary>
    public Task GetAsync(string name, Stream privateStaging, ReadOnlyMemory<byte> vaultKey,
        CancellationToken cancellationToken = default)
    {
        var recordId = GetRecordId(name, vaultKey.Span);
        byte[] context = BuildManifestContext(recordId);
        return ReadWithContextAsync(recordId, privateStaging, vaultKey, context, cancellationToken);
    }

    /// <summary>
    /// Stage an arbitrarily long file as individually encrypted immutable
    /// content-addressed objects. Only after the root exists is its small
    /// encrypted record pointer atomically committed and catalog-registered.
    /// A crash before pointer commit leaves inaccessible orphan ciphertext,
    /// never a partially readable file.
    /// </summary>
    public async Task<VaultRecordPointer> PutIndexedStreamingFileAsync(
        string name, VaultRecordPointer? expectedVersion,
        Stream plaintext, ReadOnlyMemory<byte> vaultKey, string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidateName(name);
        StellarLedgerFileProtocol.ValidateName(name);
        var scope = BuildFileTreeContext(GetRecordId(name, vaultKey.Span));
        try
        {
            var result = await VaultStreamingEncryptedFileStore.StageAsync(
                plaintext, name, contentType, _fileObjects, vaultKey,
                scope, cancellationToken);
            var pointerBytes = Encoding.ASCII.GetBytes(FileTreePointerPrefix + result.RootId);
            try
            {
                using var pointerStream = new MemoryStream(pointerBytes, writable: false);
                return await PutIndexedAsync(name, expectedVersion, pointerStream,
                    vaultKey, contentType, cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(pointerBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(scope); }
    }

    /// <summary>
    /// Verify a file root and all referenced encrypted nodes into private
    /// caller-owned seekable staging. The caller must verify its record revision
    /// and catalog visibility again before exposing or promoting the result.
    /// </summary>
    public async Task<VaultStreamingFileTree.Root> ReadStreamingFileAsync(
        string name, Stream privateStaging, ReadOnlyMemory<byte> vaultKey,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(privateStaging);
        using var pointerStream = new BoundedFilePointerStream();
        await GetAsync(name, pointerStream, vaultKey, cancellationToken);
        byte[] bytes = pointerStream.ToArray();
        string rootId;
        try
        {
            var value = Encoding.ASCII.GetString(bytes);
            if (value.Length != FileTreePointerPrefix.Length + 64 ||
                !value.StartsWith(FileTreePointerPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("Invalid streamed file pointer.");
            rootId = value[FileTreePointerPrefix.Length..];
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }

        var scope = BuildFileTreeContext(GetRecordId(name, vaultKey.Span));
        try
        {
            return await VaultStreamingEncryptedFileStore.RestoreAsync(
                rootId, _fileObjects, privateStaging, vaultKey, scope, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(scope); }
    }

    private byte[] BuildFileTreeContext(Guid recordId) =>
        Encoding.UTF8.GetBytes("StellarVault/account-file-tree/v1\0" +
            _accountId + "\0" + recordId.ToString("N"));

    private sealed class BoundedFilePointerStream : MemoryStream
    {
        private const int MaximumPointerLength = 128;

        private void RequireLength(int added)
        {
            if (added > MaximumPointerLength - Position)
                throw new InvalidDataException("Invalid streamed file pointer length.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireLength(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireLength(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            RequireLength(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private async Task<VaultRecordPointer> WriteWithContextAsync(Guid recordId, VaultRecordPointer? expected,
        Stream plaintext, ReadOnlyMemory<byte> key, byte[] context, CancellationToken ct)
    {
        try { return await _records.WriteAsync(recordId, expected, plaintext, key, context, ct); }
        finally { CryptographicOperations.ZeroMemory(context); }
    }

    private async Task ReadWithContextAsync(Guid recordId, Stream staging, ReadOnlyMemory<byte> key,
        byte[] context, CancellationToken ct)
    {
        try { await _records.ReadAsync(recordId, staging, key, context, ct); }
        finally { CryptographicOperations.ZeroMemory(context); }
    }

    private byte[] BuildManifestContext(Guid recordId) =>
        Encoding.UTF8.GetBytes(ContextDomain + "\0" + _accountId + "\0" + recordId.ToString("N"));

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Encoding.UTF8.GetByteCount(name) > 512 || name.IndexOf('\0') >= 0)
            throw new ArgumentException("Record name must be at most 512 UTF-8 bytes and contain no NUL.", nameof(name));
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("Expected a 256-bit vault key.", nameof(key));
    }
}
