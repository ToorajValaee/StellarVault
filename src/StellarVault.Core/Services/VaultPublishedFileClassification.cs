namespace StellarVault.Core.Services;

/// <summary>Strategy-neutral public record display classification. These checks
/// are only for presentation; ciphertext/authenticity still comes from the
/// original strategy-specific ledger verification and file reader.</summary>
public static class VaultPublishedFileClassification
{
    public static bool IsFile(LedgerRecordItem item) =>
        !item.IsFolder &&
        (StellarLedgerFileProtocol.IsFile(item) ||
         VaultLinkedTreeFilePolicy.IsRootRecord(item));

    public static bool IsInternalNode(LedgerRecordItem item) =>
        !item.IsFolder &&
        (item.Content?.StartsWith(StellarLedgerFileProtocol.ChunkPrefix,
             StringComparison.Ordinal) == true ||
         (item.Content?.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
              StringComparison.Ordinal) == true &&
          !VaultLinkedTreeFilePolicy.IsRootRecord(item)));
}
