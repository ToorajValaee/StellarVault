using System.Text.Json;
using Microsoft.JSInterop;
using VaultEngine.Stellar.Abstractions;

namespace StellarVault.Pwa.Services;

/// <summary>Transactional browser pointer CAS; cannot prevent malicious rollback of IndexedDB.</summary>
public sealed class IndexedDbVaultRecordIndex(IJSRuntime js) : IVaultRecordIndex
{
    public async Task<VaultRecordPointer?> ReadAsync(Guid recordId, CancellationToken cancellationToken = default)
    {
        if (recordId == Guid.Empty) throw new ArgumentException("Record ID required.", nameof(recordId));
        var bytes = await js.InvokeAsync<byte[]?>("stellarVaultV2Db.readPointer", cancellationToken, recordId.ToString("N"));
        if (bytes is null) return null;
        var pointer = JsonSerializer.Deserialize<VaultRecordPointer>(bytes);
        Validate(pointer);
        return pointer;
    }

    public async Task<bool> CompareExchangeAsync(Guid recordId, VaultRecordPointer? expected,
        VaultRecordPointer next, CancellationToken cancellationToken = default)
    {
        if (recordId == Guid.Empty) throw new ArgumentException("Record ID required.", nameof(recordId));
        Validate(next);
        if (next.Generation != (expected is null ? 0 : checked(expected.Generation + 1)))
            throw new ArgumentException("Pointer generation must advance exactly once.", nameof(next));
        byte[]? previous = expected is null ? null : JsonSerializer.SerializeToUtf8Bytes(expected);
        byte[] replacement = JsonSerializer.SerializeToUtf8Bytes(next);
        return await js.InvokeAsync<bool>("stellarVaultV2Db.compareExchangePointer", cancellationToken,
            recordId.ToString("N"), previous, replacement);
    }

    private static void Validate(VaultRecordPointer? pointer)
    {
        if (pointer is null || pointer.Generation < 0 ||
            pointer.ManifestContentId is null || pointer.ManifestContentId.Length != 64 ||
            pointer.ManifestContentId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid encrypted manifest pointer.");
    }
}
