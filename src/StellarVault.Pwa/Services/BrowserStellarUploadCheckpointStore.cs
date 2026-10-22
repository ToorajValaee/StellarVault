using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Adapter for legacy browser recovery checkpoints. Storage is localStorage
/// metadata only, not encrypted. This adapter never persists file plaintext.
/// </summary>
public sealed class BrowserStellarUploadCheckpointStore(IJSRuntime js) : IStellarUploadCheckpointStore
{
    public async Task<StellarUploadCheckpoint?> LoadAsync(string accountId, CancellationToken ct = default)
    {
        var checkpoint = await js.InvokeAsync<StellarUploadCheckpoint?>(
            "stellarVaultUploadCheckpoint.load", ct, accountId);
        checkpoint?.Validate();
        return checkpoint;
    }

    public async Task SaveAsync(string accountId, StellarUploadCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        checkpoint.Validate();
        await js.InvokeVoidAsync("stellarVaultUploadCheckpoint.save", ct,
            accountId, checkpoint.Fingerprint, checkpoint.Ids, checkpoint.Step);
    }

    public Task ClearAsync(string accountId, CancellationToken ct = default) =>
        js.InvokeVoidAsync("stellarVaultUploadCheckpoint.clear", ct, accountId).AsTask();
}
