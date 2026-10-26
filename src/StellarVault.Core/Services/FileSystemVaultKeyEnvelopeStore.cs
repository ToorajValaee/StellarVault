using System.Security.Cryptography;
using System.Text;

namespace StellarVault.Core.Services;

/// <summary>Local private-directory implementation of wrapped-key storage.
/// Cooperating processes serialize updates with an exclusive filesystem lock.
/// This does not protect against malicious filesystem writers or disk rollback.</summary>
public sealed class FileSystemVaultKeyEnvelopeStore : IVaultKeyEnvelopeStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemVaultKeyEnvelopeStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task<byte[]?> ReadAsync(string accountId, CancellationToken cancellationToken = default)
    {
        string path = PathFor(accountId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length != VaultKeyEnvelope.WrappedLength)
            throw new CryptographicException("Invalid stored vault key envelope length.");
        byte[] data = new byte[VaultKeyEnvelope.WrappedLength];
        try
        {
            await stream.ReadExactlyAsync(data, cancellationToken);
            if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
                throw new CryptographicException("Stored key envelope changed during read.");
            return data;
        }
        catch { CryptographicOperations.ZeroMemory(data); throw; }
    }

    public Task<bool> CreateAsync(string accountId, ReadOnlyMemory<byte> envelope,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(accountId, null, envelope, cancellationToken);

    public Task<bool> ReplaceAsync(string accountId, ReadOnlyMemory<byte> expectedEnvelope,
        ReadOnlyMemory<byte> nextEnvelope, CancellationToken cancellationToken = default) =>
        UpdateAsync(accountId, expectedEnvelope, nextEnvelope, cancellationToken);

    private async Task<bool> UpdateAsync(string accountId, ReadOnlyMemory<byte>? expected,
        ReadOnlyMemory<byte> next, CancellationToken ct)
    {
        if (next.Length != VaultKeyEnvelope.WrappedLength)
            throw new ArgumentException("Invalid wrapped key envelope length.", nameof(next));
        string target = PathFor(accountId);
        await _gate.WaitAsync(ct);
        try
        {
            string lockPath = Path.Combine(_directory, ".key-envelope.lock");
            await using var exclusive = await AcquireLockAsync(lockPath, ct);
            byte[]? current = await ReadAsync(accountId, ct);
            try
            {
                if (expected is null ? current is not null : current is null ||
                    !CryptographicOperations.FixedTimeEquals(expected.Value.Span, current))
                    return false;
            }
            finally { if (current is not null) CryptographicOperations.ZeroMemory(current); }
            string pending = Path.Combine(_directory, ".pending-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(next, ct);
                    await output.FlushAsync(ct);
                }
                File.Move(pending, target, overwrite: expected is not null);
                return true;
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        finally { _gate.Release(); }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (timeout.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Timed out waiting for key envelope lock.");
                }
                try { await Task.Delay(25, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new TimeoutException("Timed out waiting for key envelope lock."); }
            }
        }
    }

    private string PathFor(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        StellarAccountAddress.Validate(accountId);
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(accountId));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash) + ".key");
    }
}
