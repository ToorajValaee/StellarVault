namespace StellarVault.Core.Services;

/// <summary>
/// Strategy-neutral display classification. File protocol markers are recognized
/// even when their contents are malformed: invalid manifests MUST NOT be
/// offered through ordinary record Edit/Delete/Export actions.
/// A verified file reader remains responsible for authentication and integrity.
/// </summary>
public static class VaultPublishedFileClassification
{
    public static bool IsManagedFileProtocolRecord(LedgerRecordItem item) =>
        item.Content is { } content &&
        (content.StartsWith(StellarLedgerFileProtocol.SinglePrefix,
             StringComparison.Ordinal) ||
         content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix,
             StringComparison.Ordinal) ||
         content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix,
             StringComparison.Ordinal) ||
         content.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
             StringComparison.Ordinal));

    public static bool IsFile(LedgerRecordItem item) =>
        !item.IsFolder &&
        (item.Content?.StartsWith(StellarLedgerFileProtocol.SinglePrefix,
             StringComparison.Ordinal) == true ||
         item.Content?.StartsWith(StellarLedgerFileProtocol.ManifestPrefix,
             StringComparison.Ordinal) == true ||
         VaultLinkedTreeFilePolicy.IsRootRecord(item));

    public static bool IsInternalNode(LedgerRecordItem item) =>
        !item.IsFolder &&
        (item.Content?.StartsWith(StellarLedgerFileProtocol.ChunkPrefix,
             StringComparison.Ordinal) == true ||
         (item.Content?.StartsWith(VaultLinkedTreeFilePolicy.Prefix,
              StringComparison.Ordinal) == true &&
          !VaultLinkedTreeFilePolicy.IsRootRecord(item)));
}
