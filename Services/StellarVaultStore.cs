using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.JSInterop;
using StellarVaultPwa.Core;

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

                        var record = await _crypto.DecryptAsync(recordId, encrypted);
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

        var encrypted = await _crypto.EncryptAsync(record);
        try
        {
            var chunkCount = SplitIntoChunks(encrypted).Count;
            EnsureRecordFitsNode(chunkCount);

            var createsChild = !CanStageRecord(nodes[^1], chunkCount, headerAlreadyExists: false);
            var recordWrites = chunkCount + 1;
            var createOperations = createsChild ? 3 : 0;
            var dataEntriesAdded = recordWrites + (createsChild ? 1 : 0); // sv:next on the old tail
            var structuralReserves = createsChild ? 3 : 0; // child account: 2 base reserves + root signer
            var reserve = BaseReserveNative;
            var operationCount = createOperations + recordWrites;
            var transactionCount = (createsChild ? 1 : 0) + TransactionCount(recordWrites);

            return new VaultCostEstimate(
                operationCount,
                transactionCount,
                chunkCount,
                dataEntriesAdded,
                0,
                FeeNative(operationCount),
                (dataEntriesAdded + structuralReserves) * reserve,
                (dataEntriesAdded + structuralReserves) * reserve,
                createsChild ? ParseChildStartingBalance() : 0m,
                createsChild);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
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

        var encrypted = await _crypto.EncryptAsync(record);
        try
        {
            var chunkCount = SplitIntoChunks(encrypted).Count;
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

            var commitOperations = chunkCount + 1;
            var createOperations = createsChild ? 3 : 0;
            var dataEntriesAdded = chunkCount + (targetHasHeader ? 0 : 1) + (createsChild ? 1 : 0);
            var structuralReserves = createsChild ? 3 : 0;
            var operationCount = createOperations + commitOperations + cleanupCount;
            var transactionCount = (createsChild ? 1 : 0)
                                   + TransactionCount(commitOperations)
                                   + TransactionCount(cleanupCount);
            var reserve = BaseReserveNative;
            var peakReserve = (dataEntriesAdded + structuralReserves) * reserve;
            var netReserve = peakReserve - cleanupCount * reserve;

            return new VaultCostEstimate(
                operationCount,
                transactionCount,
                chunkCount,
                dataEntriesAdded,
                cleanupCount,
                FeeNative(operationCount),
                peakReserve,
                netReserve,
                createsChild ? ParseChildStartingBalance() : 0m,
                createsChild);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
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

        var readableCopies = FindRecordCopies(nodes, expectedLocation.Record.Id);
        var chunkDeleteCount = readableCopies.Sum(copy => (int)copy.Manifest.ChunkCount);
        var operationCount = headerCount + chunkDeleteCount;
        var transactionCount = TransactionCount(headerCount) + TransactionCount(chunkDeleteCount);
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

    public async Task<string> AddAsync(VaultRecord record)
    {
        ThrowIfDisposed();
        ValidateRecord(record);

        var nodes = await LoadChainAsync();
        if (nodes.Any(node => node.Data.ContainsKey(HeaderName(record.Id))))
            throw new InvalidOperationException($"A record with ID {record.Id} already exists.");

        var encrypted = await _crypto.EncryptAsync(record);
        try
        {
            var chunks = SplitIntoChunks(encrypted);
            EnsureRecordFitsNode(chunks.Count);

            var target = nodes[^1];
            if (!CanStageRecord(target, chunks.Count, headerAlreadyExists: false))
                target = await CreateAndAppendChildAsync(target);

            var generation = CreateGeneration();
            var manifest = await CreateManifestAsync(
                encrypted,
                chunks.Count,
                record.UpdatedUtc.ToUnixTimeSeconds(),
                generation);

            var writes = BuildRecordWrites(target.AccountId, record.Id, chunks, manifest, generation);
            return await SubmitWritesAsync(writes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<VaultMutationResult> UpdateAsync(
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

        var encrypted = await _crypto.EncryptAsync(record);
        try
        {
            var chunks = SplitIntoChunks(encrypted);
            EnsureRecordFitsNode(chunks.Count);

            var generation = CreateGeneration(currentCopy.Manifest.Generation);
            var updatedSeconds = Math.Max(
                record.UpdatedUtc.ToUnixTimeSeconds(),
                currentCopy.Manifest.UpdatedUnixSeconds + 1);

            var source = currentCopy.Node;
            VaultNodeSnapshot target;

            if (CanStageRecord(source, chunks.Count, headerAlreadyExists: true))
            {
                target = source;
            }
            else
            {
                target = nodes[^1];
                var targetHasHeader = target.Data.ContainsKey(HeaderName(record.Id));

                if (!CanStageRecord(target, chunks.Count, targetHasHeader))
                    target = await CreateAndAppendChildAsync(target);
            }

            var manifest = await CreateManifestAsync(
                encrypted,
                chunks.Count,
                updatedSeconds,
                generation);

            var commitHash = await SubmitWritesAsync(
                BuildRecordWrites(target.AccountId, record.Id, chunks, manifest, generation));

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

            return new VaultMutationResult(lastHash, cleanupWarning);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<VaultMutationResult> DeleteAsync(VaultRecordLocation expectedLocation)
    {
        ThrowIfDisposed();
        ValidateRecordId(expectedLocation.Record.Id);

        var nodes = await LoadChainAsync();
        var headerName = HeaderName(expectedLocation.Record.Id);
        var headerNodes = nodes
            .Where(node => node.Data.ContainsKey(headerName))
            .ToArray();

        if (headerNodes.Length == 0)
            throw new InvalidOperationException("The record no longer exists.");

        var readableCopies = FindRecordCopies(nodes, expectedLocation.Record.Id);
        var headerDeletes = headerNodes
            .Select(node => DeleteWrite(node.AccountId, headerName))
            .DistinctBy(write => $"{write.SourceAccountId}|{write.Name}", StringComparer.Ordinal)
            .ToArray();

        var lastHash = await SubmitWritesAsync(headerDeletes);
        string? cleanupWarning = readableCopies.Count < headerNodes.Length
            ? "The record manifest was deleted, but at least one malformed copy could not be mapped to its encrypted chunks."
            : null;

        try
        {
            var chunkDeletes = readableCopies
                .SelectMany(copy => BuildChunkDeletes(
                    copy.Node.AccountId,
                    expectedLocation.Record.Id,
                    copy.Manifest))
                .DistinctBy(write => $"{write.SourceAccountId}|{write.Name}", StringComparer.Ordinal)
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

        return new VaultMutationResult(lastHash, cleanupWarning);
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

    private async Task<string> SubmitWritesAsync(IEnumerable<JsManageDataWrite> writes)
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
        foreach (var batch in materialized.Chunk(MaxOperationsPerTransaction))
        {
            lastTransactionHash = await _js.InvokeAsync<string>(
                "stellarVault.stellar.submitHdManageData",
                _sessionId,
                batch,
                _settings.BaseFeeStroops,
                _settings.BaseReserveStroops);
        }

        return lastTransactionHash;
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
        var addedEntries = newChunkCount + (headerAlreadyExists ? 0 : 1);
        var reservedNextPointer = node.NextAccountId is null ? 1 : 0;
        var respectsConfiguredDataLimit =
            node.Data.Count + addedEntries + reservedNextPointer <= _settings.NodeDataEntryLimit;
        var respectsProtocolSubentryLimit =
            node.SubentryCount + addedEntries + reservedNextPointer <= 1_000;

        return respectsConfiguredDataLimit && respectsProtocolSubentryLimit;
    }

    private void EnsureRecordFitsNode(int chunkCount)
    {
        var permanentEntries = chunkCount + 1;
        if (permanentEntries + 1 > _settings.NodeDataEntryLimit)
        {
            throw new InvalidOperationException(
                $"This record needs {permanentEntries} data entries. " +
                $"The configured node limit is {_settings.NodeDataEntryLimit}, and one entry is reserved for the next-node pointer. " +
                "Increase the node limit and fund child accounts with enough native balance.");
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

    private decimal ParseChildStartingBalance() =>
        decimal.TryParse(
            _settings.ChildStartingBalance,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0m;

    private static int TransactionCount(int operationCount) =>
        operationCount <= 0
            ? 0
            : (operationCount + MaxOperationsPerTransaction - 1) / MaxOperationsPerTransaction;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record RecordCopy(VaultNodeSnapshot Node, RecordManifest Manifest);

    private sealed class JsSessionInfo
    {
        public string RootAccountId { get; set; } = string.Empty;
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
