using System.Security.Cryptography;
using VaultEngine.Stellar.Abstractions;

namespace VaultEngine.Stellar.Services;

/// <summary>Development-only immutable, content-addressed encrypted object store.
/// IDs are lowercase SHA-256 hex digests. No persistence or remote authorization.</summary>
public sealed class InMemoryVaultChunkStore : IVaultChunkStore
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public const int MaxObjectLength = VaultV2Envelope.MaximumLength;

    public async Task<string> PutAsync(Stream encryptedContent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedContent);
        if (!encryptedContent.CanRead) throw new ArgumentException("Readable ciphertext stream required.", nameof(encryptedContent));
        using var buffer = new MemoryStream();
        byte[] block = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int read = await encryptedContent.ReadAsync(block, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaxObjectLength) throw new InvalidDataException("Encrypted object exceeds size limit.");
                await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
            }
            byte[] ciphertext = buffer.ToArray();
            if (ciphertext.Length < VaultV2Envelope.MinimumLength)
                throw new InvalidDataException("Encrypted object is too small.");
            string id = Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant();
            lock (_gate)
            {
                if (_objects.TryGetValue(id, out var existing))
                {
                    if (!CryptographicOperations.FixedTimeEquals(existing, ciphertext))
                        throw new CryptographicException("Content identifier collision.");
                }
                else _objects.Add(id, ciphertext);
            }
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(block); }
    }

    public Task<Stream> OpenReadAsync(string contentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateId(contentId);
        lock (_gate)
        {
            if (!_objects.TryGetValue(contentId, out var content)) throw new FileNotFoundException("Encrypted object not found.");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), contentId, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Encrypted object hash mismatch.");
            return Task.FromResult<Stream>(new MemoryStream((byte[])content.Clone(), writable: false));
        }
    }

    public Task DeleteAsync(string contentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateId(contentId);
        lock (_gate)
        {
            if (_objects.Remove(contentId, out var removed)) CryptographicOperations.ZeroMemory(removed);
        }
        return Task.CompletedTask;
    }

    private static void ValidateId(string id)
    {
        if (id is null || id.Length != 64 || id.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Expected a lowercase SHA-256 content identifier.", nameof(id));
    }
}
