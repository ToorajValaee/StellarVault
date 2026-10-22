using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Native app-private filesystem checkpoint storage. Stores metadata only; it is
/// NOT encrypted and does not protect against OS compromise or disk rollback.
/// Writes use a same-directory temporary file and atomic rename where supported.
/// Missing files return null; malformed checkpoints throw and block recovery.
/// </summary>
public sealed class FileSystemStellarUploadCheckpointStore : IStellarUploadCheckpointStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemStellarUploadCheckpointStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string accountId)
    {
        StellarAccountAddress.Validate(accountId);
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(accountId));
        return Path.Combine(_directory, Convert.ToHexStringLower(digest) + ".upload");
    }

    public async Task<StellarUploadCheckpoint?> LoadAsync(string accountId, CancellationToken ct = default)
    {
        var path = PathFor(accountId);
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 16 * 1024)
                throw new InvalidDataException("Upload checkpoint exceeds size limit.");
            byte[] data = await File.ReadAllBytesAsync(path, ct);
            try
            {
                if (data.Length > 16 * 1024)
                    throw new InvalidDataException("Upload checkpoint exceeds size limit.");
                var checkpoint = JsonSerializer.Deserialize<StellarUploadCheckpoint>(data)
                    ?? throw new InvalidDataException("Upload checkpoint is empty.");
                checkpoint.Validate();
                return checkpoint;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Upload checkpoint is corrupted; recovery is blocked.", ex);
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string accountId, StellarUploadCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        string target = PathFor(accountId);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
        string pending = Path.Combine(_directory, ".pending-upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (data.Length > 16 * 1024)
                throw new InvalidDataException("Upload checkpoint exceeds size limit.");
            await _gate.WaitAsync(ct);
            try
            {
                // Never replace a damaged existing checkpoint: an unchecked
                // overwrite could conceal a previously submitted transaction.
                if (File.Exists(target))
                {
                    var length = new FileInfo(target).Length;
                    if (length > 16 * 1024)
                        throw new InvalidDataException("Existing upload checkpoint exceeds size limit.");
                    byte[] old = await File.ReadAllBytesAsync(target, ct);
                    try
                    {
                        if (old.Length > 16 * 1024)
                            throw new InvalidDataException("Existing upload checkpoint exceeds size limit.");
                        var previous = JsonSerializer.Deserialize<StellarUploadCheckpoint>(old)
                            ?? throw new InvalidDataException("Existing upload checkpoint is empty.");
                        previous.Validate();
                        if (previous.Fingerprint != checkpoint.Fingerprint ||
                            !previous.Ids.SequenceEqual(checkpoint.Ids, StringComparer.Ordinal) ||
                            checkpoint.Step < previous.Step)
                            throw new InvalidDataException(
                                "Checkpoint identity or confirmed progress differs; reconcile before retry.");
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException("Existing upload checkpoint corrupted; recovery blocked.", ex);
                    }
                    finally { CryptographicOperations.ZeroMemory(old); }
                }
                await using (var stream = new FileStream(pending, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(data, ct);
                    await stream.FlushAsync(ct);
                }
                File.Move(pending, target, overwrite: true);
            }
            finally { _gate.Release(); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    public async Task ClearAsync(string accountId, CancellationToken ct = default)
    {
        string path = PathFor(accountId);
        await _gate.WaitAsync(ct);
        try { File.Delete(path); }
        finally { _gate.Release(); }
    }
}
