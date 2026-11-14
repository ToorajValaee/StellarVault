using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Reuses the existing IndexedDB credential CAS operations but keeps imported
/// S-key queue credentials in a separately named and network-scoped namespace.
/// Only wrapped random data keys and public Argon2id parameters are retained.
/// HD credential IDs can never alias an S-key queue credential ID.
/// </summary>
public sealed class IndexedDbVaultLinkedPassphraseCredentialStore(
    IJSRuntime js,
    VaultNetworkSelection network,
    VaultDeviceRetentionGate retention) : IVaultLinkedPassphraseCredentialStore
{
    private string Scope(string accountId)
    {
        retention.RequireLocalPersistence();
        StellarAccountAddress.Validate(accountId);
        return "secret-chain:queue-key:v1:" + network.CredentialKey(accountId);
    }

    public Task<byte[]?> ReadAsync(string accountId, CancellationToken ct = default) =>
        js.InvokeAsync<byte[]?>("stellarVaultV2Db.readCredential", ct, Scope(accountId)).AsTask();

    public Task<bool> CreateAsync(
        string accountId, ReadOnlyMemory<byte> record, CancellationToken ct = default) =>
        js.InvokeAsync<bool>("stellarVaultV2Db.createCredential",
            ct, Scope(accountId), record.ToArray()).AsTask();

    public Task<bool> ReplaceAsync(
        string accountId, ReadOnlyMemory<byte> expected,
        ReadOnlyMemory<byte> replacement, CancellationToken ct = default) =>
        js.InvokeAsync<bool>("stellarVaultV2Db.replaceCredential",
            ct, Scope(accountId), expected.ToArray(), replacement.ToArray()).AsTask();
}
