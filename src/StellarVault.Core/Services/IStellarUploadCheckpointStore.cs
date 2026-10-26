using System.Text.RegularExpressions;

namespace StellarVault.Core.Services;

/// <summary>
/// Persisted recovery metadata only: no file bytes, mnemonic, key, or plaintext draft.
/// Host adapters must document their at-rest confidentiality guarantees.
/// An invalid checkpoint must never be treated as an absent checkpoint.
/// </summary>
public sealed record StellarUploadCheckpoint(int Version, string Fingerprint, string[] Ids, int Step)
{
    public const int CurrentVersion = 1;
    public void Validate()
    {
        if (Version != CurrentVersion ||
            Fingerprint is null || !Regex.IsMatch(Fingerprint, "^[A-F0-9]{64}$", RegexOptions.CultureInvariant) ||
            Ids is null || Ids.Length is < 1 or > 513 ||
            Step < 0 || Step > Ids.Length ||
            Ids.Any(id => id is null ||
                !Regex.IsMatch(id, "^[a-f0-9]{16}$", RegexOptions.CultureInvariant)) ||
            Ids.Distinct(StringComparer.Ordinal).Count() != Ids.Length)
            throw new InvalidDataException("Invalid upload checkpoint; recovery must stop.");
    }
}

/// <summary>
/// Platform-specific checkpoint storage. Null means genuinely absent; corruption
/// must throw rather than return null. Existing PWA checkpoints use localStorage
/// and are NOT encrypted, so they must contain identifiers only.
/// </summary>
public interface IStellarUploadCheckpointStore
{
    Task<StellarUploadCheckpoint?> LoadAsync(string accountId, CancellationToken ct = default);
    Task SaveAsync(string accountId, StellarUploadCheckpoint checkpoint, CancellationToken ct = default);
    Task ClearAsync(string accountId, CancellationToken ct = default);
}
