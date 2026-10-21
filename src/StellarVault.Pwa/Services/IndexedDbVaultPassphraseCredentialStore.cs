using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVaultPwa.Services;

/// <summary>Browser-backed v2 wrapped credential storage. No plaintext keys are persisted.
/// IndexedDB transactions provide per-account create-if-absent and compare-and-swap.</summary>
public sealed class IndexedDbVaultPassphraseCredentialStore(IJSRuntime js) : IVaultPassphraseCredentialStore
{
    public Task<byte[]?> ReadAsync(string accountId, CancellationToken ct = default)
    {
        StellarAccountAddress.Validate(accountId);
        return js.InvokeAsync<byte[]?>("stellarVaultV2Db.readCredential", ct, accountId).AsTask();
    }

    public Task<bool> CreateAsync(string accountId, ReadOnlyMemory<byte> record, CancellationToken ct = default)
    {
        StellarAccountAddress.Validate(accountId);
        return js.InvokeAsync<bool>("stellarVaultV2Db.createCredential", ct, accountId, record.ToArray()).AsTask();
    }

    public Task<bool> ReplaceAsync(string accountId, ReadOnlyMemory<byte> expected,
        ReadOnlyMemory<byte> replacement, CancellationToken ct = default)
    {
        StellarAccountAddress.Validate(accountId);
        return js.InvokeAsync<bool>("stellarVaultV2Db.replaceCredential", ct,
            accountId, expected.ToArray(), replacement.ToArray()).AsTask();
    }
}
