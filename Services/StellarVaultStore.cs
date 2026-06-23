using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.JSInterop;
using StellarDotnetSdk;
using StellarDotnetSdk.Accounts;
using StellarDotnetSdk.Operations;
using StellarDotnetSdk.Responses;
using StellarDotnetSdk.Transactions;
using StellarVaultPwa.Core;
using StellarNetwork = StellarDotnetSdk.Network;
using StellarOperation = StellarDotnetSdk.Operations.Operation;

namespace StellarVaultPwa.Services;

public sealed class StellarVaultStore : IAsyncDisposable
{
    private const string NextPointerName = "sv:next";
    private const string HeaderPrefix = "svh:";
    private const string ChunkPrefix = "svc:";
    private const int ChunkValueSize = 64;
    private const int MaxOperationsPerTransaction = 90;
    private const int MaxChainLength = 64;
    private const byte ManifestVersion = 1;
    private const int ManifestLength = 47;

    private readonly VaultNetworkSettings _settings;
    private readonly KeyPair _root;
    private readonly StellarNetwork _network;
    private readonly Server _server;
    private readonly VaultCrypto _crypto;
    private bool _disposed;

    public StellarVaultStore(VaultNetworkSettings settings, KeyPair root, IJSRuntime js)
    {
        _settings = settings with { HorizonUrl = settings.NormalizedHorizonUrl };
        _root = root;
        _network = new StellarNetwork(settings.Passphrase);
        _server = new Server(_settings.HorizonUrl);
        _crypto = new VaultCrypto(js, root, _settings);
    }

    public string RootAccountId => _root.AccountId;
    public VaultNetworkSettings Settings => _settings;

    public async Task OpenAsync()
    {
        ThrowIfDisposed();
        await _crypto.InitializeAsync();
        _ = await GetAccountAsync(_root.AccountId);
    }

