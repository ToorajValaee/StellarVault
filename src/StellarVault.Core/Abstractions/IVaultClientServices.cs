using StellarVaultPwa.Core;

namespace StellarVault.Core.Abstractions;

/// <summary>
/// Host-provided UI capabilities. Network settings and theme are non-secret.
/// The clipboard is sensitive and must only be called after explicit user action.
/// </summary>
public interface IVaultClientServices : IVaultPreferences, IVaultPlatformActions
{
    ValueTask CopyAsync(string value);
    ValueTask<HorizonNetworkInfo> InspectHorizonAsync(string horizonUrl);
    ValueTask PrepareModalAsync();
    ValueTask StabilizeViewportAsync();
}
