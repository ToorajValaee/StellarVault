using System.Text.Json;
using StellarVault.Core.Abstractions;

namespace StellarVault.Core.Services;

/// <summary>Single-process durable compare-and-swap pointer index. Requires a trusted
/// private directory and one writer process; it is not a distributed lock or rollback anchor.</summary>
public sealed class FileSystemVaultRecordIndex : IVaultRecordIndex
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSystemVaultRecordIndex(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async Task<VaultRecordPointer?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        ValidateRecord(recordId);
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(recordId, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<bool> CompareExchangeAsync(Guid recordId, VaultRecordPointer? expected,
        VaultRecordPointer next, CancellationToken cancellationToken = default)
    {
        ValidateRecord(recordId);
        ArgumentNullException.ThrowIfNull(next);
        if (next.Generation < 0 || next.ManifestContentId is null ||
            next.ManifestContentId.Length != 64 || next.ManifestContentId.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new ArgumentException("Expected lowercase SHA-256 manifest ID and nonnegative generation.", nameof(next));
        if (next.Generation != (expected is null ? 0 : checked(expected.Generation + 1)))
            throw new ArgumentException("Next generation must advance exactly once.", nameof(next));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadCoreAsync(recordId, cancellationToken);
            if (current != expected) return false;
            string target = PathFor(recordId);
            string temporary = Path.Combine(_directory, $".pending-{Guid.NewGuid():N}");
            try
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(next);
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(data, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temporary, target, overwrite: true);
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    private async Task<VaultRecordPointer?> ReadCoreAsync(Guid recordId, CancellationToken cancellationToken)
    {
        string path = PathFor(recordId);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length > 4096) throw new InvalidDataException("Record pointer exceeds size limit.");
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var pointer = JsonSerializer.Deserialize<VaultRecordPointer>(bytes)
            ?? throw new InvalidDataException("Record pointer is invalid.");
        if (pointer.Generation < 0 || pointer.ManifestContentId is null ||
            pointer.ManifestContentId.Length != 64 || pointer.ManifestContentId.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new InvalidDataException("Stored record pointer is invalid.");
        return pointer;
    }

    private string PathFor(Guid recordId) => Path.Combine(_directory, recordId.ToString("N") + ".json");
    private static void ValidateRecord(Guid recordId)
    {
        if (recordId == Guid.Empty) throw new ArgumentException("Record ID required.", nameof(recordId));
    }
}
