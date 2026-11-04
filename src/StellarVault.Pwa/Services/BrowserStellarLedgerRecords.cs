using Microsoft.JSInterop;
using StellarVault.Core.Services;
using StellarVaultPwa.Core;
using StellarVaultPwa.Services;

namespace StellarVault.Pwa.Services;

/// <summary>HD-v2 ledger path using the original transaction writer and manifest
/// format. Never silently falls back to IndexedDB when submission fails.</summary>
public sealed class BrowserStellarLedgerRecords(IJSRuntime js) : IStellarLedgerRecords, IVaultLedgerManifestCopyInventory,
    IVaultGuardedLedgerMutations, IVaultPreparedCreateLedger,
    IVaultPreparedUpdateLedger, IVaultPreparedDeletionLedger, IAsyncDisposable
{
    private StellarVaultStore? _store;
    public bool IsConnected => _store is not null;
    private StellarVaultStore RequireStore() =>
        _store ?? throw new InvalidOperationException("Connect a funded Stellar root first.");

    public async Task ConnectAsync(string mnemonic, string horizonUrl, string networkPassphrase)
    {
        if (_store is not null) throw new InvalidOperationException("A ledger session is already connected.");
        if (string.IsNullOrWhiteSpace(mnemonic) || string.IsNullOrWhiteSpace(networkPassphrase))
            throw new ArgumentException("Mnemonic and network passphrase are required.");
        var info = await js.InvokeAsync<HorizonNetworkInfo>(
            "stellarVault.network.inspectHorizon", horizonUrl.TrimEnd('/'));
        if (!string.Equals(info.NetworkPassphrase, networkPassphrase, StringComparison.Ordinal))
            throw new InvalidOperationException("Horizon network passphrase changed or does not match the selected network.");
        if (info.BaseReserveStroops <= 0)
            throw new InvalidOperationException("Horizon did not report a valid base reserve.");
        uint fee = Math.Max(info.RecommendedBaseFeeStroops, info.MinimumBaseFeeStroops);
        if (fee == 0)
            throw new InvalidOperationException("Horizon did not report a valid network fee.");
        var settings = new VaultNetworkSettings("Selected network", horizonUrl, networkPassphrase,
            "0", 80, fee, info.BaseReserveStroops);
        var candidate = new StellarVaultStore(settings, js);
        try
        {
            await candidate.OpenAsync(mnemonic);
            _store = candidate;
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Check a receipt-validated historic registry envelope with the current
    /// connected seed and selected network. No transaction or unlock occurs.
    /// </summary>
    public async Task<bool> CheckHistoricalRegistryAsync(
        string expectedRoot, string expectedChild, int childIndex,
        string expectedHorizonUrl, string expectedPassphrase,
        byte[] envelope, CancellationToken ct = default)
    {
        if (!IsConnected || envelope is not { Length: 50 } ||
            !Uri.TryCreate(expectedHorizonUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0)
            return false;
        try
        {
            var store = RequireStore();
            if (!string.Equals(store.RootAccountId, expectedRoot, StringComparison.Ordinal) ||
                !string.Equals(store.Settings.Passphrase, expectedPassphrase, StringComparison.Ordinal) ||
                !Uri.TryCreate(store.Settings.NormalizedHorizonUrl, UriKind.Absolute, out var connected) ||
                Uri.Compare(connected, endpoint, UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            return await store.CheckHistoricalRegistryAsync(
                expectedRoot, expectedChild, childIndex, envelope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Decrypt and compare the exact ciphertext supplied by a successful
    /// historical proof, using only the already-connected HD session.
    /// The caller must clear the ciphertext after this method returns.
    /// </summary>
    public async Task<bool> CompareHistoricalEncryptedRecordAsync(
        string expectedRootAccount, string expectedNodeAccount, int nodeIndex,
        string expectedHorizonUrl, string expectedNetworkPassphrase,
        string recordId, string generation, byte[] ciphertext,
        LedgerRecordDraft approved, CancellationToken ct = default)
    {
        if (!IsConnected || approved is null ||
            string.IsNullOrWhiteSpace(expectedNetworkPassphrase) ||
            !Uri.TryCreate(expectedHorizonUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            return false;
        try
        {
            ct.ThrowIfCancellationRequested();
            var store = RequireStore();
            if (!string.Equals(store.RootAccountId, expectedRootAccount, StringComparison.Ordinal) ||
                !string.Equals(store.Settings.Passphrase, expectedNetworkPassphrase, StringComparison.Ordinal) ||
                !Uri.TryCreate(store.Settings.NormalizedHorizonUrl, UriKind.Absolute, out var sessionEndpoint) ||
                Uri.Compare(sessionEndpoint, endpoint, UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            return await store.CompareHistoricalHdRecordAsync(
                expectedRootAccount, expectedNodeAccount, nodeIndex,
                recordId, generation, ciphertext, approved, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Read-only comparison of the decrypted CURRENT HD-v2 record against a
    /// caller-supplied approved draft. No plaintext is returned in the finding.
    /// Requires an already-open signing session; never unlocks or publishes.
    /// </summary>
    public async Task<ConnectedVaultRecordFinding> InspectConnectedRecordAsync(
        string expectedRootAccount, string expectedNodeAccount, int expectedNodeIndex,
        string expectedHorizonUrl, string expectedNetworkPassphrase,
        string recordId, LedgerRecordDraft expected, CancellationToken ct = default)
    {
        static ConnectedVaultRecordFinding Reject(string reason) => new(false, reason);
        if (!IsConnected || expected is null ||
            expectedRootAccount is not { Length: 56 } ||
            expectedNodeAccount is not { Length: 56 } ||
            expectedNodeIndex is <= 0 or > 64 ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(expectedNetworkPassphrase) ||
            !Uri.TryCreate(expectedHorizonUrl, UriKind.Absolute, out var expectedEndpoint) ||
            expectedEndpoint.Scheme != Uri.UriSchemeHttps ||
            expectedEndpoint.UserInfo.Length != 0 ||
            expectedEndpoint.Query.Length != 0 || expectedEndpoint.Fragment.Length != 0)
            return Reject("Invalid or disconnected HD-v2 comparison inputs.");

        try
        {
            ct.ThrowIfCancellationRequested();
            var store = RequireStore();
            if (!string.Equals(store.RootAccountId, expectedRootAccount, StringComparison.Ordinal) ||
                !string.Equals(store.Settings.Passphrase, expectedNetworkPassphrase, StringComparison.Ordinal) ||
                !Uri.TryCreate(store.Settings.NormalizedHorizonUrl, UriKind.Absolute, out var sessionEndpoint) ||
                Uri.Compare(sessionEndpoint, expectedEndpoint, UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
                return Reject("Connected root, Horizon endpoint or network selection differs.");

            var read = await store.ReadAsync();
            ct.ThrowIfCancellationRequested();
            if (read.Warnings.Count != 0 ||
                read.Nodes.Count == 0 ||
                read.Nodes[0].Position != 0 ||
                !string.Equals(read.Nodes[0].AccountId, expectedRootAccount, StringComparison.Ordinal) ||
                read.Nodes.Count(x => x.Position == expectedNodeIndex &&
                    string.Equals(x.AccountId, expectedNodeAccount, StringComparison.Ordinal)) != 1 ||
                read.Records.Count(x => string.Equals(x.Record.Id, recordId,
                    StringComparison.OrdinalIgnoreCase)) != 1)
                return Reject("Connected HD chain or record is incomplete or ambiguous.");

            var record = read.Records.Single(x => string.Equals(x.Record.Id, recordId,
                StringComparison.OrdinalIgnoreCase));
            var actual = record.Record;
            if (!string.Equals(record.NodeAccountId, expectedNodeAccount, StringComparison.Ordinal) ||
                !string.Equals(actual.Id, recordId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) ||
                !string.Equals(actual.Secret, expected.Content, StringComparison.Ordinal) ||
                !string.Equals(actual.FolderId, expected.FolderId, StringComparison.Ordinal) ||
                actual.IsFolder != expected.IsFolder ||
                !string.Equals(actual.UserName, expected.UserName ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(actual.Url, expected.Url ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(actual.Notes, expected.Notes ?? string.Empty, StringComparison.Ordinal) ||
                !(actual.Tags ?? []).SequenceEqual(expected.Tags ?? [], StringComparer.Ordinal))
                return Reject("Decrypted current record differs from the approved draft or HD child.");
            return new(true, "Current session decrypted record and approved fields match the requested HD child.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Reject("Connected HD record comparison could not be established."); }
    }

    public Task<bool> HasRegistryAsync() => Task.FromResult(RequireStore().RegistryInitialized);

    public async Task<LedgerFundingStatus> InspectRegistryFundingAsync()
    {
        var store = RequireStore();
        var treasury = await store.InspectRootTreasuryAsync(
            additionalSponsoredUnits: 1, expectedFeeStroops: store.Settings.BaseFeeStroops);
        decimal required = (store.Settings.BaseReserveStroops + store.Settings.BaseFeeStroops) / 10_000_000m;
        return new LedgerFundingStatus(store.RootAccountId, treasury.BalanceXlm,
            treasury.SpendableXlm, required.ToString("0.0000000",
                System.Globalization.CultureInfo.InvariantCulture), treasury.CanAffordPlan);
    }
    public async Task<LedgerAccountInfo> InspectAccountInfoAsync()
    {
        var store = RequireStore();
        var read = await store.ReadAsync();
        if (read.Warnings.Count != 0)
            throw new InvalidDataException("Account chain recovery has warnings: " + string.Join("; ", read.Warnings.Take(3)));
        var treasury = await store.InspectRootTreasuryAsync();
        return new LedgerAccountInfo(store.RootAccountId,
            read.Nodes.LastOrDefault()?.AccountId ?? store.RootAccountId,
            read.Nodes.Count, read.Records.Count(x => !x.Record.IsFolder),
            read.Records.Count(x => x.Record.IsFolder),
            treasury.BalanceXlm, treasury.SpendableXlm, treasury.SponsoredReserveXlm);
    }

    public Task InitializeRegistryAsync() => RequireStore().InitializeHdRegistryAsync();

    /// <summary>
    /// Resolve HD child index from the authenticated currently-connected chain,
    /// never from user-entered indices or untrusted Horizon transaction fields.
    /// An incomplete or ambiguous discovery fails closed.
    /// </summary>
    public async Task<int?> ResolveAuthenticatedHdChildAsync(
        string expectedRoot, string childAccount, string expectedHorizon,
        string expectedPassphrase, CancellationToken ct = default)
    {
        if (!IsConnected || !StellarAccountAddress.IsValid(expectedRoot) ||
            !StellarAccountAddress.IsValid(childAccount) ||
            !Uri.TryCreate(expectedHorizon, UriKind.Absolute, out var expectedEndpoint) ||
            expectedEndpoint.Scheme != Uri.UriSchemeHttps)
            return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var store = RequireStore();
            if (store.RootAccountId != expectedRoot ||
                store.Settings.Passphrase != expectedPassphrase ||
                !Uri.TryCreate(store.Settings.NormalizedHorizonUrl, UriKind.Absolute, out var currentEndpoint) ||
                Uri.Compare(currentEndpoint, expectedEndpoint,
                    UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
                return null;
            var snapshot = await store.ReadAsync();
            ct.ThrowIfCancellationRequested();
            if (snapshot.Warnings.Count != 0 || snapshot.Nodes.Count == 0 ||
                snapshot.Nodes[0].Position != 0 ||
                snapshot.Nodes[0].AccountId != expectedRoot)
                return null;
            var matches = snapshot.Nodes.Where(x =>
                x.Position is > 0 and < 64 &&
                x.AccountId == childAccount).ToArray();
            if (matches.Length != 1 ||
                !await store.VerifyCurrentHdLineageAsync(
                    expectedRoot, childAccount, matches[0].Position, ct))
                return null;
            return matches[0].Position;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    public async Task<IReadOnlyList<string>> InspectOwnersAsync(
        string rootAccountId, string recordId, CancellationToken ct = default)
    {
        if (!IsConnected || !StellarAccountAddress.IsValid(rootAccountId) ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Invalid manifest inventory scope.");
        var store = RequireStore();
        if (store.RootAccountId != rootAccountId)
            throw new UnauthorizedAccessException("Connected signer root does not match the local vault.");
        ct.ThrowIfCancellationRequested();
        var snapshot = await store.ReadAsync();
        ct.ThrowIfCancellationRequested();
        if (snapshot.Warnings.Count != 0 || snapshot.Nodes.Count == 0 ||
            snapshot.Nodes[0].AccountId != rootAccountId)
            throw new InvalidDataException("HD inventory contains recovery warnings or an unexpected root.");
        var key = "svh:" + recordId.ToLowerInvariant();
        var owners = snapshot.Nodes.Where(x => x.Data.ContainsKey(key))
            .Select(x => x.AccountId).ToArray();
        if (owners.Length == 0 || owners.Length > 63 ||
            owners.Distinct(StringComparer.Ordinal).Count() != owners.Length)
            throw new InvalidDataException("Manifest-owner inventory is missing or ambiguous.");
        foreach (var owner in owners)
        {
            var node = snapshot.Nodes.SingleOrDefault(x => x.AccountId == owner);
            if (node is null || node.Position is <= 0 or >= 64 ||
                !await store.VerifyCurrentHdLineageAsync(
                    rootAccountId, owner, node.Position, ct))
                throw new InvalidDataException("Manifest-owner HD lineage cannot be authenticated.");
        }
        if (!IsConnected || store.RootAccountId != rootAccountId)
            throw new InvalidOperationException("Connected signing session changed.");
        return owners.Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<LedgerRecordItem>> ReadAsync()
    {
        var read = await RequireStore().ReadAsync();
        if (read.Warnings.Count != 0)
            throw new InvalidDataException("Ledger record recovery reported errors: " +
                string.Join("; ", read.Warnings.Take(3)));
        return read.Records.Select(r =>
            new LedgerRecordItem(r.Record.Id, r.Record.Title, r.Record.Secret, r.Record.FolderId,
                r.Record.Tags, r.Record.IsFolder, r.Record.UserName, r.Record.Url, r.Record.Notes)).ToArray();
    }

    public Task<string> PublishAsync(string title, string content) =>
        PublishDetailedAsync(new LedgerRecordDraft(title, content));

    public Task<string> PublishDetailedAsync(LedgerRecordDraft draft) =>
        PublishWithIdAsync(Guid.NewGuid().ToString("N")[..16], draft);

    public Task<string> PublishWithIdAsync(string recordId, LedgerRecordDraft draft) =>
        PublishWithIdCoreAsync(recordId, draft, null);

    public Task<string> PublishWithPreparedHashAsync(
        string recordId, LedgerRecordDraft draft,
        Func<string, Task> checkpointSignedManifestHash)
    {
        ArgumentNullException.ThrowIfNull(checkpointSignedManifestHash);
        return PublishWithIdCoreAsync(recordId, draft, checkpointSignedManifestHash);
    }

    private async Task<string> PublishWithIdCoreAsync(
        string recordId, LedgerRecordDraft draft,
        Func<string, Task>? checkpointSignedManifestHash)
    {
        if (recordId.Length != 16 || !recordId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid 16-character record identifier.", nameof(recordId));
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrEmpty(draft.Content))
            throw new ArgumentException("Record title and contents are required.");
        if (draft.Title.Length > 256 || draft.Tags?.Length > 24 ||
            draft.Tags?.Any(t => t.Length > 64) == true)
            throw new ArgumentException("Record title or tags exceed the supported limits.");
        var store = RequireStore();
        var snapshot = await store.ReadAsync();
        if (snapshot.Warnings.Count != 0)
            throw new InvalidDataException("Incomplete ledger read blocks publication.");
        if (snapshot.Records.Any(x => string.Equals(x.Record.Id, recordId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Target record ID already exists.");
        if (!string.IsNullOrEmpty(draft.FolderId) &&
            !snapshot.Records.Any(x => string.Equals(x.Record.Id, draft.FolderId, StringComparison.OrdinalIgnoreCase) && x.Record.Kind == "folder"))
            throw new InvalidOperationException("Parent is missing or is not a folder.");
        var now = DateTimeOffset.UtcNow;
        var record = new VaultRecord
        {
            Id = recordId,
            Title = draft.Title.Trim(),
            Secret = draft.Content,
            FolderId = draft.FolderId,
            Tags = draft.Tags ?? [],
            Kind = draft.IsFolder ? "folder" : "record",
            UserName = draft.UserName ?? string.Empty,
            Url = draft.Url ?? string.Empty,
            Notes = draft.Notes ?? string.Empty,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        string hash = await store.AddAsync(record, checkpointSignedManifestHash);
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("Stellar did not return a confirmed transaction hash.");
        return hash;
    }

    private static VaultRecord ToRecord(LedgerRecordDraft draft, string id, DateTimeOffset created) =>
        new() {
            Id = id, Title = draft.Title.Trim(), Secret = draft.Content,
            FolderId = string.IsNullOrWhiteSpace(draft.FolderId) ? null : draft.FolderId,
            Tags = draft.Tags ?? [], Kind = draft.IsFolder ? "folder" : "record",
            UserName = draft.UserName ?? string.Empty, Url = draft.Url ?? string.Empty,
            Notes = draft.Notes ?? string.Empty, CreatedUtc = created, UpdatedUtc = DateTimeOffset.UtcNow
        };

    private static LedgerCostPreview Preview(VaultCostEstimate estimate) =>
        new(estimate.OperationCount, estimate.TransactionCount, estimate.NetworkFeeNative,
            estimate.PeakReserveIncreaseNative, estimate.NetReserveChangeNative,
            estimate.CreatesChildNode);

    public async Task<LedgerCostPreview> EstimateAsync(LedgerRecordDraft draft, string? recordId = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrEmpty(draft.Content))
            throw new ArgumentException("Record title and content are required.");
        var store = RequireStore();
        if (recordId is null)
            return Preview(await store.EstimateAddAsync(ToRecord(draft, Guid.NewGuid().ToString("N")[..16], DateTimeOffset.UtcNow)));
        var snapshot = await store.ReadAsync();
        if (snapshot.Warnings.Count != 0) throw new InvalidDataException("Ledger recovery warnings prevent safe estimation.");
        var location = snapshot.Records.SingleOrDefault(x => x.Record.Id == recordId)
            ?? throw new InvalidOperationException("Record no longer exists.");
        return Preview(await store.EstimateUpdateAsync(location, ToRecord(draft, recordId, location.Record.CreatedUtc)));
    }

    public async Task<LedgerCostPreview> EstimateDeleteAsync(string id)
    {
        var store = RequireStore();
        var snapshot = await store.ReadAsync();
        if (snapshot.Warnings.Count != 0) throw new InvalidDataException("Ledger recovery warnings prevent safe estimation.");
        var location = snapshot.Records.SingleOrDefault(x => x.Record.Id == id)
            ?? throw new InvalidOperationException("Record no longer exists.");
        return Preview(await store.EstimateDeleteAsync(location));
    }

    public Task<LedgerMutationResult> UpdateAsync(string id, LedgerRecordDraft draft) =>
        UpdateCoreAsync(id, draft, null);

    public Task<LedgerMutationResult> UpdateIfUnchangedAsync(
        string recordId, LedgerRecordDraft draft, string expectedVersion,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (expectedVersion is not { Length: 64 } ||
            !expectedVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Guarded update requires an authenticated pinned version.");
        return UpdateCoreAsync(recordId, draft, expectedVersion, null);
    }

    public Task<LedgerMutationResult> UpdateIfUnchangedWithPreparedHashAsync(
        string recordId, LedgerRecordDraft draft, string expectedRemoteVersion,
        Func<string, Task> checkpointSignedManifestHash,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpointSignedManifestHash);
        if (expectedRemoteVersion is not { Length: 64 } ||
            !expectedRemoteVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Prepared update requires a pinned remote version.");
        return UpdateCoreAsync(recordId, draft, expectedRemoteVersion,
            checkpointSignedManifestHash);
    }

    private async Task<LedgerMutationResult> UpdateCoreAsync(
        string id, LedgerRecordDraft draft, string? expectedVersion,
        Func<string, Task>? checkpointSignedManifestHash = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var snapshot = await RequireStore().ReadAsync();
        var current = snapshot.Records.SingleOrDefault(x => x.Record.Id == id)
            ?? throw new InvalidOperationException("Record no longer exists; synchronize first.");
        if (snapshot.Warnings.Count > 0)
            throw new InvalidDataException("Ledger recovery contains warnings. Synchronize before editing.");
        if (current.Record.IsFolder != draft.IsFolder)
            throw new InvalidOperationException("Cannot change an existing record into a folder or vice versa.");
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrEmpty(draft.Content)
            || draft.Title.Length > 256 || draft.Tags?.Length > 24 ||
            draft.Tags?.Any(t => t.Length > 64) == true)
            throw new ArgumentException("Invalid title, content or tags.");
        if (draft.FolderId == id) throw new ArgumentException("A folder cannot contain itself.");
        if (!string.IsNullOrWhiteSpace(draft.FolderId))
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal) { id };
            var ancestor = draft.FolderId;
            while (ancestor is not null)
            {
                if (!ancestors.Add(ancestor))
                    throw new InvalidOperationException("Folder move would create a cycle.");
                ancestor = snapshot.Records.SingleOrDefault(x => x.Record.Id == ancestor && x.Record.IsFolder)?.Record.FolderId
                    ?? (snapshot.Records.Any(x => x.Record.Id == ancestor && x.Record.IsFolder)
                        ? null : throw new InvalidOperationException("Target folder does not exist."));
            }
        }
        var updated = new VaultRecord {
            Id = id, Title = draft.Title.Trim(), Secret = draft.Content,
            UserName = draft.UserName ?? string.Empty, Url = draft.Url ?? string.Empty,
            Notes = draft.Notes ?? string.Empty, Tags = draft.Tags ?? [],
            FolderId = string.IsNullOrWhiteSpace(draft.FolderId) ? null : draft.FolderId,
            Kind = current.Record.Kind, CreatedUtc = current.Record.CreatedUtc,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        var result = await RequireStore().UpdateAsync(
            current, updated, expectedVersion, checkpointSignedManifestHash);
        return LedgerMutationResult.RequireConfirmed(
            result.TransactionHash, result.Warning, result.CommitTransactionHash);
    }

    public Task<LedgerMutationResult> DeleteAsync(string id) =>
        DeleteCoreAsync(id, null, null);

    public Task<LedgerMutationResult> DeleteIfUnchangedAsync(
        string recordId, string expectedVersion,
        IReadOnlyList<string> expectedManifestOwners,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (expectedVersion is not { Length: 64 } ||
            !expectedVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Guarded deletion requires a pinned version.");
        ArgumentNullException.ThrowIfNull(expectedManifestOwners);
        return DeleteCoreAsync(recordId, expectedVersion, expectedManifestOwners);
    }

    public Task<LedgerMutationResult> DeleteIfUnchangedWithJournalAsync(
        string recordId, string expectedRemoteVersion,
        IReadOnlyList<string> expectedManifestOwners,
        Func<string, string, Task> checkpointSignedTombstone,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpointSignedTombstone);
        if (expectedRemoteVersion is not { Length: 64 } ||
            !expectedRemoteVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Version-pinned signed deletion journal required.");
        return DeleteCoreAsync(recordId, expectedRemoteVersion, expectedManifestOwners,
            checkpointSignedTombstone);
    }

    private async Task<LedgerMutationResult> DeleteCoreAsync(
        string id, string? expectedVersion,
        IReadOnlyList<string>? expectedManifestOwners,
        Func<string, string, Task>? checkpointSignedTombstone = null)
    {
        var snapshot = await RequireStore().ReadAsync();
        if (snapshot.Warnings.Count > 0)
            throw new InvalidDataException("Ledger recovery contains warnings. Synchronize before deleting.");
        var current = snapshot.Records.SingleOrDefault(x => x.Record.Id == id)
            ?? throw new InvalidOperationException("Record no longer exists; synchronize first.");
        if (current.Record.IsFolder && snapshot.Records.Any(x => x.Record.FolderId == id))
            throw new InvalidOperationException("Move or delete the folder's contents before deleting the folder.");
        var result = await RequireStore().DeleteAsync(
            current, expectedVersion, expectedManifestOwners,
            checkpointSignedTombstone);
        return LedgerMutationResult.RequireConfirmed(
            result.TransactionHash, result.Warning, result.CommitTransactionHash);
    }

    public async Task DisconnectAsync()
    {
        var store = _store;
        if (store is null) return;
        // Do not report disconnected until JS session deletion completes.
        // If deletion fails, retaining the reference blocks another ConnectAsync
        // from opening a second signing session over potentially live keys.
        await store.DisposeAsync();
        _store = null;
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync();
    private async ValueTask DisposeCoreAsync() => await DisconnectAsync();
}

/// <summary>Read-only finding: never carries decrypted vault data.</summary>
public sealed record ConnectedVaultRecordFinding(bool Matches, string Explanation);
