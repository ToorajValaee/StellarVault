using StellarVault.Core.Services;

/// <summary>
/// Executable regression for UI file-protocol classification. Malformed or
/// truncated file envelopes must stay protected from ordinary edit/delete.
/// This does not validate the integrity of a file's decrypted contents.
/// </summary>
internal static class VaultPublishedFileClassificationSmoke
{
    public static Task RunAsync()
    {
        static LedgerRecordItem Record(string content) =>
            new("0123456789abcdef", "Stored item", content, null, null,
                false, null, null, null);

        var ordinary = Record("regular encrypted record content");
        Check(!VaultPublishedFileClassification.IsManagedFileProtocolRecord(ordinary),
            "Ordinary record was marked as file protocol.");
        Check(!VaultPublishedFileClassification.IsFile(ordinary),
            "Ordinary record was displayed as a file.");

        foreach (var marker in new[]
        {
            StellarLedgerFileProtocol.SinglePrefix,
            StellarLedgerFileProtocol.ManifestPrefix
        })
        {
            // A damaged manifest is still a file, never an editable credential.
            var malformed = Record(marker + "{broken");
            Check(VaultPublishedFileClassification.IsManagedFileProtocolRecord(malformed),
                "Malformed published file was not protected from ordinary mutation.");
            Check(VaultPublishedFileClassification.IsFile(malformed),
                "Malformed published file was exposed as an ordinary record.");
            Check(!VaultPublishedFileClassification.IsInternalNode(malformed),
                "File manifest unexpectedly disappeared from the explorer.");
        }

        var chunk = Record(StellarLedgerFileProtocol.ChunkPrefix + "{broken");
        Check(VaultPublishedFileClassification.IsManagedFileProtocolRecord(chunk) &&
            VaultPublishedFileClassification.IsInternalNode(chunk) &&
            !VaultPublishedFileClassification.IsFile(chunk),
            "Malformed file chunk was exposed as an ordinary record.");

        var tree = Record(VaultLinkedTreeFilePolicy.Prefix + "{broken");
        Check(VaultPublishedFileClassification.IsManagedFileProtocolRecord(tree) &&
            VaultPublishedFileClassification.IsInternalNode(tree),
            "Malformed file-tree node was exposed for ordinary deletion.");

        Console.WriteLine("PASS: file-protocol prefixes stay protected even when metadata is damaged");
        return Task.CompletedTask;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
