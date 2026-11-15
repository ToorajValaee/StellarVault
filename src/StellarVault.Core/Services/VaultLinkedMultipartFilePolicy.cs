using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Linked S-key SVFILE2 staged part/manifest validation. Each part is still an
/// independent record with its own fresh fee approval and historical XDR proof.
/// The manifest may only be submitted after every exact referenced part is
/// confirmed and present in the remote snapshot. Never automates publishing.
/// </summary>
public static class VaultLinkedMultipartFilePolicy
{
    // Conservative limit below the atomic skh/skc transaction ceiling for the
    // final manifest. The general SVFILE2 codec supports larger *HD* uploads;
    // a different manifest hierarchy is needed for larger linked S-key files.
    public const int MaxParts = 48;
    public const int MaxBytes = MaxParts * StellarLedgerFileProtocol.MaxChunkBytes;

    public static IReadOnlyList<(string Id, LedgerRecordDraft Draft)> BuildUpload(
        string title, string filename, string contentType, ReadOnlySpan<byte> bytes,
        string? folderId = null)
    {
        if (bytes.Length is <= StellarLedgerFileProtocol.MaxChunkBytes or > MaxBytes)
            throw new InvalidDataException(
                "Linked multipart files currently require 2049–98304 bytes.");
        var planned = StellarLedgerFileProtocol.BuildUpload(
            title, filename, contentType, bytes, folderId);
        if (planned.Count is < 3 or > MaxParts + 1)
            throw new InvalidDataException("Linked multipart file has unsupported part count.");
        for (int i = 0; i < planned.Count - 1; i++)
            if (!IsPart(planned[i].Draft, planned[^1].Id, i))
                throw new InvalidDataException("Linked multipart part failed validation.");
        if (!IsManifest(planned[^1].Draft))
            throw new InvalidDataException("Linked multipart manifest failed validation.");
        return planned;
    }

