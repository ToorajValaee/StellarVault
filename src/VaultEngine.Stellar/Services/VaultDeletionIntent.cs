namespace VaultEngine.Stellar.Services;

/// <summary>
/// Encrypted, immutable deletion intent. Captures a complete (but time-of-read)
/// set of HD manifest-owning children and the original decrypted record.
/// The snapshot is NOT an atomic ledger precondition or historical proof.
/// </summary>
public sealed record VaultDeletionIntent(
    LedgerRecordDraft Original,
    string[] ManifestSourceAccounts);

/// <summary>
/// Host adapter for deriving all currently visible HD manifest-owner accounts
/// from one authenticated connected ledger read, not from user input.
/// </summary>
public interface IVaultLedgerManifestCopyInventory
{
    Task<IReadOnlyList<string>> InspectOwnersAsync(
        string rootAccountId, string recordId, CancellationToken ct = default);
}