    public async Task<VaultReadResult> ReadAsync()
    {
        ThrowIfDisposed();

        var nodes = await LoadChainAsync();
        var records = new List<VaultRecordLocation>();
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

                        records.Add(new VaultRecordLocation(
                            record,
                            node.AccountId,
                            manifest.ChunkCount,
                            manifest.EncryptedLength));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(encrypted);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"{node.AccountId[..8]}… / {recordId}: {ex.Message}");
                }
            }
        }

        return new VaultReadResult(
            records.OrderBy(x => x.Record.Title, StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings,
            nodes);
    }

    public async Task<string> AddAsync(VaultRecord record)
    {
        ThrowIfDisposed();
        ValidateRecord(record);

        var encrypted = await _crypto.EncryptAsync(record);
        try
        {
            var chunks = SplitIntoChunks(encrypted);
            var requiredDataEntries = chunks.Count + 1;

            if (requiredDataEntries + 1 > _settings.NodeDataEntryLimit)
            {
                throw new InvalidOperationException(
                    $"This record needs {requiredDataEntries} data entries. " +
                    $"The configured node limit is {_settings.NodeDataEntryLimit}, and one entry is reserved for the next-node pointer. " +
                    "Increase the node limit and fund child accounts with enough native balance.");
            }

            var nodes = await LoadChainAsync();
            var target = nodes[^1];

            if (target.Data.Count + requiredDataEntries + 1 > _settings.NodeDataEntryLimit)
                target = await CreateAndAppendChildAsync(target);

            var operations = new List<StellarOperation>(requiredDataEntries);
            var source = KeyPair.FromAccountId(target.AccountId);

            for (var i = 0; i < chunks.Count; i++)
                operations.Add(new ManageDataOperation(ChunkName(record.Id, i), chunks[i], source));

            var manifest = await CreateManifestAsync(encrypted, chunks.Count, record.UpdatedUtc);
            operations.Add(new ManageDataOperation(HeaderName(record.Id), manifest, source));

            string lastTransactionHash = string.Empty;
            foreach (var batch in operations.Chunk(MaxOperationsPerTransaction))
                lastTransactionHash = await SubmitOperationsAsync(batch);

            return lastTransactionHash;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await _crypto.DisposeAsync();
        _server.Dispose();
        if (_root.SeedBytes is { } seed)
            CryptographicOperations.ZeroMemory(seed);
        _disposed = true;
    }

    private async Task<List<VaultNodeSnapshot>> LoadChainAsync()
    {
        var nodes = new List<VaultNodeSnapshot>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = _root.AccountId;

        while (true)
        {
            if (!visited.Add(current))
                throw new InvalidDataException("The vault account chain contains a cycle.");
            if (nodes.Count >= MaxChainLength)
                throw new InvalidDataException($"The vault chain exceeds the safety limit of {MaxChainLength} accounts.");

            var account = await GetAccountAsync(current);
            var data = DecodeData(account.Data);
            string? next = null;

            if (data.TryGetValue(NextPointerName, out var pointerBytes))
            {
                if (pointerBytes.Length != 32)
                    throw new InvalidDataException($"Account {current} has an invalid {NextPointerName} pointer.");

                next = KeyPair.FromPublicKey(pointerBytes).AccountId;
            }

            nodes.Add(new VaultNodeSnapshot(
                account.AccountId,
                nodes.Count,
                account.SubentryCount,
                data,
                next));

            if (next is null)
                return nodes;

            current = next;
        }
    }

    private async Task<VaultNodeSnapshot> CreateAndAppendChildAsync(VaultNodeSnapshot previous)
    {
        if (previous.Data.ContainsKey(NextPointerName))
            throw new InvalidOperationException("Only the tail account can be extended.");

        var child = KeyPair.Random();
        try
        {
            var rootAccount = await GetAccountAsync(_root.AccountId);
            var previousSource = KeyPair.FromAccountId(previous.AccountId);

            var configureChild = new SetOptionsOperation(child)
                .SetSigner(_root.AccountId, 1)
                .SetMasterKeyWeight(0)
                .SetLowThreshold(1)
                .SetMediumThreshold(1)
                .SetHighThreshold(1);

            var transaction = new TransactionBuilder(rootAccount)
                .SetFee(_settings.BaseFeeStroops)
                .AddOperation(new CreateAccountOperation(child, _settings.ChildStartingBalance))
                .AddOperation(configureChild)
                .AddOperation(new ManageDataOperation(NextPointerName, child.PublicKey, previousSource))
                .Build();

            transaction.Sign(_root, _network);
            transaction.Sign(child, _network);
            await SubmitAsync(transaction);

            var childAccount = await GetAccountAsync(child.AccountId);
            return new VaultNodeSnapshot(
                childAccount.AccountId,
                previous.Position + 1,
                childAccount.SubentryCount,
                DecodeData(childAccount.Data),
                null);
        }
        finally
        {
            if (child.SeedBytes is { } childSeed)
                CryptographicOperations.ZeroMemory(childSeed);
        }
    }

    private async Task<string> SubmitOperationsAsync(IEnumerable<StellarOperation> operations)
    {
        var rootAccount = await GetAccountAsync(_root.AccountId);
        var builder = new TransactionBuilder(rootAccount).SetFee(_settings.BaseFeeStroops);

        foreach (var operation in operations)
            builder.AddOperation(operation);

        var transaction = builder.Build();
        transaction.Sign(_root, _network);
        return await SubmitAsync(transaction);
    }

    private async Task<string> SubmitAsync(Transaction transaction)
    {
        var response = await _server.SubmitTransaction(transaction);
        if (response?.IsSuccess == true)
            return response.Hash ?? string.Empty;

        var transactionCode = response?.SubmitTransactionResponseExtras?
            .ExtrasResultCodes?.TransactionResultCode ?? "unknown";
        var operationCodes = response?.SubmitTransactionResponseExtras?
            .ExtrasResultCodes?.OperationsResultCodes;

        var details = operationCodes is { Count: > 0 }
            ? $" Operations: {string.Join(", ", operationCodes)}"
            : string.Empty;

        throw new InvalidOperationException($"Stellar transaction failed: {transactionCode}.{details}");
    }

    private Task<AccountResponse> GetAccountAsync(string accountId) =>
        _server.Accounts.Account(accountId);

    private static Dictionary<string, byte[]> DecodeData(Dictionary<string, string> encodedData)
    {
        var result = new Dictionary<string, byte[]>(encodedData.Count, StringComparer.Ordinal);
        foreach (var pair in encodedData)
            result[pair.Key] = Convert.FromBase64String(pair.Value);
        return result;
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
            var name = ChunkName(recordId, i);
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

    private async Task<byte[]> CreateManifestAsync(byte[] encrypted, int chunkCount, DateTimeOffset updatedUtc)
    {
        if (chunkCount is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(chunkCount));

        var manifest = new byte[ManifestLength];
        manifest[0] = ManifestVersion;
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

        BinaryPrimitives.WriteInt64BigEndian(
            manifest.AsSpan(39, 8),
            updatedUtc.ToUnixTimeSeconds());

        return manifest;
    }

    private static RecordManifest ParseManifest(byte[] value)
    {
        if (value.Length != ManifestLength)
            throw new InvalidDataException($"Invalid manifest length: {value.Length}.");
        if (value[0] != ManifestVersion)
            throw new InvalidDataException($"Unsupported manifest version: {value[0]}.");

        var chunkCount = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(1, 2));
        var encryptedLength = BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(3, 4));
        var hash = value.AsSpan(7, 32).ToArray();
        var updated = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(39, 8));

        if (chunkCount == 0)
            throw new InvalidDataException("The manifest contains zero chunks.");
        if (encryptedLength <= 0 || encryptedLength > chunkCount * ChunkValueSize)
            throw new InvalidDataException("The manifest contains an invalid encrypted length.");

        return new RecordManifest(value[0], chunkCount, encryptedLength, hash, updated);
    }

    private static string HeaderName(string recordId) => HeaderPrefix + recordId;

    private static string ChunkName(string recordId, int index) =>
        $"{ChunkPrefix}{recordId}:{index.ToString("D4", CultureInfo.InvariantCulture)}";

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
