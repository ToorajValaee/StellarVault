using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.JSInterop;
using StellarVaultPwa.Core;
using StellarVault.Core.Services;

namespace StellarVaultPwa.Services;

public sealed class StellarVaultStore : IAsyncDisposable
{
    private const string HeaderPrefix = "svh:";
    private const string ChunkPrefix = "svc:";
    private const int ChunkValueSize = 64;
    private const int MaxOperationsPerTransaction = 90;
    private const int MaxChainLength = 64;
    private const byte LegacyManifestVersion = 1;
    private const byte CurrentManifestVersion = 2;
    private const int LegacyManifestLength = 47;
    private const int CurrentManifestLength = 51;

    private static long _sessionCounter;

    private readonly VaultNetworkSettings _settings;
    private readonly IJSRuntime _js;
    private readonly string _sessionId;
    private readonly VaultCrypto _crypto;
    private string? _rootAccountId;
    private bool _disposed;
    public bool RegistryInitialized { get; private set; }

    public StellarVaultStore(VaultNetworkSettings settings, IJSRuntime js)
    {
        _settings = settings with { HorizonUrl = settings.NormalizedHorizonUrl };
        _js = js;
        _sessionId = $"sv-{Interlocked.Increment(ref _sessionCounter):x}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}";
        _crypto = new VaultCrypto(js, _sessionId);
    }

    public string RootAccountId =>
        _rootAccountId ?? throw new InvalidOperationException("The Stellar session is not open.");

    public VaultNetworkSettings Settings => _settings;

