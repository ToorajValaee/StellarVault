using System.Security.Cryptography;
using Microsoft.JSInterop;
using VaultEngine.Stellar.Abstractions;
using VaultEngine.Stellar.Services;

namespace StellarVault.Pwa.Services;

/// <summary>Encrypted immutable object persistence in IndexedDB. No plaintext is stored.</summary>
public sealed class IndexedDbVaultObjectStore(IJSRuntime js) : IVaultChunkStore
{
    public async Task<string> PutAsync(Stream encryptedContent, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        byte[] block = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await encryptedContent.ReadAsync(block, cancellationToken);
                if (n == 0) break;
                if (buffer.Length + n > VaultV2Envelope.MaximumLength)
                    throw new InvalidDataException("Encrypted object exceeds limit.");
                await buffer.WriteAsync(block.AsMemory(0, n), cancellationToken);
            }
            byte[] ciphertext = buffer.ToArray();
            try
            {
                if (ciphertext.Length < VaultV2Envelope.MinimumLength)
                    throw new InvalidDataException("Encrypted object too short.");
                string id = Convert.ToHexStringLower(SHA256.HashData(ciphertext));
                bool inserted = await js.InvokeAsync<bool>("stellarVaultV2Db.createObject", cancellationToken, id, ciphertext);
                if (!inserted)
                {
                    var existing = await ReadVerifiedAsync(id, cancellationToken);
                    CryptographicOperations.ZeroMemory(existing);
                }
                return id;
            }
            finally { CryptographicOperations.ZeroMemory(ciphertext); }
        }
        finally { CryptographicOperations.ZeroMemory(block); }
    }

    public async Task<Stream> OpenReadAsync(string contentId, CancellationToken cancellationToken = default) =>
        new MemoryStream(await ReadVerifiedAsync(contentId, cancellationToken), writable: false);

    private async Task<byte[]> ReadVerifiedAsync(string contentId, CancellationToken ct)
    {
        ValidateId(contentId);
        byte[] data = await js.InvokeAsync<byte[]?>("stellarVaultV2Db.readObject", ct, contentId)
            ?? throw new FileNotFoundException("Encrypted object not found.");
        if (data.Length < VaultV2Envelope.MinimumLength ||
            data.Length > VaultV2Envelope.MaximumLength ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), contentId, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(data);
            throw new CryptographicException("Encrypted object integrity failure.");
        }
        return data;
    }

    public Task DeleteAsync(string contentId, CancellationToken cancellationToken = default)
    {
        ValidateId(contentId);
        return js.InvokeVoidAsync("stellarVaultV2Db.deleteObject", cancellationToken, contentId).AsTask();
    }

    private static void ValidateId(string id)
    {
        if (id is null || id.Length != 64 || id.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid encrypted object identifier.", nameof(id));
    }
}