    public static bool IsPart(LedgerRecordDraft draft, string fileId, int index)
    {
        if (draft.IsFolder || draft.FolderId is not null ||
            draft.Tags is not ["svchunk-v2"] ||
            draft.Content is null ||
            !draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix,
                StringComparison.Ordinal))
            return false;
        byte[]? payload = null;
        try
        {
            var part = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Part>(
                draft.Content[StellarLedgerFileProtocol.ChunkPrefix.Length..]);
            if (part is null || part.FileId != fileId ||
                part.Index != index || part.Index < 0 || part.Index >= MaxParts ||
                part.Sha256Hex is not { Length: 64 } ||
                !part.Sha256Hex.All(Uri.IsHexDigit))
                return false;
            payload = Convert.FromBase64String(part.DataBase64);
            return payload.Length is >= 1 and <= StellarLedgerFileProtocol.MaxChunkBytes &&
                string.Equals(Convert.ToHexString(SHA256.HashData(payload)),
                    part.Sha256Hex, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static bool IsManifest(LedgerRecordDraft draft)
    {
        if (draft.IsFolder || draft.Tags is not ["file", "svfile-v2"] ||
            draft.Content is null ||
            !draft.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix,
                StringComparison.Ordinal))
            return false;
        try
        {
            var manifest = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Manifest>(
                draft.Content[StellarLedgerFileProtocol.ManifestPrefix.Length..]);
            return manifest is not null &&
                manifest.Length is > StellarLedgerFileProtocol.MaxChunkBytes and <= MaxBytes &&
                manifest.PartIds is { Length: >= 2 and <= MaxParts } ids &&
                ids.Length == (manifest.Length + StellarLedgerFileProtocol.MaxChunkBytes - 1) /
                    StellarLedgerFileProtocol.MaxChunkBytes &&
                ids.All(id => id is { Length: 16 } && id.All(Uri.IsHexDigit)) &&
                ids.Distinct(StringComparer.Ordinal).Count() == ids.Length &&
                manifest.Sha256Hex is { Length: 64 } &&
                manifest.Sha256Hex.All(Uri.IsHexDigit);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsTypedFileDraft(LedgerRecordDraft draft) =>
        IsManifest(draft) || draft.Content is not null &&
            draft.Content.StartsWith(
                StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal) &&
            draft.Tags is ["svchunk-v2"];

    public static string[] Problems(VaultPendingMutation mutation,
        IReadOnlyList<VaultPendingMutation> all,
        IReadOnlyList<LedgerRecordItem>? remote = null)
    {
        var normal = VaultLinkedSingleFilePolicy.Problems(mutation, all);
        if (!normal.Contains("file-protocol-needs-staged-review", StringComparer.Ordinal) ||
            mutation.OriginStrategyId != VaultMutationStrategyPolicy.Linked ||
            mutation.Kind != VaultMutationKind.CreateRecord)
            return normal;
        LedgerRecordDraft? draft;
        try { draft = JsonSerializer.Deserialize<LedgerRecordDraft>(mutation.EncryptedPayload); }
        catch (JsonException) { return normal; }
        if (draft is null) return normal;
        if (draft.Content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix,
                StringComparison.Ordinal))
        {
            StellarLedgerFileProtocol.Part? part;
            try
            {
                part = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Part>(
                    draft.Content[StellarLedgerFileProtocol.ChunkPrefix.Length..]);
            }
            catch (JsonException) { return normal; }
            if (part is null || !IsPart(draft, part.FileId, part.Index))
                return normal;

            var manifest = FindManifest(all, part.FileId);
            if (manifest is null || part.Index >= manifest.PartIds.Length ||
                manifest.PartIds[part.Index] != mutation.EntityId)
                return ["multipart-manifest-missing-or-invalid"];
            for (int i = 0; i < part.Index; i++)
            {
                var prior = FindExactPart(all, manifest.PartIds[i], part.FileId, i);
                if (prior is null || prior.State != VaultSyncState.Confirmed)
                    return ["multipart-previous-part-not-confirmed"];
            }
            return normal.Where(x => x != "file-protocol-needs-staged-review").ToArray();
        }
        if (!IsManifest(draft)) return normal;
        var metadata = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Manifest>(
            draft.Content[StellarLedgerFileProtocol.ManifestPrefix.Length..])!;
        foreach (var (partId, index) in metadata.PartIds.Select((id, i) => (id, i)))
        {
            var prior = FindExactPart(all, partId, mutation.EntityId, index);
            if (prior is null || prior.State != VaultSyncState.Confirmed)
                return ["multipart-all-parts-must-be-historically-confirmed"];
        }
        // Advisory read-only planner has no remote snapshot. The publisher
        // repeats this exact policy with a fresh snapshot before fee quoting
        // and again immediately before any durable submission.
        if (remote is null)
            return normal.Where(x => x != "file-protocol-needs-staged-review").ToArray();
        try
        {
            var candidate = new LedgerRecordItem(mutation.EntityId, draft.Title,
                draft.Content, draft.FolderId, draft.Tags, draft.IsFolder,
                draft.UserName, draft.Url, draft.Notes);
            var decoded = StellarLedgerFileProtocol.DecodeFile(candidate, remote);
            try
            {
                if (decoded.Length != metadata.Length)
                    return ["multipart-onchain-digest-mismatch"];
            }
            finally { CryptographicOperations.ZeroMemory(decoded); }
        }
        catch
        {
            return ["multipart-remote-parts-missing-or-invalid"];
        }
        return normal.Where(x => x != "file-protocol-needs-staged-review").ToArray();
    }

    private static StellarLedgerFileProtocol.Manifest? FindManifest(
        IReadOnlyList<VaultPendingMutation> entries, string fileId)
    {
        var items = entries.Where(x => x.EntityId == fileId &&
            x.Kind == VaultMutationKind.CreateRecord &&
            x.OriginStrategyId == VaultMutationStrategyPolicy.Linked).ToArray();
        if (items.Length != 1) return null;
        try
        {
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(
                items[0].EncryptedPayload);
            if (draft is null || !IsManifest(draft)) return null;
            return JsonSerializer.Deserialize<StellarLedgerFileProtocol.Manifest>(
                draft.Content[StellarLedgerFileProtocol.ManifestPrefix.Length..]);
        }
        catch (JsonException) { return null; }
    }

    private static VaultPendingMutation? FindExactPart(
        IReadOnlyList<VaultPendingMutation> entries, string id, string fileId, int index)
    {
        var matches = entries.Where(x => x.EntityId == id &&
            x.Kind == VaultMutationKind.CreateRecord &&
            x.OriginStrategyId == VaultMutationStrategyPolicy.Linked).ToArray();
        if (matches.Length != 1) return null;
        try
        {
            var draft = JsonSerializer.Deserialize<LedgerRecordDraft>(
                matches[0].EncryptedPayload);
            return draft is not null && IsPart(draft, fileId, index)
                ? matches[0] : null;
        }
        catch (JsonException) { return null; }
    }
}
