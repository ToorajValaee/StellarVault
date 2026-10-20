using System.Text.Json;
using Microsoft.JSInterop;
using StellarVaultPwa.Core;
using StellarVault.Core.Abstractions;

namespace StellarVaultPwa.Services;

public sealed class BrowserStorage(IJSRuntime js) : IVaultPreferences, IVaultPlatformActions
{
    private const string NetworkKey = "stellar-vault.network.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveNetworkAsync(VaultNetworkSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await js.InvokeVoidAsync("stellarVault.setLocal", NetworkKey, json);
    }

    public async Task<VaultNetworkSettings?> LoadNetworkAsync()
    {
        var json = await js.InvokeAsync<string?>("stellarVault.getLocal", NetworkKey);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<VaultNetworkSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public ValueTask CopyTextAsync(string value) => CopyAsync(value);

    public ValueTask CopyAsync(string value) =>
        js.InvokeVoidAsync("stellarVault.copyText", value);

    public ValueTask SetThemeAsync(string theme) =>
        js.InvokeVoidAsync("stellarVault.setTheme", theme);

    public ValueTask<string?> GetThemeAsync() =>
        js.InvokeAsync<string?>("stellarVault.getTheme");


    public ValueTask<HorizonNetworkInfo> InspectHorizonAsync(string horizonUrl) =>
        js.InvokeAsync<HorizonNetworkInfo>("stellarVault.network.inspectHorizon", horizonUrl);

    public ValueTask<string> RandomHexAsync(int byteCount) =>
        js.InvokeAsync<string>("stellarVault.crypto.randomHex", byteCount);

    public ValueTask<string> GeneratePasswordAsync(int length = 24) =>
        js.InvokeAsync<string>("stellarVault.crypto.generatePassword", length);

    public ValueTask PrepareModalAsync() =>
        js.InvokeVoidAsync("stellarVault.viewport.prepareModal");

    public ValueTask StabilizeViewportAsync() =>
        js.InvokeVoidAsync("stellarVault.viewport.stabilize");
}
