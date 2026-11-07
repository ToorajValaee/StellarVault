namespace StellarVault.Core.Services;

/// <summary>Host-provided file selection and explicit, user-directed export.
/// The returned stream is caller-owned and must be disposed promptly.</summary>
public interface IVaultNativeFileAccess
{
    Task<VaultNativePickedFile?> PickAsync();
    Task<bool> SaveAsync(string fileName, Stream content);
}

public sealed record VaultNativePickedFile(
    string FileName, Stream Stream,
    string ContentType = "application/octet-stream");
