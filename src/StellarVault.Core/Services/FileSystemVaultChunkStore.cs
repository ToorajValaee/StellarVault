using System.Security.Cryptography;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Local immutable ciphertext-object backend. The caller must secure the directory
/// and supply a durable record index; this class is not a distributed object store.</summary>
public sealed class FileSystemVaultChunkStore : IVaultChunkStore
{
    private readonly string _directory;

    public FileSystemVaultChunkStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task<string> PutAsync(Stream encryptedContent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedContent);
        if (!encryptedContent.CanRead) throw new ArgumentException("Readable ciphertext required.", nameof(encryptedContent));
        string temporary = Path.Combine(_directory, $".pending-{Guid.NewGuid():N}");
        byte[] buffer = new byte[64 * 1024];
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                while (true)
                {
                    int n = await encryptedContent.ReadAsync(buffer, cancellationToken);
                    if (n == 0) break;
                    total += n;
                    if (total > VaultV2Envelope.MaximumLength)
                        throw new InvalidDataException("Ciphertext object exceeds size limit.");
                    digest.AppendData(buffer, 0, n);
                    await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                }
                if (total < VaultV2Envelope.MinimumLength)
                    throw new InvalidDataException("Ciphertext object is too short.");
                await destination.FlushAsync(cancellationToken);
            }
            string id = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            string target = Path.Combine(_directory, id);
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                // An immutable object with this digest already exists; verify before deduplicating.
                await using var existing = await OpenReadAsync(id, cancellationToken);
            }
            return id;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<Stream> OpenReadAsync(string contentId, CancellationToken cancellationToken = default)
    {
        ValidateId(contentId);
        string path = Path.Combine(_directory, contentId);
        // Verify the complete ciphertext before handing a private in-memory snapshot to callers.
        byte[] ciphertext = await File.ReadAllBytesAsync(path, cancellationToken);
        if (ciphertext.Length < VaultV2Envelope.MinimumLength || ciphertext.Length > VaultV2Envelope.MaximumLength ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(ciphertext)), contentId, StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            throw new CryptographicException("Stored ciphertext content hash or length mismatch.");
        }
        return new MemoryStream(ciphertext, writable: false);
    }

    public Task DeleteAsync(string contentId, CancellationToken cancellationToken = default)
    {
        ValidateId(contentId);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(_directory, contentId));
        return Task.CompletedTask;
    }

    private static void ValidateId(string id)
    {
        if (id is null || id.Length != 64 || id.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new ArgumentException("Expected lowercase SHA-256 ciphertext identifier.", nameof(id));
    }
}
