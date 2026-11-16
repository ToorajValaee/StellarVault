using System.Security.Cryptography;
using System.Text;

namespace VaultEngine.Stellar.Services;

/// <summary>Atomic filesystem persistence of KDF settings and wrapped key.
/// Does not defend against malicious disk rollback, deletion, or OS-level compromise.</summary>
public sealed class FileSystemVaultPassphraseCredentialStore : IVaultPassphraseCredentialStore
{
    private readonly string _directory;

    public FileSystemVaultPassphraseCredentialStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string accountId)
    {
        StellarAccountAddress.Validate(accountId);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(accountId));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash) + ".passphrase");
    }

    public async Task<byte[]?> ReadAsync(string accountId, CancellationToken ct = default)
    {
        string path = PathFor(accountId);
        if (!File.Exists(path)) return null;
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        try
        {
            var (_, envelope) = VaultPassphraseCredentials.Decode(bytes);
            CryptographicOperations.ZeroMemory(envelope);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public Task<bool> CreateAsync(string accountId, ReadOnlyMemory<byte> record, CancellationToken ct = default) =>
        SaveAsync(accountId, null, record, ct);

    public Task<bool> ReplaceAsync(string accountId, ReadOnlyMemory<byte> expected,
        ReadOnlyMemory<byte> replacement, CancellationToken ct = default) =>
        SaveAsync(accountId, expected, replacement, ct);

    private async Task<bool> SaveAsync(string accountId, ReadOnlyMemory<byte>? expected,
        ReadOnlyMemory<byte> next, CancellationToken ct)
    {
        // Validate before any write.
        var (_, validatedEnvelope) = VaultPassphraseCredentials.Decode(next.Span);
        CryptographicOperations.ZeroMemory(validatedEnvelope);
        string target = PathFor(accountId);
        string lockPath = Path.Combine(_directory, ".passphrase.lock");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        FileStream exclusive;
        while (true)
        {
            try
            {
                exclusive = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException)
            {
                if (timeout.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Timed out acquiring credential lock.");
                }
                await Task.Delay(50, timeout.Token);
            }
        }
        await using (exclusive)
        {
            byte[]? current = await ReadAsync(accountId, ct);
            try
            {
                if (expected is null ? current is not null :
                    current is null || !CryptographicOperations.FixedTimeEquals(expected.Value.Span, current))
                    return false;
            }
            finally
            {
                if (current is not null) CryptographicOperations.ZeroMemory(current);
            }

            string pending = Path.Combine(_directory, ".pending-passphrase-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var output = new FileStream(pending, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(next, ct);
                    await output.FlushAsync(ct);
                }
                File.Move(pending, target, overwrite: expected is not null);
                return true;
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }
}
