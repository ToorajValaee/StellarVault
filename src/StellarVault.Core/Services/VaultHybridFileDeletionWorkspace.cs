using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Shared PWA/MAUI staging and read-only review of complete remote file deletion.
/// No deletion transaction is sent here: multipart tombstones require separate
/// journaled, authenticated publication, which must fail closed until implemented.
/// </summary>
public sealed class VaultHybridFileDeletionWorkspace(
    VaultPendingMutationQueue queue,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    VaultNetworkSelection networks,
    IStellarLedgerRecords ledger)
{
    private const int FormatVersion = 1;

    public sealed record FileDeletionEntry(string RecordId, LedgerRecordDraft Original);
    public sealed record FileDeletionIntent(int Version, string FileName,
        string Sha256, FileDeletionEntry[] Entries);
    public sealed record RemoteFile(string ManifestId, string Name, int RecordCount);
    public sealed record DeletionDraft(string MutationId, string ManifestId,
        string FileName, int RecordCount, VaultSyncState State, bool IsCorrupt,
        DateTimeOffset CreatedUtc);
    public sealed record DeletionInspection(string MutationId, bool AllEntriesUnchanged,
        int PresentEntries, int TotalEntries, string Explanation);

    private (string Account, string Scope, long Epoch) RequireScope()
    {
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId is not string account)
            throw new InvalidOperationException("Unlock the encrypted local vault.");
        return (account, networks.CredentialKey(account), sessions.Epoch);
    }

    private void CheckScope(string account, string scope, long epoch)
    {
        retention.RequireLocalPersistence();
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != account ||
            sessions.Epoch != epoch || networks.CredentialKey(account) != scope ||
            !ledger.IsConnected)
            throw new InvalidOperationException("Vault session, network or signing connection changed.");
    }

    private async Task<IReadOnlyList<LedgerRecordItem>> ReadBoundAsync(
        string account, string scope, long epoch, CancellationToken ct)
    {
        CheckScope(account, scope, epoch);
        if ((await ledger.InspectAccountInfoAsync()).RootAccountId != account)
            throw new UnauthorizedAccessException("Connected signing root does not match local vault.");
        var remote = await ledger.ReadAsync();
        ct.ThrowIfCancellationRequested();
        CheckScope(account, scope, epoch);
        if ((await ledger.InspectAccountInfoAsync()).RootAccountId != account)
            throw new UnauthorizedAccessException("Connected signing root changed.");
        if (remote.Any(x => x is null || x.Id is not { Length: 16 } ||
                !x.Id.All(Uri.IsHexDigit)) ||
            remote.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() != 1))
            throw new InvalidDataException("Ambiguous ledger record IDs block file deletion.");
        return remote;
    }

    private static FileDeletionIntent CaptureIntent(
        LedgerRecordItem file, IReadOnlyList<LedgerRecordItem> remote)
    {
        if (!StellarLedgerFileProtocol.IsFile(file) || file.IsFolder)
            throw new InvalidOperationException("Select a published file manifest.");
        string[] ids;
        if (file.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal))
            ids = [file.Id];
        else
        {
            var manifest = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Manifest>(
                file.Content[StellarLedgerFileProtocol.ManifestPrefix.Length..])
                ?? throw new InvalidDataException("Missing file manifest.");
            if (manifest.PartIds is null || manifest.PartIds.Length == 0 ||
                manifest.PartIds.Length > StellarLedgerFileProtocol.MaxParts ||
                manifest.PartIds.Any(x => x is not { Length: 16 } || !x.All(Uri.IsHexDigit)) ||
                manifest.PartIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.PartIds.Length ||
                manifest.PartIds.Contains(file.Id, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Malformed file chunk references.");
            ids = [file.Id, .. manifest.PartIds];
        }
        var selected = ids.Select(id => remote.Single(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray();
        // Never delete chunks another manifest currently references.
        var owned = ids.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in remote.Where(x => x.Id != file.Id &&
                     x.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal)))
        {
            StellarLedgerFileProtocol.Manifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Manifest>(
                    other.Content[StellarLedgerFileProtocol.ManifestPrefix.Length..]);
            }
            catch (JsonException) { throw new InvalidDataException("An unrelated file manifest is invalid; deletion blocked."); }
            if (manifest?.PartIds is null)
                throw new InvalidDataException("An unrelated file manifest cannot be decoded.");
            if (manifest.PartIds.Any(owned.Contains))
                throw new InvalidOperationException("File shares a part with another manifest; deletion blocked.");
        }
        byte[] decoded = StellarLedgerFileProtocol.DecodeFile(file, remote);
        try
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(decoded));
            var entries = selected.Select(x => new FileDeletionEntry(x.Id,
                new LedgerRecordDraft(x.Title, x.Content, x.FolderId,
                    x.Tags?.ToArray(), x.IsFolder, x.UserName, x.Url, x.Notes))).ToArray();
            return new(FormatVersion, StellarLedgerFileProtocol.FileName(file),
                fingerprint, entries);
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    private static FileDeletionIntent ReadIntent(VaultPendingMutation mutation)
    {
        FileDeletionIntent intent;
        try
        {
            intent = JsonSerializer.Deserialize<FileDeletionIntent>(mutation.EncryptedPayload)
                ?? throw new InvalidDataException("File deletion intent is empty.");
        }
        catch (JsonException ex) { throw new InvalidDataException("File deletion intent is corrupt.", ex); }
        if (mutation.Kind != VaultMutationKind.DeleteFile ||
            intent.Version != FormatVersion || intent.Entries is not { Length: > 0 and <= StellarLedgerFileProtocol.MaxParts + 1 } ||
            intent.Sha256 is not { Length: 64 } || !intent.Sha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(intent.FileName) ||
            intent.Entries.Any(x => x is null || x.RecordId is not { Length: 16 } ||
                !x.RecordId.All(Uri.IsHexDigit) || x.Original is null ||
                string.IsNullOrEmpty(x.Original.Content)) ||
            intent.Entries.Select(x => x.RecordId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != intent.Entries.Length ||
            !intent.Entries[0].RecordId.Equals(mutation.EntityId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid complete-file deletion inventory.");
        StellarLedgerFileProtocol.ValidateName(intent.FileName);
        if (!intent.Entries[0].Original.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) &&
            !intent.Entries[0].Original.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Deletion target is not a file manifest.");
        if (intent.Entries[0].Original.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) &&
            intent.Entries.Length != 1)
            throw new InvalidDataException("Single-record file has unexpected chunks.");
        if (intent.Entries[0].Original.Content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal))
        {
            StellarLedgerFileProtocol.Manifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<StellarLedgerFileProtocol.Manifest>(
                    intent.Entries[0].Original.Content[StellarLedgerFileProtocol.ManifestPrefix.Length..])
                    ?? throw new InvalidDataException("Deletion manifest missing.");
            }
            catch (JsonException ex) { throw new InvalidDataException("Deletion manifest invalid.", ex); }
            if (manifest.PartIds is null || manifest.PartIds.Length != intent.Entries.Length - 1 ||
                manifest.PartIds.Where((id, i) =>
                    !string.Equals(id, intent.Entries[i + 1].RecordId,
                        StringComparison.OrdinalIgnoreCase)).Any())
                throw new InvalidDataException("Deletion parts do not match manifest order.");
        }
        var snapshot = intent.Entries.Select(x => new LedgerRecordItem(x.RecordId,
            x.Original.Title, x.Original.Content, x.Original.FolderId,
            x.Original.Tags, x.Original.IsFolder, x.Original.UserName,
            x.Original.Url, x.Original.Notes)).ToArray();
        var bytes = StellarLedgerFileProtocol.DecodeFile(snapshot[0], snapshot);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(intent.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
                StellarLedgerFileProtocol.FileName(snapshot[0]) != intent.FileName)
                throw new InvalidDataException("Deletion snapshot fingerprint differs.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return intent;
    }

    private static bool SameRecord(LedgerRecordItem actual, FileDeletionEntry expected)
    {
        var old = expected.Original;
        return actual.Id.Equals(expected.RecordId, StringComparison.OrdinalIgnoreCase) &&
            actual.Title == old.Title && actual.Content == old.Content &&
            actual.FolderId == old.FolderId && actual.IsFolder == old.IsFolder &&
            actual.UserName == old.UserName && actual.Url == old.Url &&
            actual.Notes == old.Notes &&
            (actual.Tags ?? []).SequenceEqual(old.Tags ?? [], StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<RemoteFile>> BrowseAsync(CancellationToken ct = default)
    {
        var (account, scope, epoch) = RequireScope();
        var remote = await ReadBoundAsync(account, scope, epoch, ct);
        return remote.Where(x => !x.IsFolder && StellarLedgerFileProtocol.IsFile(x))
            .Select(x => new RemoteFile(x.Id, StellarLedgerFileProtocol.FileName(x),
                x.Content.StartsWith(StellarLedgerFileProtocol.SinglePrefix,
                    StringComparison.Ordinal) ? 1 : -1))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DeletionDraft> StageDeleteAsync(string manifestId, CancellationToken ct = default)
    {
        if (manifestId is not { Length: 16 } || !manifestId.All(Uri.IsHexDigit))
            throw new ArgumentException("Select a valid file manifest ID.", nameof(manifestId));
        var (account, scope, epoch) = RequireScope();
        var remote = await ReadBoundAsync(account, scope, epoch, ct);
        var file = remote.SingleOrDefault(x => x.Id.Equals(manifestId,
            StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Published file does not exist.");
        var intent = CaptureIntent(file, remote);
        var queueEntries = await queue.ReviewAllAsync(account, scope, ct);
        var targeted = intent.Entries.Select(x => x.RecordId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (queueEntries.Any(x => x.State is not (VaultSyncState.Confirmed or VaultSyncState.Failed) &&
                (targeted.Contains(x.EntityId) ||
                 x.Kind == VaultMutationKind.UploadFile && x.EntityId.Equals(manifestId, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("An active queue mutation targets this file or one of its chunks.");
        }
        finally
        {
            foreach (var entry in queueEntries)
                CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent);
        try
        {
            if (payload.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("File deletion intent exceeds encrypted queue limit.");
            var latestRemote = await ReadBoundAsync(account, scope, epoch, ct);
            var same = latestRemote.Count == remote.Count &&
                intent.Entries.All(expected => latestRemote.Any(x => SameRecord(x, expected)));
            if (!same) throw new InvalidOperationException("File changed while staging its deletion.");
            var latestQueue = await queue.ReviewAllAsync(account, scope, ct);
            try
            {
                if (latestQueue.Any(x => x.State is not (VaultSyncState.Confirmed or VaultSyncState.Failed) &&
                    targeted.Contains(x.EntityId)))
                    throw new InvalidOperationException("Concurrent queue operation targets the file.");
            }
            finally
            {
                foreach (var entry in latestQueue)
                    CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
            }
            CheckScope(account, scope, epoch);
            var mutation = VaultPendingMutation.Create(account, scope,
                VaultMutationKind.DeleteFile, file.Id, payload);
            try
            {
                await queue.EnqueueAsync(mutation, ct);
                CheckScope(account, scope, epoch);
                return new(mutation.Id, file.Id, intent.FileName,
                    intent.Entries.Length, VaultSyncState.Pending, false, mutation.CreatedUtc);
            }
            finally { CryptographicOperations.ZeroMemory(mutation.EncryptedPayload); }
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public async Task<IReadOnlyList<DeletionDraft>> ListAsync(CancellationToken ct = default)
    {
        var (account, scope, _) = RequireScope();
        var all = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            return all.Where(x => x.Kind == VaultMutationKind.DeleteFile)
                .Select(x =>
                {
                    try
                    {
                        var intent = ReadIntent(x);
                        return new DeletionDraft(x.Id, x.EntityId, intent.FileName,
                            intent.Entries.Length, x.State, false, x.CreatedUtc);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException
                                               or FormatException or ArgumentException)
                    {
                        return new DeletionDraft(x.Id, x.EntityId, "UNREADABLE FILE DELETION",
                            0, x.State, true, x.CreatedUtc);
                    }
                }).OrderBy(x => x.CreatedUtc).ToArray();
        }
        finally
        {
            foreach (var entry in all)
                CryptographicOperations.ZeroMemory(entry.EncryptedPayload);
        }
    }

    public async Task<DeletionInspection> InspectAsync(string mutationId, CancellationToken ct = default)
    {
        var (account, scope, epoch) = RequireScope();
        var all = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            var mutation = all.SingleOrDefault(x => x.Id == mutationId &&
                x.Kind == VaultMutationKind.DeleteFile)
                ?? throw new KeyNotFoundException("File deletion draft not found.");
            var intent = ReadIntent(mutation);
            var remote = await ReadBoundAsync(account, scope, epoch, ct);
            var matches = intent.Entries.Count(x => remote.Any(y => SameRecord(y, x)));
            var present = intent.Entries.Count(x => remote.Any(y =>
                y.Id.Equals(x.RecordId, StringComparison.OrdinalIgnoreCase)));
            var stillSafe = matches == intent.Entries.Length && present == matches;
            var latest = await queue.ReviewAllAsync(account, scope, ct);
            try
            {
                var current = latest.SingleOrDefault(x => x.Id == mutationId);
                if (current is null || current.State != mutation.State ||
                    !current.EncryptedPayload.AsSpan().SequenceEqual(mutation.EncryptedPayload) ||
                    !current.DependsOn.SequenceEqual(mutation.DependsOn, StringComparer.Ordinal))
                    throw new InvalidOperationException("Deletion intent changed during inspection.");
                CheckScope(account, scope, epoch);
                return new(mutationId, stillSafe, present, intent.Entries.Length,
                    stillSafe
                        ? "All original file records are currently present and unchanged. This is read-only and NOT deletion approval."
                        : "One or more file records are changed or missing. Preserve the local intent; do not publish deletion.");
            }
            finally
            {
                foreach (var item in latest)
                    CryptographicOperations.ZeroMemory(item.EncryptedPayload);
            }
        }
        finally
        {
            foreach (var item in all)
                CryptographicOperations.ZeroMemory(item.EncryptedPayload);
        }
    }

    public async Task DiscardAsync(string mutationId, CancellationToken ct = default)
    {
        var (account, scope, epoch) = RequireScope();
        var all = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            if (!all.Any(x => x.Id == mutationId && x.Kind == VaultMutationKind.DeleteFile &&
                x.State == VaultSyncState.Pending))
                throw new InvalidOperationException("Only untouched file deletion drafts can be discarded.");
            await queue.DiscardPendingAsync(account, scope, mutationId, ct);
            if (sessions.Epoch != epoch) throw new InvalidOperationException("Session changed after discard.");
        }
        finally
        {
            foreach (var item in all)
                CryptographicOperations.ZeroMemory(item.EncryptedPayload);
        }
    }
}
