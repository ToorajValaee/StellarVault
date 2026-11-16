using System.Security.Cryptography;
using VaultEngine.Stellar.Services;

/// <summary>
/// A signed transaction is not a ledger receipt. This exercises the pure
/// read-only recovery journal policy without a signer, network or disk store.
/// </summary>
internal static class VaultRecoveryCheckpointSmoke
{
    private const string Root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var payload = new byte[] { 1 };
        try
        {
            var original = VaultPendingMutation.Create(
                Root, "testnet", VaultMutationKind.DeleteRecord,
                "0123456789abcdef", payload);
            var hash1 = new string('a', 64);
            var hash2 = new string('b', 64);
            var uncertain = original with
            {
                State = VaultSyncState.Uncertain,
                TransactionHash = null,
                SignedDeletionManifests =
                [
                    new VaultSignedDeletionManifest(Root, hash1)
                ]
            };
            var report = VaultPendingMutationQueue.DescribeRecovery(uncertain);
            Check(report.NeedsInvestigation && report.CheckpointJournalValid &&
                report.TransactionHash is null &&
                report.Checkpoints is [var checkpoint] &&
                checkpoint.Kind == "deletion-manifest" &&
                checkpoint.TargetId == Root &&
                checkpoint.TransactionHash == hash1 &&
                report.Guidance.Contains("Never resubmit", StringComparison.OrdinalIgnoreCase),
                "Per-owner signed tombstone identity was lost or treated as confirmation.");

            var duplicate = uncertain with
            {
                SignedDeletionManifests =
                [
                    new VaultSignedDeletionManifest(Root, hash1),
                    new VaultSignedDeletionManifest(Root, hash2)
                ]
            };
            var ambiguous = VaultPendingMutationQueue.DescribeRecovery(duplicate);
            Check(!ambiguous.CheckpointJournalValid &&
                ambiguous.Checkpoints.Length == 0 &&
                ambiguous.NeedsInvestigation,
                "Duplicate owner tombstones became trusted recovery evidence.");

            var invalidKind = uncertain with { Kind = VaultMutationKind.CreateRecord };
            Check(!VaultPendingMutationQueue.DescribeRecovery(invalidKind).CheckpointJournalValid,
                "A signed deletion checkpoint was accepted on a creation intent.");

            var invalidHash = uncertain with { TransactionHash = "not-a-signed-hash" };
            Check(!VaultPendingMutationQueue.DescribeRecovery(invalidHash).CheckpointJournalValid,
                "Malformed primary transaction hash was silently erased from recovery findings.");

            var upload = original with
            {
                Kind = VaultMutationKind.UploadFile,
                State = VaultSyncState.Submitting,
                SignedFileEntries =
                [
                    new VaultSignedFileEntry("0123456789abcdef", hash1),
                    new VaultSignedFileEntry("fedcba9876543210", hash2)
                ]
            };
            var multipart = VaultPendingMutationQueue.DescribeRecovery(upload);
            Check(multipart.NeedsInvestigation && multipart.CheckpointJournalValid &&
                multipart.Checkpoints.Length == 2 &&
                multipart.Checkpoints.All(x => x.Kind == "file-node") &&
                multipart.TransactionHash is null,
                "Multipart file checkpoints did not survive read-only recovery inspection.");

            var brokenFile = upload with
            {
                SignedFileEntries =
                [
                    new VaultSignedFileEntry("0123456789abcdef", hash1),
                    new VaultSignedFileEntry("0123456789abcdef", hash2)
                ]
            };
            Check(!VaultPendingMutationQueue.DescribeRecovery(brokenFile).CheckpointJournalValid,
                "Duplicate file node transaction identities were trusted.");

            var normal = VaultPendingMutationQueue.DescribeRecovery(original);
            Check(!normal.NeedsInvestigation && normal.Checkpoints.Length == 0,
                "Unsubmitted pending draft was incorrectly labeled as a network submission.");

            Console.WriteLine("PASS: signed recovery checkpoint inventory never implies network confirmation");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
