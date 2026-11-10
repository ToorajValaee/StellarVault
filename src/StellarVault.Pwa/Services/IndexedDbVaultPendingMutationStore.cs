using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Durable per-device mutation queue. Whole entries, including operation metadata,
/// are AES-GCM encrypted with the unlocked vault key. The IndexedDB key contains
/// only the account/network scope and random mutation ID for enumeration.
/// </summary>
public sealed class IndexedDbVaultPendingMutationStore(
    IJSRuntime js, VaultSessionManager sessions, VaultDeviceRetentionGate retention)
    : IVaultPendingMutationStore
{
    private sealed record QueueEntry(string Id, byte[] Data);

    public Task<VaultPendingMutation?> GetAsync(
        string accountId, string networkId, string mutationId, CancellationToken ct = default) =>
        WithKeyAsync(accountId, async key =>
        {
            var bytes = await js.InvokeAsync<byte[]?>("stellarVaultV2Db.readQueue", ct,
                StorageId(accountId, networkId, mutationId));
            return bytes is null ? null : Decode(bytes, key, accountId, networkId, mutationId);
        }, ct);

    public Task<IReadOnlyList<VaultPendingMutation>> ListAsync(
        string accountId, string networkId, CancellationToken ct = default) =>
        WithKeyAsync<IReadOnlyList<VaultPendingMutation>>(accountId, async key =>
        {
            var entries = await js.InvokeAsync<QueueEntry[]>("stellarVaultV2Db.listQueue", ct,
                ScopeId(accountId, networkId));
            var result = new List<VaultPendingMutation>();
            foreach (var entry in entries)
                result.Add(Decode(entry.Data, key, accountId, networkId, entry.Id));
            return result;
        }, ct);

    public Task<bool> AddAsync(VaultPendingMutation entry, CancellationToken ct = default) =>
        WithKeyAsync(entry.AccountId, async key =>
        {
            byte[] blob = Encode(entry, key);
            try
            {
                return await js.InvokeAsync<bool>("stellarVaultV2Db.addQueue", ct,
                    StorageId(entry.AccountId, entry.NetworkId, entry.Id), blob);
            }
            finally { CryptographicOperations.ZeroMemory(blob); }
        }, ct);

    public Task<bool> TryReplaceAsync(VaultPendingMutation previous,
        VaultPendingMutation replacement, CancellationToken ct = default)
    {
        if (previous.Id != replacement.Id || previous.AccountId != replacement.AccountId ||
            previous.NetworkId != replacement.NetworkId)
            throw new InvalidOperationException("Queue identity cannot change during state transition.");

        return WithKeyAsync(previous.AccountId, async key =>
        {
            var id = StorageId(previous.AccountId, previous.NetworkId, previous.Id);
            byte[]? current = await js.InvokeAsync<byte[]?>("stellarVaultV2Db.readQueue", ct, id);
            if (current is null) return false;
            try
            {
                var actual = Decode(current, key, previous.AccountId, previous.NetworkId, previous.Id);
                if (!Equivalent(actual, previous)) return false;
                byte[] next = Encode(replacement, key);
                try
                {
                    return await js.InvokeAsync<bool>("stellarVaultV2Db.replaceQueue", ct, id, current, next);
                }
                finally { CryptographicOperations.ZeroMemory(next); }
            }
            finally { CryptographicOperations.ZeroMemory(current); }
        }, ct);
    }

    public Task<bool> TryDeleteAsync(VaultPendingMutation expected, CancellationToken ct = default) =>
        WithKeyAsync(expected.AccountId, async key =>
        {
            var id = StorageId(expected.AccountId, expected.NetworkId, expected.Id);
            byte[]? current = await js.InvokeAsync<byte[]?>("stellarVaultV2Db.readQueue", ct, id);
            if (current is null) return false;
            try
            {
                var actual = Decode(current, key, expected.AccountId, expected.NetworkId, expected.Id);
                if (!Equivalent(actual, expected)) return false;
                return await js.InvokeAsync<bool>("stellarVaultV2Db.deleteQueueIfMatch", ct, id, current);
            }
            finally { CryptographicOperations.ZeroMemory(current); }
        }, ct);

    private Task<T> WithKeyAsync<T>(string accountId, Func<ReadOnlyMemory<byte>, Task<T>> work,
        CancellationToken ct)
    {
        retention.RequireLocalPersistence();
        StellarAccountAddress.Validate(accountId);
        if (sessions.ActiveAccountId != accountId)
            throw new UnauthorizedAccessException("Queue account differs from the unlocked local vault.");
        return sessions.ExecuteAsync(async (_, key, token) =>
        {
            token.ThrowIfCancellationRequested();
            return await work(key);
        }, ct);
    }

    private static string ScopeId(string account, string network)
    {
        StellarAccountAddress.Validate(account);
        if (string.IsNullOrWhiteSpace(network) || network.Length > 512)
            throw new ArgumentException("Invalid queue network scope.", nameof(network));
        return account + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(network)));
    }

    private static string StorageId(string account, string network, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid mutation ID.", nameof(id));
        return ScopeId(account, network) + ":" + id;
    }

    private static byte[] AssociatedData(string account, string network, string id) =>
        Encoding.UTF8.GetBytes("StellarVault:queue:v1:" + StorageId(account, network, id));

    private static byte[] Encode(VaultPendingMutation entry, ReadOnlyMemory<byte> key)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(entry);
        try { return VaultCryptoPrimitives.Seal(key.Span, plain,
            AssociatedData(entry.AccountId, entry.NetworkId, entry.Id)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static VaultPendingMutation Decode(byte[] ciphertext, ReadOnlyMemory<byte> key,
        string account, string network, string id)
    {
        byte[] plain = VaultCryptoPrimitives.Open(key.Span, ciphertext,
            AssociatedData(account, network, id));
        try
        {
            var item = JsonSerializer.Deserialize<VaultPendingMutation>(plain)
                ?? throw new InvalidDataException("Invalid queue entry.");
            if (item.Id != id || item.AccountId != account || item.NetworkId != network)
                throw new CryptographicException("Queue entry identity mismatch.");
            return item;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static bool Equivalent(VaultPendingMutation a, VaultPendingMutation b) =>
        a.Id == b.Id && a.AccountId == b.AccountId && a.NetworkId == b.NetworkId &&
        a.Kind == b.Kind && a.EntityId == b.EntityId &&
        a.EncryptedPayload.AsSpan().SequenceEqual(b.EncryptedPayload) &&
        a.ExpectedRemoteVersion == b.ExpectedRemoteVersion &&
        a.DependsOn.SequenceEqual(b.DependsOn) && a.State == b.State &&
        a.TransactionHash == b.TransactionHash && a.CreatedUtc == b.CreatedUtc &&
        (a.SignedDeletionManifests ?? []).SequenceEqual(b.SignedDeletionManifests ?? []) &&
        (a.SignedFileEntries ?? []).SequenceEqual(b.SignedFileEntries ?? []);
}
