using System.Security.Cryptography;
using System.Text;

namespace StellarVault.Core.Services;

/// <summary>
/// Encrypts the streaming file-tree nodes into the existing content-addressed
/// IVaultChunkObjectStore. References contain SHA-256(ciphertext), not plaintext
/// hashes or a huge flat index. The store sees only encrypted node envelopes.
/// Callers must provide a separately authorized 256-bit unlocked vault key and
/// account/network-bound context and retain the returned root ID durably.
/// Neither this adapter nor the tree broadcasts ledger transactions.
/// </summary>
public static class VaultStreamingEncryptedFileStore
{
    private const string ContextPrefix = "svault:encrypted-file-tree:v1:";
    private const byte LeafCode = 1;
    private const byte BranchCode = 2;
    private const byte RootCode = 3;

    public static Task<VaultStreamingFileTree.Result> StageAsync(
        Stream input, string fileName, string contentType,
        IVaultChunkObjectStore objects, ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> vaultScope, CancellationToken ct = default)
    {
        RequireInputs(objects, key, vaultScope);
        return VaultStreamingFileTree.WriteContentAddressedAsync(
            input, fileName, contentType,
            async (node, token) =>
            {
                var code = KindCode(node.Kind);
                var context = Context(vaultScope.Span, code);
                byte[]? envelope = null;
                byte[]? stored = null;
                try
                {
                    envelope = VaultV2Envelope.Seal(key.Span, node.Plaintext, context);
                    stored = new byte[envelope.Length + 1];
                    stored[0] = code;
                    envelope.CopyTo(stored, 1);
                    string objectId = Convert.ToHexString(
                        SHA256.HashData(stored)).ToLowerInvariant();
                    // Commit each encrypted object before it can be referenced
                    // by a parent; the root object is written LAST.
                    await objects.PutAsync(objectId, stored, token);
                    return objectId;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(context);
                    if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
                    if (stored is not null) CryptographicOperations.ZeroMemory(stored);
                }
            }, VaultStreamingFileTree.MaxLocalChunkBytes, ct);
    }

    /// <summary>
    /// Decrypts and verifies a full tree to a private seekable temporary stream.
    /// The stream is rolled back if ANY ciphertext, node or root hash fails.
    /// The caller is responsible for deleting/discarding temporary plaintext
    /// and exposing it only after a successful return.
    /// </summary>
    public static async Task<VaultStreamingFileTree.Root> RestoreAsync(
        string rootId, IVaultChunkObjectStore objects,
        Stream privateStaging, ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> vaultScope, CancellationToken ct = default)
    {
        RequireInputs(objects, key, vaultScope);
        ArgumentNullException.ThrowIfNull(privateStaging);
        if (!privateStaging.CanSeek || !privateStaging.CanWrite ||
            privateStaging.Position != privateStaging.Length)
            throw new ArgumentException("Append-only seekable private staging required.", nameof(privateStaging));
        long offset = privateStaging.Position;
        try
        {
            return await VaultStreamingFileTree.ReadVerifiedAsync(rootId,
                async (objectId, token) =>
                {
                    if (objectId is not { Length: 64 } ||
                        !objectId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                        throw new InvalidDataException("Invalid encrypted file object identifier.");
                    byte[]? stored = null;
                    byte[]? context = null;
                    try
                    {
                        stored = await objects.GetAsync(objectId, token);
                        if (stored.Length <= 1 ||
                            !string.Equals(Convert.ToHexString(
                                SHA256.HashData(stored)).ToLowerInvariant(),
                                objectId, StringComparison.Ordinal))
                            throw new CryptographicException("Encrypted file object digest mismatch.");
                        byte code = stored[0];
                        var kind = CodeKind(code);
                        context = Context(vaultScope.Span, code);
                        var plaintext = VaultV2Envelope.Open(
                            key.Span, stored.AsSpan(1), context);
                        return new VaultStreamingFileTree.Emission(
                            objectId, kind, plaintext);
                    }
                    finally
                    {
                        if (stored is not null) CryptographicOperations.ZeroMemory(stored);
                        if (context is not null) CryptographicOperations.ZeroMemory(context);
                    }
                }, privateStaging, ct);
        }
        catch
        {
            // The bytes must remain in a private, disposable staging stream.
            privateStaging.SetLength(offset);
            privateStaging.Position = offset;
            throw;
        }
    }

    private static void RequireInputs(IVaultChunkObjectStore? objects,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> scope)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (key.Length != 32) throw new ArgumentException("Expected AES-256 key.", nameof(key));
        if (scope.IsEmpty || scope.Length > 512)
            throw new ArgumentException("Explicit bounded vault/network context required.", nameof(scope));
    }

    private static byte[] Context(ReadOnlySpan<byte> scope, byte code)
    {
        var prefix = Encoding.ASCII.GetBytes(ContextPrefix);
        var result = new byte[prefix.Length + 1 + scope.Length];
        prefix.CopyTo(result, 0);
        result[prefix.Length] = code;
        scope.CopyTo(result.AsSpan(prefix.Length + 1));
        CryptographicOperations.ZeroMemory(prefix);
        return result;
    }

    private static byte KindCode(string kind) => kind switch
    {
        VaultStreamingFileTree.LeafKind => LeafCode,
        VaultStreamingFileTree.BranchKind => BranchCode,
        VaultStreamingFileTree.RootKind => RootCode,
        _ => throw new InvalidDataException("Invalid streamed file node kind.")
    };

    private static string CodeKind(byte code) => code switch
    {
        LeafCode => VaultStreamingFileTree.LeafKind,
        BranchCode => VaultStreamingFileTree.BranchKind,
        RootCode => VaultStreamingFileTree.RootKind,
        _ => throw new InvalidDataException("Invalid encrypted streamed file node kind.")
    };
}
