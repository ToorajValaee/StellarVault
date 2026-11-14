using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Native-host encrypted queue storage. Every entry and its metadata is sealed
/// with the current unlocked vault key and account/network/id-associated data.
/// Exclusive process locks protect read-modify-write CAS across cooperating
/// app instances; local files are not an anti-rollback anchor.
/// </summary>
public sealed class FileSystemVaultPendingMutationStore : IVaultPendingMutationStore
{
    private const int MaximumQueueFileBytes = 12 * 1024 * 1024;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private readonly VaultSessionManager sessions;
    private readonly VaultDeviceRetentionGate retention;
    private readonly string directory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string lockPath;

    public FileSystemVaultPendingMutationStore(
        VaultSessionManager sessions, VaultDeviceRetentionGate retention, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.sessions = sessions;
        this.retention = retention;
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        lockPath = Path.Combine(this.directory, ".queue.lock");
    }

    public Task<VaultPendingMutation?> GetAsync(
        string accountId, string networkId, string mutationId, CancellationToken ct = default) =>
        WithKeyAsync(accountId, networkId, async (key, token) =>
        {
            await gate.WaitAsync(token);
            try
            {
                var stored = await ReadBlobAsync(PathFor(accountId, networkId, mutationId), token);
                if (stored is null) return null;
                try { return Decode(stored, key.Span, accountId, networkId, mutationId); }
                finally { CryptographicOperations.ZeroMemory(stored); }
            }
            finally { gate.Release(); }
        }, ct);

