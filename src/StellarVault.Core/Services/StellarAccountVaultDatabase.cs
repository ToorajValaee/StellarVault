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
    private readonly VaultEncryptedRecordService _records;
    private readonly string _accountId;

    public StellarAccountVaultDatabase(IVaultChunkStore objects, IVaultRecordIndex pointers, string stellarAccountId)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pointers);
        ArgumentException.ThrowIfNullOrWhiteSpace(stellarAccountId);
        // Account IDs are canonical public Stellar G-addresses (56 uppercase base32 chars).
        // Full StrKey checksum validation belongs to the Stellar account boundary.
        if (stellarAccountId.Length != 56 || stellarAccountId[0] != 'G' ||
            stellarAccountId.Any(c => !((c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7'))))
            throw new ArgumentException("Expected a canonical Stellar G-account address.", nameof(stellarAccountId));
        _accountId = stellarAccountId;
        _records = new VaultEncryptedRecordService(objects, pointers);
    }

    /// <summary>Derive an opaque, account-bound record identifier without exposing the record name.</summary>
    public Guid GetRecordId(string name, ReadOnlySpan<byte> vaultKey)
    {
        ValidateName(name);
        ValidateKey(vaultKey);
        byte[] message = Encoding.UTF8.GetBytes(RecordDomain + "\\0" + _accountId + "\\0" + name);
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
        IVaultRecordIndex pointers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pointers);
        return pointers.ReadAsync(GetRecordId(name, vaultKey.Span), cancellationToken);
    }

    /// <summary>Write or replace a named record using optimistic concurrency.</summary>
    public Task<VaultRecordPointer> PutAsync(string name, VaultRecordPointer? expectedVersion,
        Stream plaintext, ReadOnlyMemory<byte> vaultKey, CancellationToken cancellationToken = default)
    {
        var recordId = GetRecordId(name, vaultKey.Span);
        byte[] context = BuildManifestContext(recordId);
        return WriteWithContextAsync(recordId, expectedVersion, plaintext, vaultKey, context, cancellationToken);
    }

    /// <summary>Read a named record into caller-owned private staging. Do not publish staging until success.</summary>
    public Task GetAsync(string name, Stream privateStaging, ReadOnlyMemory<byte> vaultKey,
        CancellationToken cancellationToken = default)
    {
        var recordId = GetRecordId(name, vaultKey.Span);
        byte[] context = BuildManifestContext(recordId);
        return ReadWithContextAsync(recordId, privateStaging, vaultKey, context, cancellationToken);
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
        Encoding.UTF8.GetBytes(ContextDomain + "\\0" + _accountId + "\\0" + recordId.ToString("N"));

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Encoding.UTF8.GetByteCount(name) > 512 || name.IndexOf('\\0') >= 0)
            throw new ArgumentException("Record name must be at most 512 UTF-8 bytes and contain no NUL.", nameof(name));
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("Expected a 256-bit vault key.", nameof(key));
    }
}
