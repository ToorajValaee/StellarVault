namespace VaultEngine.Stellar.Services;

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

/// <summary>
/// Optional host capability for browser user activation: select a file
/// destination immediately from a real button gesture, *before* any awaited
/// ledger read, password KDF or verification. The returned sink must abort
/// rather than commit on failure. Other hosts may keep using SaveAsync.
/// </summary>
public interface IVaultDeferredFileSaveAccess
{
    Task<IVaultPendingFileSave?> SelectSaveDestinationAsync(string fileName);
}

public interface IVaultPendingFileSave : IAsyncDisposable
{
    /// <summary>Write and commit only after the caller has completed its
    /// account/network verification. Dispose without writing must not create
    /// a partial completed export.</summary>
    Task SaveAsync(Stream source);
}
