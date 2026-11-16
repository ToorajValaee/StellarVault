using Microsoft.JSInterop;
using VaultEngine.Stellar.Services;

namespace StellarVault.Pwa.Services;

/// <summary>Browser Web Crypto AES-GCM bridge preserving SVKW/v1 key envelopes.</summary>
public sealed class BrowserVaultKeyProtector(IJSRuntime js) : IVaultKeyProtector
{
    public Task<byte[]> WrapAsync(byte[] dataKey, byte[] wrappingKey, string accountId, CancellationToken ct = default) =>
        js.InvokeAsync<byte[]>("stellarVaultV2KeyWrap.wrap", ct, dataKey, wrappingKey, accountId).AsTask();

    public Task<byte[]> UnwrapAsync(byte[] envelope, byte[] wrappingKey, string accountId, CancellationToken ct = default) =>
        js.InvokeAsync<byte[]>("stellarVaultV2KeyWrap.unwrap", ct, envelope, wrappingKey, accountId).AsTask();
}
