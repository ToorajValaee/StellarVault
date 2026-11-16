using System.Security.Cryptography;
using System.Text.Json;

namespace VaultEngine.Stellar.Services;

/// <summary>
/// Explicit, S-key-only file capability. Single SVFILE1 records use the same
/// encrypted linked record journal and verified atomic transaction as ordinary
/// creates. Multipart SVFILE2 requires a separate durable multi-transaction
/// protocol and is deliberately NOT supported here.
/// </summary>
public static class VaultLinkedSingleFilePolicy
{
    public const int MaxBytes = StellarLedgerFileProtocol.MaxChunkBytes;

    public static LedgerRecordDraft BuildDraft(
        string title, string filename, string contentType,
        ReadOnlySpan<byte> content, string? parentFolder)
    {
        if (content.Length is < 1 or > MaxBytes)
            throw new InvalidDataException(
                "A single linked encrypted file must contain 1–2048 bytes.");
        var prepared = StellarLedgerFileProtocol.BuildUpload(
            title, filename, contentType, content, parentFolder);
        if (prepared.Count != 1 ||
            !IsSupportedDraft(prepared[0].Draft))
            throw new InvalidDataException("Only a single verified SVFILE1 record may be staged.");
        return prepared[0].Draft;
    }

    public static bool IsSupportedDraft(LedgerRecordDraft draft)
    {
        if (draft.IsFolder || draft.Content is null ||
            !draft.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix,
                StringComparison.Ordinal) ||
            draft.Tags is not ["file", "svfile-v1"] ||
            string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 256)
            return false;
        byte[]? result = null;
        try
        {
            var metadata = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Single>(
                draft.Content[StellarLedgerFileProtocol.SinglePrefix.Length..]);
            if (metadata is null ||
                metadata.Sha256Hex is not { Length: 64 } checksum ||
                !checksum.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ||
                metadata.ContentType is not { Length: > 0 and <= 128 } mime ||
                !mime.All(c => c is >= '!' and <= '~'))
                return false;
            var item = new LedgerRecordItem("0000000000000000",
                draft.Title, draft.Content, draft.FolderId, draft.Tags);
            result = StellarLedgerFileProtocol.DecodeFile(item, [item]);
            return result.Length is >= 1 and <= MaxBytes;
        }
        catch { return false; }
        finally
        {
            if (result is not null) CryptographicOperations.ZeroMemory(result);
        }
    }

    public static string[] Problems(VaultPendingMutation mutation,
        IReadOnlyList<VaultPendingMutation> entries)
    {
        var original = VaultQueuedCreatePolicy.Problems(mutation, entries);
        if (!original.Contains("file-protocol-needs-staged-review",
                StringComparer.Ordinal))
            return original;
        if (mutation.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
            mutation.Kind != VaultMutationKind.CreateRecord)
            return original;
        try
        {
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(
                mutation.EncryptedPayload);
            if (draft is null || !IsSupportedDraft(draft))
                return original;
            return original.Where(x =>
                x != "file-protocol-needs-staged-review").ToArray();
        }
        catch (JsonException)
        {
            return original;
        }
    }
}
