using Microsoft.JSInterop;
using StellarVaultPwa.Core;

namespace StellarVaultPwa.Services;

public sealed class VaultSession(IJSRuntime js) : IAsyncDisposable
{
    private StellarVaultStore? _store;

    public event Action? Changed;

    public bool IsUnlocked => _store is not null;
    public bool IsBusy { get; private set; }
    public string? StatusMessage { get; private set; }
    public VaultReadResult? Snapshot { get; private set; }
    public VaultNetworkSettings? Network => _store?.Settings;
    public string? RootAccountId => _store?.RootAccountId;

    public async Task UnlockAsync(VaultNetworkSettings settings, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Database secret is required.", nameof(secret));

        await LockAsync();
        SetBusy(true, "NEGOTIATING HORIZON LINK...");

        StellarVaultStore? candidate = null;
        try
        {
            candidate = new StellarVaultStore(settings, js);
            await candidate.OpenAsync(secret.Trim());
            var snapshot = await candidate.ReadAsync();

            _store = candidate;
            candidate = null;
            Snapshot = snapshot;
            StatusMessage = "DATABASE ONLINE";
        }
        catch
        {
            if (candidate is not null)
                await candidate.DisposeAsync();
            StatusMessage = "ACCESS DENIED";
            throw;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task RefreshAsync()
    {
        var store = RequireStore();
        SetBusy(true, "SCANNING ACCOUNT CHAIN...");
        try
        {
            Snapshot = await store.ReadAsync();
            StatusMessage = "DATABASE SYNCHRONIZED";
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    public Task<VaultCostEstimate> EstimateAddAsync(VaultRecord record) =>
        RequireStore().EstimateAddAsync(record);

    public Task<VaultCostEstimate> EstimateUpdateAsync(
        VaultRecordLocation existing,
        VaultRecord record) =>
        RequireStore().EstimateUpdateAsync(existing, record);

    public Task<VaultCostEstimate> EstimateDeleteAsync(VaultRecordLocation existing) =>
        RequireStore().EstimateDeleteAsync(existing);

    public async Task<string> AddAsync(VaultRecord record)
    {
        var store = RequireStore();
        SetBusy(true, "ENCRYPTING AND COMMITTING...");
        try
        {
            var transactionHash = await store.AddAsync(record);
            Snapshot = await store.ReadAsync();
            StatusMessage = "RECORD COMMITTED";
            return transactionHash;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }


    public async Task<VaultMutationResult> UpdateAsync(
        VaultRecordLocation existing,
        VaultRecord record)
    {
        var store = RequireStore();
        SetBusy(true, "ENCRYPTING AND UPDATING...");
        try
        {
            var result = await store.UpdateAsync(existing, record);
            Snapshot = await store.ReadAsync();
            StatusMessage = string.IsNullOrWhiteSpace(result.Warning)
                ? "RECORD UPDATED"
                : "RECORD UPDATED // CLEANUP WARNING";
            return result;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task<VaultMutationResult> DeleteAsync(VaultRecordLocation existing)
    {
        var store = RequireStore();
        SetBusy(true, "DELETING RECORD...");
        try
        {
            var result = await store.DeleteAsync(existing);
            Snapshot = await store.ReadAsync();
            StatusMessage = string.IsNullOrWhiteSpace(result.Warning)
                ? "RECORD DELETED"
                : "RECORD DELETED // CLEANUP WARNING";
            return result;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    public async Task LockAsync()
    {
        if (_store is not null)
            await _store.DisposeAsync();

        _store = null;
        Snapshot = null;
        IsBusy = false;
        StatusMessage = "DATABASE LOCKED";
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync() => new(LockAsync());

    private StellarVaultStore RequireStore() =>
        _store ?? throw new InvalidOperationException("The vault is locked.");

    private void SetBusy(bool busy, string message)
    {
        IsBusy = busy;
        StatusMessage = message;
        Changed?.Invoke();
    }
}
