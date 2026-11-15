namespace StellarVault.Core.Services;

/// <summary>
/// Returns the exact public S-key v1 skh manifest's base64 value for an
/// existing record. This is an optimistic-concurrency token, NOT historical
/// transaction proof. A missing or unsupported source fails queued edits shut.
/// </summary>
public interface IStellarSecretKeyRecordRevisionReader
{
    Task<string?> ReadRecordRevisionAsync(string recordId,
        CancellationToken ct = default);
}
