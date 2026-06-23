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
    private const byte ManifestVersion = 1;
    private const int ManifestLength = 47;

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

    public async Task OpenAsync(string secret)
    {
        ThrowIfDisposed();
        if (_rootAccountId is not null)
            throw new InvalidOperationException("The Stellar session is already open.");

        var result = await _js.InvokeAsync<JsSessionInfo>(
            "stellarVault.stellar.createSession",
            _sessionId,
            secret,
            _settings.HorizonUrl,
            _settings.Passphrase);

        if (string.IsNullOrWhiteSpace(result.RootAccountId))
            throw new InvalidOperationException("The browser Stellar SDK did not return a root account ID.");

        _rootAccountId = result.RootAccountId;
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

            var writes = new List<JsManageDataWrite>(requiredDataEntries);
            for (var i = 0; i < chunks.Count; i++)
            {
                writes.Add(new JsManageDataWrite
                {
                    Name = ChunkName(record.Id, i),
                    ValueBase64 = Convert.ToBase64String(chunks[i]),
                    SourceAccountId = target.AccountId
                });
            }

            var manifest = await CreateManifestAsync(encrypted, chunks.Count, record.UpdatedUtc);
            writes.Add(new JsManageDataWrite
            {
                Name = HeaderName(record.Id),
                ValueBase64 = Convert.ToBase64String(manifest),
                SourceAccountId = target.AccountId
            });

            string lastTransactionHash = string.Empty;
            foreach (var batch in writes.Chunk(MaxOperationsPerTransaction))
            {
                lastTransactionHash = await _js.InvokeAsync<string>(
                    "stellarVault.stellar.submitManageData",
                    _sessionId,
                    batch,
                    _settings.BaseFeeStroops);
            }

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
            _settings.BaseFeeStroops);

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
        public string Name { get; set; } = string.Empty;
        public string ValueBase64 { get; set; } = string.Empty;
        public string SourceAccountId { get; set; } = string.Empty;
    }
}