    public async Task<RootTreasurySnapshot> InspectRootTreasuryAsync(
        int additionalSponsoredUnits = 0,
        long expectedFeeStroops = 0,
        long safetyBufferStroops = 0)
    {
        ThrowIfDisposed();
        if (_rootAccountId is null)
            throw new InvalidOperationException("The Stellar session is not open.");
        if (additionalSponsoredUnits < 0 || expectedFeeStroops < 0 || safetyBufferStroops < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalSponsoredUnits));
        return await _js.InvokeAsync<RootTreasurySnapshot>(
            "stellarVault.stellar.inspectRootTreasury", _sessionId,
            _settings.BaseReserveStroops, additionalSponsoredUnits,
            expectedFeeStroops.ToString(CultureInfo.InvariantCulture),
            safetyBufferStroops.ToString(CultureInfo.InvariantCulture));
    }

    public async Task OpenAsync(string secret, string bip39Passphrase = "")
    {
        ThrowIfDisposed();
        if (_rootAccountId is not null)
            throw new InvalidOperationException("The Stellar session is already open.");

        var result = await _js.InvokeAsync<JsSessionInfo>(
            "stellarVault.stellar.openHdSession",
            _sessionId,
            secret,
            _settings.HorizonUrl,
            _settings.Passphrase,
            bip39Passphrase);

        if (string.IsNullOrWhiteSpace(result.RootAccountId))
            throw new InvalidOperationException("The browser Stellar SDK did not return a root account ID.");

        _rootAccountId = result.RootAccountId;
        RegistryInitialized = result.RegistryInitialized;
    }

    public async Task InitializeHdRegistryAsync()
    {
        ThrowIfDisposed();
        if (_rootAccountId is null)
            throw new InvalidOperationException("Unlock the funded HD root before initialization.");

        // The JavaScript bootstrap refuses existing registries and scans derived
        // children before writing the first authenticated registry entry.
        await _js.InvokeVoidAsync(
            "stellarVault.stellar.initializeHdRegistry",
            _sessionId,
            _settings.BaseReserveStroops,
            _settings.BaseFeeStroops);
        RegistryInitialized = true;
    }

    /// <summary>
    /// Verify an already receipt-checked historical root registry envelope using
    /// the connected seed-derived registry MAC and expected child derivation.
    /// This does not establish independent Horizon trust.
    /// </summary>
    public async Task<bool> CheckHistoricalRegistryAsync(
        string expectedRoot, string expectedChild, int index, byte[] envelope,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_rootAccountId is null ||
            !string.Equals(_rootAccountId, expectedRoot, StringComparison.Ordinal) ||
            index is <= 0 or >= MaxChainLength || envelope is not { Length: 50 })
            return false;
        try
        {
            return await _js.InvokeAsync<bool>(
                "stellarVault.stellar.inspectHistoricalHdRegistry", ct,
                _sessionId, expectedRoot, expectedChild, index, envelope);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Authenticate an HD child directly from the unlocked mnemonic-derived
    /// signing session, including the JS chain/registry checks. A child
    /// appearing in an account snapshot alone is not lineage proof.
    /// </summary>
    public async Task<bool> VerifyCurrentHdLineageAsync(
        string expectedRoot, string expectedChild, int childIndex,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_rootAccountId != expectedRoot || childIndex is <= 0 or >= MaxChainLength ||
            !StellarAccountAddress.IsValid(expectedChild))
            return false;
        try
        {
            return await _js.InvokeAsync<bool>(
                "stellarVault.stellar.inspectHdLineage", ct,
                _sessionId, expectedRoot, expectedChild, childIndex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Compare already provenance-checked HISTORICAL encrypted bytes using the
    /// existing in-memory HD session. No transaction is sent; no plaintext is
    /// returned. The caller owns and clears the ciphertext buffer.
    /// </summary>
    public async Task<bool> CompareHistoricalHdRecordAsync(
        string expectedRoot, string expectedNode, int nodeIndex, string recordId,
        string generation, byte[] ciphertext, LedgerRecordDraft approved,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_rootAccountId is null || approved is null ||
            !string.Equals(_rootAccountId, expectedRoot, StringComparison.Ordinal) ||
            nodeIndex is <= 0 or >= MaxChainLength ||
            recordId is not { Length: 16 } || !recordId.All(Uri.IsHexDigit) ||
            generation is not { Length: 8 } || !generation.All(Uri.IsHexDigit) ||
            ciphertext is not { Length: > 0 })
            return false;

        ct.ThrowIfCancellationRequested();
        // Independently derive root and child addresses from the unlocked seed
        // and re-authenticate the current on-chain root registry before decrypting.
        // A cached list of account addresses alone is not lineage evidence.
        var lineageMatches = await _js.InvokeAsync<bool>(
            "stellarVault.stellar.inspectHdLineage",
            ct, _sessionId, expectedRoot, expectedNode, nodeIndex);
        if (!lineageMatches) return false;
        ct.ThrowIfCancellationRequested();
        var chain = await LoadChainAsync();
        ct.ThrowIfCancellationRequested();
        if (chain.Count == 0 || chain[0].Position != 0 ||
            !string.Equals(chain[0].AccountId, expectedRoot, StringComparison.Ordinal) ||
            chain.Count(node => node.Position == nodeIndex &&
                string.Equals(node.AccountId, expectedNode, StringComparison.Ordinal)) != 1)
            return false;

        // Decrypt the bytes proven by historical operations, not a mutable
        // current-account snapshot. VaultCrypto clears its plaintext buffer.
        var actual = await _crypto.DecryptHdAsync(recordId, nodeIndex, generation, ciphertext);
        ct.ThrowIfCancellationRequested();
        return string.Equals(actual.Id, recordId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(actual.Title, approved.Title, StringComparison.Ordinal) &&
            string.Equals(actual.Secret, approved.Content, StringComparison.Ordinal) &&
            string.Equals(actual.FolderId, approved.FolderId, StringComparison.Ordinal) &&
            actual.IsFolder == approved.IsFolder &&
            string.Equals(actual.UserName, approved.UserName ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(actual.Url, approved.Url ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(actual.Notes, approved.Notes ?? string.Empty, StringComparison.Ordinal) &&
            (actual.Tags ?? []).SequenceEqual(approved.Tags ?? [], StringComparer.Ordinal);
    }

    public async Task<VaultReadResult> ReadAsync()
    {
        ThrowIfDisposed();

        var nodes = await LoadChainAsync();
        var recordsById = new Dictionary<string, (VaultRecordLocation Location, long Updated)>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var node in nodes)
        {
            foreach (var header in node.Data.Where(x => x.Key.StartsWith(HeaderPrefix, StringComparison.Ordinal)))
            {
                var recordId = header.Key[HeaderPrefix.Length..];

                try
                {
                    ValidateRecordId(recordId);
                    var manifest = ParseManifest(header.Value);
                    var encrypted = ReassembleEncryptedRecord(node, recordId, manifest);

                    try
                    {
                        var computedHash = await _crypto.HashAsync(encrypted);
                        try
                        {
                            if (!CryptographicOperations.FixedTimeEquals(computedHash, manifest.CiphertextHash))
                                throw new InvalidDataException("Ciphertext hash verification failed.");
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(computedHash);
                        }

                        if (manifest.Generation is null)
                            throw new InvalidDataException("Legacy record manifests are not supported in HD v2.");

                        var record = await _crypto.DecryptHdAsync(
                            recordId, node.Position, manifest.Generation, encrypted);
                        if (!string.Equals(record.Id, recordId, StringComparison.Ordinal))
                            throw new InvalidDataException("The decrypted record ID does not match its ledger key.");

                        var location = new VaultRecordLocation(
                            record,
                            node.AccountId,
                            manifest.ChunkCount,
                            manifest.EncryptedLength);

                        if (recordsById.TryGetValue(recordId, out var existing))
                        {
                            warnings.Add(
                                $"Duplicate record {recordId} was found while recovering an interrupted update. " +
                                "The newest committed manifest is being used.");

                            if (manifest.UpdatedUnixSeconds < existing.Updated)
                                continue;
                        }

                        recordsById[recordId] = (location, manifest.UpdatedUnixSeconds);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(encrypted);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"{ShortAccount(node.AccountId)} / {recordId}: {ex.Message}");
                }
            }
        }

        return new VaultReadResult(
            recordsById.Values
                .Select(x => x.Location)
                .OrderBy(x => x.Record.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            nodes);
    }

    public async Task<VaultCostEstimate> EstimateAddAsync(VaultRecord record)
    {
        ThrowIfDisposed();
        ValidateRecord(record);

        var nodes = await LoadChainAsync();
        if (nodes.Any(node => node.Data.ContainsKey(HeaderName(record.Id))))
            throw new InvalidOperationException($"A record with ID {record.Id} already exists.");

        var chunkCount = await EstimateHdChunkCountAsync(record);
        EnsureRecordFitsNode(chunkCount);
        var createsChild = !CanStageRecord(nodes[^1], chunkCount, headerAlreadyExists: false);
        var entries = chunkCount + 1;
        var operations = entries * 3 + (createsChild ? 4 : 0);
        var transactions = entries + (createsChild ? 1 : 0);
        var reserve = (entries + (createsChild ? 2 : 0)) * BaseReserveNative;

        return new VaultCostEstimate(
            operations, transactions, chunkCount, entries, 0,
            FeeNative(operations), reserve, reserve, 0m, createsChild);
    }

    public async Task<VaultCostEstimate> EstimateUpdateAsync(
        VaultRecordLocation expectedLocation,
        VaultRecord record)
    {
        ThrowIfDisposed();
        ValidateRecord(record);

        if (!string.Equals(expectedLocation.Record.Id, record.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The edited record ID does not match the original record.");

        var nodes = await LoadChainAsync();
        var oldCopies = FindRecordCopies(nodes, record.Id);
        if (oldCopies.Count == 0)
            throw new InvalidOperationException("The record no longer exists. Synchronize the vault and try again.");

        var currentCopy = oldCopies
            .OrderByDescending(x => x.Manifest.UpdatedUnixSeconds)
            .First();

        var chunkCount = await EstimateHdChunkCountAsync(record);
        EnsureRecordFitsNode(chunkCount);

        var source = currentCopy.Node;
        var target = source;
        var targetHasHeader = true;
        var createsChild = false;

        if (!CanStageRecord(source, chunkCount, headerAlreadyExists: true))
        {
            target = nodes[^1];
            targetHasHeader = target.Data.ContainsKey(HeaderName(record.Id));
            if (!CanStageRecord(target, chunkCount, targetHasHeader))
            {
                createsChild = true;
                targetHasHeader = false;
            }
        }

        var targetId = createsChild ? "NEW-CHILD-NODE" : target.AccountId;
        var generation = "__estimate_generation__";
        var cleanupCount = BuildUpdateCleanupWrites(
                oldCopies,
                targetId,
                record.Id,
                generation)
            .Count;

        var commitWrites = chunkCount + 1;
        var newEntries = chunkCount + (targetHasHeader ? 0 : 1);
        var allocationOperations = createsChild ? 4 : 0;
        // Each newly sponsored entry requires begin/write/end operations.
        // Existing entries need only a ManageData operation.
        var commitOperations = newEntries * 3 + (commitWrites - newEntries);
        var operationCount = allocationOperations + commitOperations + cleanupCount;
        var transactionCount = (createsChild ? 1 : 0) + commitWrites + cleanupCount;
        var reserve = BaseReserveNative;
        var peakReserve = (newEntries + (createsChild ? 2 : 0)) * reserve;
        var netReserve = peakReserve - cleanupCount * reserve;

        return new VaultCostEstimate(
            operationCount, transactionCount, chunkCount, newEntries,
            cleanupCount, FeeNative(operationCount), peakReserve,
            netReserve, 0m, createsChild);
    }

    public async Task<VaultCostEstimate> EstimateDeleteAsync(VaultRecordLocation expectedLocation)
    {
        ThrowIfDisposed();
        ValidateRecordId(expectedLocation.Record.Id);

        var nodes = await LoadChainAsync();
        var headerName = HeaderName(expectedLocation.Record.Id);
        var headerCount = nodes.Count(node => node.Data.ContainsKey(headerName));
        if (headerCount == 0)
            throw new InvalidOperationException("The record no longer exists.");

        var chunkPrefix = ChunkPrefix + expectedLocation.Record.Id + ":";
        var chunkDeleteCount = nodes.Sum(node =>
            node.Data.Keys.Count(name => name.StartsWith(chunkPrefix, StringComparison.Ordinal)));
        var operationCount = headerCount + chunkDeleteCount;
        var transactionCount = operationCount;
        var released = operationCount * BaseReserveNative;

        return new VaultCostEstimate(
            operationCount,
            transactionCount,
            expectedLocation.ChunkCount,
            0,
            operationCount,
            FeeNative(operationCount),
            0m,
            -released,
            0m,
            false);
    }

    public async Task<string> AddAsync(
        VaultRecord record, Func<string, Task>? checkpointSignedManifest = null)
    {
        ThrowIfDisposed();
        ValidateRecord(record);

        var nodes = await LoadChainAsync();
        if (nodes.Any(node => node.Data.ContainsKey(HeaderName(record.Id))))
            throw new InvalidOperationException($"A record with ID {record.Id} already exists.");

        // Ciphertext length is plaintext JSON length + v2 nonce/version/tag (29 bytes).
        // Size before choosing the child because the child index is authenticated AAD.
        var estimatedChunks = await EstimateHdChunkCountAsync(record);
        EnsureRecordFitsNode(estimatedChunks);

        var target = nodes[^1];
        if (!CanStageRecord(target, estimatedChunks, headerAlreadyExists: false))
            target = await CreateAndAppendChildAsync(target);

        var generation = CreateGeneration();
        var encrypted = await _crypto.EncryptHdAsync(record, target.Position, generation);
        try
        {
            var chunks = SplitIntoChunks(encrypted);
            if (chunks.Count != estimatedChunks)
                throw new InvalidDataException("Encrypted record size changed during staging.");
            var manifest = await CreateManifestAsync(
                encrypted,
                chunks.Count,
                record.UpdatedUtc.ToUnixTimeSeconds(),
                generation);

            var writes = BuildRecordWrites(target.AccountId, record.Id, chunks, manifest, generation);
            return await SubmitWritesAsync(writes, checkpointSignedManifest,
                checkpointSignedManifest is null ? null : HeaderName(record.Id));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<VaultMutationResult> UpdateAsync(
        VaultRecordLocation expectedLocation,
        VaultRecord record, string? expectedRemoteVersion = null,
        Func<string, Task>? checkpointSignedManifest = null)
    {
        ThrowIfDisposed();
        ValidateRecord(record);
        // The guarded hybrid path must re-read the original decrypted record
        // here, inside the transport writer, rather than relying only on an
        // earlier Core fee approval. Direct on-chain CRUD remains unchanged.
        if (expectedRemoteVersion is not null)
            await RequireCurrentVersionAsync(
                expectedLocation.Record.Id, expectedRemoteVersion);

        if (!string.Equals(expectedLocation.Record.Id, record.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The edited record ID does not match the original record.");

        var nodes = await LoadChainAsync();
        var oldCopies = FindRecordCopies(nodes, record.Id);
        if (oldCopies.Count == 0)
            throw new InvalidOperationException("The record no longer exists. Synchronize the vault and try again.");

        var currentCopy = oldCopies
            .OrderByDescending(x => x.Manifest.UpdatedUnixSeconds)
            .First();

        var estimatedChunks = await EstimateHdChunkCountAsync(record);
        EnsureRecordFitsNode(estimatedChunks);
        var generation = CreateGeneration(currentCopy.Manifest.Generation);
            var updatedSeconds = Math.Max(
                record.UpdatedUtc.ToUnixTimeSeconds(),
                currentCopy.Manifest.UpdatedUnixSeconds + 1);

            var source = currentCopy.Node;
            VaultNodeSnapshot target;

            if (CanStageRecord(source, estimatedChunks, headerAlreadyExists: true))
            {
                target = source;
            }
            else
            {
                target = nodes[^1];
                var targetHasHeader = target.Data.ContainsKey(HeaderName(record.Id));

                if (!CanStageRecord(target, estimatedChunks, targetHasHeader))
                    target = await CreateAndAppendChildAsync(target);
            }

            var encrypted = await _crypto.EncryptHdAsync(record, target.Position, generation);
            try
            {
                var chunks = SplitIntoChunks(encrypted);
                if (chunks.Count != estimatedChunks)
                    throw new InvalidDataException("Encrypted record size changed during staging.");

            var manifest = await CreateManifestAsync(
                encrypted,
                chunks.Count,
                updatedSeconds,
                generation);

            var commitHash = await SubmitWritesAsync(
                BuildRecordWrites(target.AccountId, record.Id, chunks, manifest, generation),
                checkpointSignedManifest,
                checkpointSignedManifest is null ? null : HeaderName(record.Id));

            string? cleanupWarning = null;
            string lastHash = commitHash;

            try
            {
                var cleanup = BuildUpdateCleanupWrites(oldCopies, target.AccountId, record.Id, generation);
                if (cleanup.Count > 0)
                    lastHash = await SubmitWritesAsync(cleanup);
            }
            catch (Exception ex)
            {
                cleanupWarning =
                    "The edited record was committed, but some obsolete ledger entries could not be removed. " +
                    $"A later edit or delete can retry cleanup. Details: {ex.Message}";
            }

            return new VaultMutationResult(lastHash, cleanupWarning, commitHash);
        }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
    }

    public async Task<VaultMutationResult> DeleteAsync(
        VaultRecordLocation expectedLocation,
        string? expectedRemoteVersion = null,
        IReadOnlyList<string>? expectedManifestOwners = null)
    {
        ThrowIfDisposed();
        ValidateRecordId(expectedLocation.Record.Id);
        if (expectedRemoteVersion is not null)
        {
            if (expectedManifestOwners is not { Count: > 0 and <= 63 } ||
                expectedManifestOwners.Any(x => !StellarAccountAddress.IsValid(x)) ||
                expectedManifestOwners.Distinct(StringComparer.Ordinal).Count() !=
                    expectedManifestOwners.Count)
                throw new InvalidDataException("Guarded deletion requires a valid complete manifest-owner set.");
            await RequireCurrentVersionAsync(
                expectedLocation.Record.Id, expectedRemoteVersion);
        }

        var nodes = await LoadChainAsync();
        var headerName = HeaderName(expectedLocation.Record.Id);
        var headerNodes = nodes
            .Where(node => node.Data.ContainsKey(headerName))
            .ToArray();

        if (headerNodes.Length == 0)
            throw new InvalidOperationException("The record no longer exists.");
        if (expectedRemoteVersion is not null)
        {
            var owners = headerNodes.Select(x => x.AccountId)
                .Order(StringComparer.Ordinal).ToArray();
            if (!owners.SequenceEqual(
                    expectedManifestOwners!.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                throw new InvalidOperationException(
                    "HD manifest-owner set changed at the transport boundary; deletion was not submitted.");
        }

        var headerDeletes = headerNodes
            .Select(node => DeleteWrite(node.AccountId, headerName))
            .DistinctBy(write => $"{write.SourceAccountId}|{write.Name}", StringComparer.Ordinal)
            .ToArray();

        // This receipt deletes the manifest and is authoritative for the
        // record's removal. Later orphan-chunk cleanup has a DIFFERENT hash.
        var lastHash = await SubmitWritesAsync(headerDeletes);
        var manifestDeleteHash = lastHash;
        string? cleanupWarning = null;

        try
        {
            var prefix = ChunkPrefix + expectedLocation.Record.Id + ":";
            var chunkDeletes = nodes.SelectMany(node =>
                    node.Data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(key => DeleteWrite(node.AccountId, key)))
                .DistinctBy(write => write.SourceAccountId + "|" + write.Name, StringComparer.Ordinal)
                .ToArray();

            if (chunkDeletes.Length > 0)
                lastHash = await SubmitWritesAsync(chunkDeletes);
        }
        catch (Exception ex)
        {
            var cleanupFailure =
                "The record manifest was deleted, so the credential is no longer visible, " +
                $"but some orphaned encrypted chunks remain. Details: {ex.Message}";
            cleanupWarning = string.IsNullOrWhiteSpace(cleanupWarning)
                ? cleanupFailure
                : $"{cleanupWarning} {cleanupFailure}";
        }

        return new VaultMutationResult(lastHash, cleanupWarning, manifestDeleteHash);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            await _js.InvokeVoidAsync("stellarVault.stellar.deleteSession", _sessionId);
        }
        catch (JSDisconnectedException)
        {
            // The browser page is unloading and its JavaScript memory is already gone.
        }

        _rootAccountId = null;
        _disposed = true;
    }

    private async Task RequireCurrentVersionAsync(
        string recordId, string expectedVersion)
    {
        if (expectedVersion is not { Length: 64 } ||
            !expectedVersion.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("A valid pinned version is required.");
        var now = await ReadAsync();
        if (now.Warnings.Count != 0)
            throw new InvalidDataException(
                "Incomplete HD recovery prevents a guarded remote mutation.");
        var matches = now.Records.Where(x =>
            string.Equals(x.Record.Id, recordId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                "Remote record is missing or ambiguous at the final write boundary.");
        var r = matches[0].Record;
        var current = new LedgerRecordItem(r.Id, r.Title, r.Secret,
            r.FolderId, r.Tags, r.IsFolder, r.UserName, r.Url, r.Notes);
        if (!string.Equals(
                VaultHybridRemoteMutationWorkspace.Fingerprint(current),
                expectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Remote version changed after Sync approval. No guarded mutation was submitted.");
    }

    private async Task<List<VaultNodeSnapshot>> LoadChainAsync()
    {
        var jsNodes = await _js.InvokeAsync<JsVaultNode[]>(
            "stellarVault.stellar.loadChain",
            _sessionId,
            MaxChainLength);

        var nodes = new List<VaultNodeSnapshot>(jsNodes.Length);
        foreach (var node in jsNodes)
        {
            var decoded = new Dictionary<string, byte[]>(node.Data.Count, StringComparer.Ordinal);
            foreach (var pair in node.Data)
                decoded[pair.Key] = Convert.FromBase64String(pair.Value);

            nodes.Add(new VaultNodeSnapshot(
                node.AccountId,
                node.Position,
                node.SubentryCount,
                decoded,
                node.NextAccountId));
        }

        if (nodes.Count == 0)
            throw new InvalidDataException("The account chain is empty.");

        return nodes;
    }

    private async Task<VaultNodeSnapshot> CreateAndAppendChildAsync(VaultNodeSnapshot previous)
    {
        if (previous.NextAccountId is not null)
            throw new InvalidOperationException("Only the tail account can be extended.");

        if (!string.Equals(_settings.ChildStartingBalance, "0", StringComparison.Ordinal))
            throw new InvalidOperationException("HD child accounts must start with zero XLM.");

        var node = await _js.InvokeAsync<JsVaultNode>(
            "stellarVault.stellar.createAndAppendChild",
            _sessionId,
            previous.AccountId,
            previous.Position + 1,
            _settings.ChildStartingBalance,
            _settings.BaseFeeStroops,
            _settings.BaseReserveStroops);

        var decoded = new Dictionary<string, byte[]>(node.Data.Count, StringComparer.Ordinal);
        foreach (var pair in node.Data)
            decoded[pair.Key] = Convert.FromBase64String(pair.Value);

        return new VaultNodeSnapshot(
            node.AccountId,
            node.Position,
            node.SubentryCount,
            decoded,
            node.NextAccountId);
    }

    private async Task<string> SubmitWritesAsync(
        IEnumerable<JsManageDataWrite> writes,
        Func<string, Task>? checkpointSignedManifest = null,
        string? finalManifestName = null)
    {
        var materialized = writes.ToArray();
        if (materialized.Length == 0)
            return string.Empty;

        // Resolve every write to its registered deterministic HD index.
        // Index zero is the treasury/registry and must never hold record data.
        var nodes = await LoadChainAsync();
        var indices = nodes.Where(node => node.Position > 0)
            .ToDictionary(node => node.AccountId, node => node.Position, StringComparer.Ordinal);
        foreach (var write in materialized)
        {
            if (!indices.TryGetValue(write.SourceAccountId, out var index))
                throw new InvalidOperationException(
                    "Record write targets an unregistered HD child account.");
            write.NodeIndex = index;
        }

        string lastTransactionHash = string.Empty;
        // V2 sponsorship submits each ManageData operation in its own atomic
        // transaction; never route writes through the legacy transaction signer.
        if ((checkpointSignedManifest is null) != (finalManifestName is null))
            throw new InvalidOperationException("Signed manifest checkpoint scope is incomplete.");
        if (finalManifestName is not null &&
            materialized.Count(x => x.Name == finalManifestName &&
                x.ValueBase64 is not null) != 1)
            throw new InvalidOperationException(
                "Guarded create must contain exactly one final manifest write.");

        foreach (var batch in materialized.Chunk(MaxOperationsPerTransaction))
        {
            // A JS-interop reference is alive only during its own awaited call.
            // Earlier encrypted chunk writes do not identify the final record.
            if (checkpointSignedManifest is not null &&
                batch.Any(x => x.Name == finalManifestName))
            {
                using var checkpoint = DotNetObjectReference.Create(
                    new SignedManifestCheckpoint(checkpointSignedManifest));
                lastTransactionHash = await _js.InvokeAsync<string>(
                    "stellarVault.stellar.submitHdManageData",
                    _sessionId, batch, _settings.BaseFeeStroops,
                    _settings.BaseReserveStroops, checkpoint, finalManifestName);
            }
            else
            {
                lastTransactionHash = await _js.InvokeAsync<string>(
                    "stellarVault.stellar.submitHdManageData",
                    _sessionId, batch, _settings.BaseFeeStroops,
                    _settings.BaseReserveStroops);
            }
        }

        return lastTransactionHash;
    }

    public sealed class SignedManifestCheckpoint(Func<string, Task> persist)
    {
        private int _consumed;

        [JSInvokable]
        public async Task CheckpointSignedManifestAsync(string hash)
        {
            if (hash is not { Length: 64 } || !hash.All(c =>
                    c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
                throw new InvalidOperationException(
                    "Signed final-manifest identity is invalid or already checkpointed.");
            await persist(hash);
        }
    }

    private List<JsManageDataWrite> BuildRecordWrites(
        string accountId,
        string recordId,
        IReadOnlyList<byte[]> chunks,
        byte[] manifest,
        string generation)
    {
        var writes = new List<JsManageDataWrite>(chunks.Count + 1);
        for (var i = 0; i < chunks.Count; i++)
        {
            writes.Add(new JsManageDataWrite
            {
                Name = ChunkName(recordId, i, generation),
                ValueBase64 = Convert.ToBase64String(chunks[i]),
                SourceAccountId = accountId
            });
        }

        // The manifest is intentionally last. Readers won't adopt the new generation
        // until every encrypted chunk has been committed.
        writes.Add(new JsManageDataWrite
        {
            Name = HeaderName(recordId),
            ValueBase64 = Convert.ToBase64String(manifest),
            SourceAccountId = accountId
        });

        return writes;
    }

    private static List<JsManageDataWrite> BuildUpdateCleanupWrites(
        IReadOnlyList<RecordCopy> oldCopies,
        string targetAccountId,
        string recordId,
        string newGeneration)
    {
        var cleanup = new List<JsManageDataWrite>();

        foreach (var copy in oldCopies)
        {
            var isTarget = string.Equals(copy.Node.AccountId, targetAccountId, StringComparison.Ordinal);

            if (!isTarget)
                cleanup.Add(DeleteWrite(copy.Node.AccountId, HeaderName(recordId)));

            foreach (var delete in BuildChunkDeletes(copy.Node.AccountId, recordId, copy.Manifest))
            {
                // Never delete the generation that was just committed.
                if (copy.Manifest.Generation is not null &&
                    string.Equals(copy.Manifest.Generation, newGeneration, StringComparison.Ordinal))
                {
                    continue;
                }

                cleanup.Add(delete);
            }
        }

        return cleanup
            .DistinctBy(write => $"{write.SourceAccountId}|{write.Name}", StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<JsManageDataWrite> BuildChunkDeletes(
        string accountId,
        string recordId,
        RecordManifest manifest)
    {
        for (var i = 0; i < manifest.ChunkCount; i++)
            yield return DeleteWrite(accountId, ChunkName(recordId, i, manifest.Generation));
    }

    private static JsManageDataWrite DeleteWrite(string accountId, string name) =>
        new()
        {
            Name = name,
            ValueBase64 = null,
            SourceAccountId = accountId
        };

    private static List<RecordCopy> FindRecordCopies(
        IReadOnlyList<VaultNodeSnapshot> nodes,
        string recordId)
    {
        var copies = new List<RecordCopy>();
        var headerName = HeaderName(recordId);

        foreach (var node in nodes)
        {
            if (!node.Data.TryGetValue(headerName, out var header))
                continue;

            try
            {
                copies.Add(new RecordCopy(node, ParseManifest(header)));
            }
            catch
            {
                // A malformed header remains visible to ReadAsync as a warning, but it
                // cannot be safely edited because its chunk layout is unknown.
            }
        }

        return copies;
    }

    private bool CanStageRecord(
        VaultNodeSnapshot node,
        int newChunkCount,
        bool headerAlreadyExists)
    {
        // Index zero is exclusively the authenticated registry and treasury.
        // V2 does not reserve an sv:next entry: children are indexed by HD path.
        if (node.Position < 1)
            return false;

        var addedEntries = newChunkCount + (headerAlreadyExists ? 0 : 1);
        var respectsConfiguredDataLimit =
            node.Data.Count + addedEntries <= _settings.NodeDataEntryLimit;
        var respectsProtocolSubentryLimit =
            node.SubentryCount + addedEntries <= 1_000;

        return respectsConfiguredDataLimit && respectsProtocolSubentryLimit;
    }

    private async Task<int> EstimateHdChunkCountAsync(VaultRecord record)
    {
        // AES-GCM output length is invariant across the selected HD child index.
        // Use a temporary v2 envelope to get the exact serialized record size.
        var encrypted = await _crypto.EncryptHdAsync(record, 1, "00000000");
        try
        {
            return (encrypted.Length + ChunkValueSize - 1) / ChunkValueSize;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private void EnsureRecordFitsNode(int chunkCount)
    {
        var permanentEntries = chunkCount + 1;
        if (permanentEntries > _settings.NodeDataEntryLimit)
        {
            throw new InvalidOperationException(
                $"This record needs {permanentEntries} data entries. " +
                $"The configured node limit is {_settings.NodeDataEntryLimit}. " +
                "Increase the limit or reduce the record size; the root sponsors child reserves.");
        }
    }

    private static List<byte[]> SplitIntoChunks(byte[] value)
    {
        var chunks = new List<byte[]>((value.Length + ChunkValueSize - 1) / ChunkValueSize);
        for (var offset = 0; offset < value.Length; offset += ChunkValueSize)
        {
            var length = Math.Min(ChunkValueSize, value.Length - offset);
            chunks.Add(value.AsSpan(offset, length).ToArray());
        }
        return chunks;
    }

    private static byte[] ReassembleEncryptedRecord(
        VaultNodeSnapshot node,
        string recordId,
        RecordManifest manifest)
    {
        var encrypted = new byte[manifest.EncryptedLength];
        var written = 0;

        for (var i = 0; i < manifest.ChunkCount; i++)
        {
            var name = ChunkName(recordId, i, manifest.Generation);
            if (!node.Data.TryGetValue(name, out var chunk))
                throw new InvalidDataException($"Missing encrypted chunk {i} of {manifest.ChunkCount}.");

            if (chunk.Length > ChunkValueSize)
                throw new InvalidDataException($"Chunk {i} is larger than {ChunkValueSize} bytes.");
            if (written + chunk.Length > encrypted.Length)
                throw new InvalidDataException("Encrypted chunks exceed the manifest length.");

            chunk.CopyTo(encrypted, written);
            written += chunk.Length;
        }

        if (written != encrypted.Length)
            throw new InvalidDataException($"Expected {encrypted.Length} encrypted bytes but reconstructed {written}.");

        return encrypted;
    }

    private async Task<byte[]> CreateManifestAsync(
        byte[] encrypted,
        int chunkCount,
        long updatedUnixSeconds,
        string generation)
    {
        if (chunkCount is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(chunkCount));

        var generationBytes = Convert.FromHexString(generation);
        if (generationBytes.Length != 4)
            throw new InvalidOperationException("Record generations must be four bytes.");

        var manifest = new byte[CurrentManifestLength];
        manifest[0] = CurrentManifestVersion;
        BinaryPrimitives.WriteUInt16BigEndian(manifest.AsSpan(1, 2), checked((ushort)chunkCount));
        BinaryPrimitives.WriteInt32BigEndian(manifest.AsSpan(3, 4), encrypted.Length);

        var hash = await _crypto.HashAsync(encrypted);
        try
        {
            hash.CopyTo(manifest, 7);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }

        BinaryPrimitives.WriteInt64BigEndian(manifest.AsSpan(39, 8), updatedUnixSeconds);
        generationBytes.CopyTo(manifest, 47);
        CryptographicOperations.ZeroMemory(generationBytes);

        return manifest;
    }

    private static RecordManifest ParseManifest(byte[] value)
    {
        if (value.Length is not LegacyManifestLength and not CurrentManifestLength)
            throw new InvalidDataException($"Invalid manifest length: {value.Length}.");

        var version = value[0];
        if (version is not LegacyManifestVersion and not CurrentManifestVersion)
            throw new InvalidDataException($"Unsupported manifest version: {version}.");

        if (version == LegacyManifestVersion && value.Length != LegacyManifestLength)
            throw new InvalidDataException("Legacy manifest has an invalid length.");
        if (version == CurrentManifestVersion && value.Length != CurrentManifestLength)
            throw new InvalidDataException("Current manifest has an invalid length.");

        var chunkCount = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(1, 2));
        var encryptedLength = BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(3, 4));
        var hash = value.AsSpan(7, 32).ToArray();
        var updated = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(39, 8));
        var generation = version == CurrentManifestVersion
            ? Convert.ToHexString(value.AsSpan(47, 4)).ToLowerInvariant()
            : null;

        if (chunkCount == 0)
            throw new InvalidDataException("The manifest contains zero chunks.");
        if (encryptedLength <= 0 || encryptedLength > chunkCount * ChunkValueSize)
            throw new InvalidDataException("The manifest contains an invalid encrypted length.");

        return new RecordManifest(version, chunkCount, encryptedLength, hash, updated, generation);
    }

    private static string CreateGeneration(string? generationToAvoid = null)
    {
        string generation;
        do
        {
            generation = Guid.NewGuid().ToString("N")[..8];
        }
        while (string.Equals(generation, generationToAvoid, StringComparison.Ordinal));

        return generation;
    }

    private static string HeaderName(string recordId) => HeaderPrefix + recordId;

    private static string ChunkName(string recordId, int index, string? generation) =>
        generation is null
            ? $"{ChunkPrefix}{recordId}:{index.ToString("D4", CultureInfo.InvariantCulture)}"
            : $"{ChunkPrefix}{recordId}:{generation}:{index.ToString("D4", CultureInfo.InvariantCulture)}";

    private static void ValidateRecord(VaultRecord record)
    {
        ValidateRecordId(record.Id);
        if (string.IsNullOrWhiteSpace(record.Title))
            throw new ArgumentException("Title is required.", nameof(record));
        if (string.IsNullOrEmpty(record.Secret))
            throw new ArgumentException("Secret is required.", nameof(record));
    }

    private static void ValidateRecordId(string id)
    {
        if (id.Length != 16 || id.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Record IDs must be 16 hexadecimal characters.");
    }

    private static string ShortAccount(string value) =>
        value.Length <= 12 ? value : $"{value[..8]}…";

    private decimal BaseReserveNative =>
        Math.Max(1L, _settings.BaseReserveStroops) / 10_000_000m;

    private decimal FeeNative(int operationCount) =>
        operationCount * _settings.BaseFeeStroops / 10_000_000m;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record RecordCopy(VaultNodeSnapshot Node, RecordManifest Manifest);

    private sealed class JsSessionInfo
    {
        public string RootAccountId { get; set; } = string.Empty;
        public bool RegistryInitialized { get; set; }
    }

    private sealed class JsVaultNode
    {
        public string AccountId { get; set; } = string.Empty;
        public int Position { get; set; }
        public int SubentryCount { get; set; }
        public Dictionary<string, string> Data { get; set; } = new(StringComparer.Ordinal);
        public string? NextAccountId { get; set; }
    }

    private sealed class JsManageDataWrite
    {
        public int NodeIndex { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? ValueBase64 { get; set; }
        public string SourceAccountId { get; set; } = string.Empty;
    }
}