    public Task<IReadOnlyList<VaultPendingMutation>> ListAsync(
        string accountId, string networkId, CancellationToken ct = default) =>
        WithKeyAsync<IReadOnlyList<VaultPendingMutation>>(accountId, networkId,
            async (key, token) =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var prefix = ScopePrefix(accountId, networkId);
                    var result = new List<VaultPendingMutation>();
                    // Scope hash and random mutation ID only; filenames contain
                    // no plaintext title, content, folder, or transaction hash.
                    foreach (var path in Directory.EnumerateFiles(directory, prefix + "*.queue")
                        .OrderBy(x => x, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(path);
                        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                            !name.EndsWith(".queue", StringComparison.Ordinal))
                            throw new InvalidDataException("Invalid queue filename.");
                        var mutationId = name[prefix.Length..^6];
                        if (!Guid.TryParseExact(mutationId, "N", out _))
                            throw new InvalidDataException("Invalid scoped mutation filename.");
                        var stored = await ReadBlobAsync(path, token)
                            ?? throw new InvalidDataException("Queue entry disappeared during reading.");
                        try { result.Add(Decode(stored, key.Span, accountId, networkId, mutationId)); }
                        finally { CryptographicOperations.ZeroMemory(stored); }
                    }
                    return result;
                }
                finally { gate.Release(); }
            }, ct);

    public Task<bool> AddAsync(VaultPendingMutation entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return WithKeyAsync(entry.AccountId, entry.NetworkId, async (key, token) =>
        {
            var blob = Encode(entry, key.Span);
            try
            {
                await gate.WaitAsync(token);
                try
                {
                    await using var exclusive = await AcquireLockAsync(token);
                    var target = PathFor(entry.AccountId, entry.NetworkId, entry.Id);
                    if (File.Exists(target)) return false;
                    return await WriteAtomicAsync(target, blob, overwrite: false, token);
                }
                finally { gate.Release(); }
            }
            finally { CryptographicOperations.ZeroMemory(blob); }
        }, ct);
    }

    public Task<bool> TryReplaceAsync(VaultPendingMutation previous,
        VaultPendingMutation replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        if (previous.Id != replacement.Id || previous.AccountId != replacement.AccountId ||
            previous.NetworkId != replacement.NetworkId)
            throw new InvalidOperationException("Queue identity cannot change.");

        return WithKeyAsync(previous.AccountId, previous.NetworkId, async (key, token) =>
        {
            await gate.WaitAsync(token);
            try
            {
                await using var exclusive = await AcquireLockAsync(token);
                var target = PathFor(previous.AccountId, previous.NetworkId, previous.Id);
                var stored = await ReadBlobAsync(target, token);
                if (stored is null) return false;
                try
                {
                    var actual = Decode(stored, key.Span, previous.AccountId, previous.NetworkId, previous.Id);
                    if (!Equivalent(actual, previous)) return false;
                    var replacementBlob = Encode(replacement, key.Span);
                    try { return await WriteAtomicAsync(target, replacementBlob, overwrite: true, token); }
                    finally { CryptographicOperations.ZeroMemory(replacementBlob); }
                }
                finally { CryptographicOperations.ZeroMemory(stored); }
            }
            finally { gate.Release(); }
        }, ct);
    }

    public Task<bool> TryDeleteAsync(VaultPendingMutation expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return WithKeyAsync(expected.AccountId, expected.NetworkId, async (key, token) =>
        {
            await gate.WaitAsync(token);
            try
            {
                await using var exclusive = await AcquireLockAsync(token);
                var target = PathFor(expected.AccountId, expected.NetworkId, expected.Id);
                var stored = await ReadBlobAsync(target, token);
                if (stored is null) return false;
                try
                {
                    var actual = Decode(stored, key.Span, expected.AccountId, expected.NetworkId, expected.Id);
                    if (!Equivalent(actual, expected)) return false;
                    File.Delete(target);
                    return true;
                }
                finally { CryptographicOperations.ZeroMemory(stored); }
            }
            finally { gate.Release(); }
        }, ct);
    }

    private async Task<T> WithKeyAsync<T>(string accountId, string networkId,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        retention.RequireLocalPersistence();
        StellarAccountAddress.Validate(accountId);
        _ = ScopePrefix(accountId, networkId);
        if (sessions.ActiveAccountId != accountId)
            throw new UnauthorizedAccessException("Queue account does not match the unlocked vault.");
        return await sessions.ExecuteAsync(async (_, key, token) =>
        {
            token.ThrowIfCancellationRequested();
            return await action(key, token);
        }, ct);
    }

    private string PathFor(string account, string network, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid queue mutation identifier.", nameof(id));
        return Path.Combine(directory, ScopePrefix(account, network) + id + ".queue");
    }

    private static string ScopePrefix(string account, string network)
    {
        StellarAccountAddress.Validate(account);
        if (string.IsNullOrWhiteSpace(network) || network.Length > 512)
            throw new ArgumentException("Invalid network namespace.", nameof(network));
        var name = Encoding.UTF8.GetBytes(network);
        try { return account + "_" + Convert.ToHexStringLower(SHA256.HashData(name)) + "_"; }
        finally { CryptographicOperations.ZeroMemory(name); }
    }

    private static byte[] AssociatedData(string account, string network, string id) =>
        Encoding.UTF8.GetBytes("StellarVault:queue:v1:" + account + ":" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(network))) + ":" + id);

    private static byte[] Encode(VaultPendingMutation entry, ReadOnlySpan<byte> key)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(entry);
        try
        {
            if (plain.Length > 12 * 1024 * 1024)
                throw new InvalidDataException("Serialized queue mutation exceeds the size limit.");
            return VaultCryptoPrimitives.Seal(key, plain,
                AssociatedData(entry.AccountId, entry.NetworkId, entry.Id));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static VaultPendingMutation Decode(byte[] ciphertext, ReadOnlySpan<byte> key,
        string account, string network, string id)
    {
        var plain = VaultCryptoPrimitives.Open(key, ciphertext, AssociatedData(account, network, id));
        try
        {
            var item = JsonSerializer.Deserialize<VaultPendingMutation>(plain)
                ?? throw new InvalidDataException("Queue entry was empty.");
            if (item.Id != id || item.AccountId != account || item.NetworkId != network ||
                item.EncryptedPayload is not { Length: > 0 and <= 8_388_608 } ||
                item.DependsOn is null)
                throw new CryptographicException("Queue entry has invalid authenticated identity.");
            VaultMutationStrategyPolicy.Validate(item.OriginStrategyId);
            return item;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LockTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None); }
            catch (IOException)
            {
                try { await Task.Delay(25, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new TimeoutException("Timed out waiting for the native queue lock."); }
            }
        }
    }

    private static async Task<byte[]?> ReadBlobAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var source = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
            if (source.Length < VaultCryptoPrimitives.NonceLength + VaultCryptoPrimitives.TagLength ||
                source.Length > MaximumQueueFileBytes)
                throw new CryptographicException("Encrypted queue entry size is invalid.");
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var count = await source.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    if (output.Length + count > MaximumQueueFileBytes)
                        throw new CryptographicException("Encrypted queue entry exceeded the size limit.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        catch (FileNotFoundException) { return null; }
    }

    private async Task<bool> WriteAtomicAsync(string target, byte[] blob,
        bool overwrite, CancellationToken ct)
    {
        if (blob.Length > MaximumQueueFileBytes)
            throw new InvalidDataException("Encrypted queue entry exceeds the size limit.");
        var temp = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(blob, ct);
                await stream.FlushAsync(ct);
            }
            if (!overwrite && File.Exists(target)) return false;
            File.Move(temp, target, overwrite);
            return true;
        }
        catch (IOException) when (!overwrite && File.Exists(target)) { return false; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool Equivalent(VaultPendingMutation a, VaultPendingMutation b) =>
        a.Id == b.Id && a.AccountId == b.AccountId && a.NetworkId == b.NetworkId &&
        a.Kind == b.Kind && a.EntityId == b.EntityId &&
        a.OriginStrategyId == b.OriginStrategyId &&
        a.EncryptedPayload.AsSpan().SequenceEqual(b.EncryptedPayload) &&
        a.ExpectedRemoteVersion == b.ExpectedRemoteVersion &&
        a.DependsOn.SequenceEqual(b.DependsOn) && a.State == b.State &&
        a.TransactionHash == b.TransactionHash && a.CreatedUtc == b.CreatedUtc &&
        (a.SignedDeletionManifests ?? []).SequenceEqual(b.SignedDeletionManifests ?? []) &&
        (a.SignedFileEntries ?? []).SequenceEqual(b.SignedFileEntries ?? []);
}
