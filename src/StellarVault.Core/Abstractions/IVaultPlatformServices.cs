using StellarVaultPwa.Core;

namespace StellarVault.Core.Abstractions;

/// <summary>Non-secret preferences shared across browser and native hosts.</summary>
public interface IVaultPreferences
{
    Task SaveNetworkAsync(VaultNetworkSettings settings);
    Task<VaultNetworkSettings?> LoadNetworkAsync();
    ValueTask<string?> GetThemeAsync();
    ValueTask SetThemeAsync(string theme);
}

/// <summary>Explicit platform capabilities; implementations must not persist secrets.</summary>
public interface IVaultPlatformActions
{
    ValueTask CopyTextAsync(string value);
    ValueTask<string> GeneratePasswordAsync(int length);
    ValueTask<string> RandomHexAsync(int byteCount);
}

/// <summary>
/// Read-only status contract for shared UI. No mnemonic or signing key exposure.
/// </summary>
public interface IVaultSessionStatus
{
    bool IsUnlocked { get; }
    bool IsBusy { get; }
    string? StatusMessage { get; }
    event Action? Changed;
}
