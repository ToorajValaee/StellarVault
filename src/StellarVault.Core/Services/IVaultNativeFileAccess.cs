namespace StellarVault.Core.Services;

/// <summary>Host-provided native file access. Shared Razor components never
/// depend on MAUI APIs directly.</summary>
public interface IVaultNativeFileAccess
{
    Task<VaultNativePickedFile?> PickAsync();
    Task<bool> SaveAsync(string fileName, Stream content);
}

public sealed record VaultNativePickedFile(string FileName, Stream Stream);
