using Microsoft.JSInterop;
using StellarVault.Core.Services;
using StellarVaultPwa.Core;
using StellarVaultPwa.Services;

namespace StellarVault.Pwa.Services;

/// <summary>HD-v2 ledger path using the original transaction writer and manifest
/// format. Never silently falls back to IndexedDB when submission fails.</summary>
public sealed class BrowserStellarLedgerRecords(IJSRuntime js) : IStellarLedgerRecords, IAsyncDisposable
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

    public Task<bool> HasRegistryAsync() => Task.FromResult(RequireStore().RegistryInitialized);
    public Task InitializeRegistryAsync() => RequireStore().InitializeHdRegistryAsync();

    public async Task<IReadOnlyList<LedgerRecordItem>> ReadAsync()
    {
        var read = await RequireStore().ReadAsync();
        if (read.Warnings.Count != 0)
            throw new InvalidDataException("Ledger record recovery reported errors: " +
                string.Join("; ", read.Warnings.Take(3)));
        return read.Records.Select(r =>
            new LedgerRecordItem(r.Record.Id, r.Record.Title, r.Record.Secret)).ToArray();
    }

    public async Task<string> PublishAsync(string title, string content)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrEmpty(content))
            throw new ArgumentException("Record title and contents are required.");
        var now = DateTimeOffset.UtcNow;
        var record = new VaultRecord
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            Title = title,
            Secret = content,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        string hash = await RequireStore().AddAsync(record);
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("Stellar did not return a confirmed transaction hash.");
        return hash;
    }

    public async Task DisconnectAsync()
    {
        var store = _store;
        _store = null;
        if (store is not null) await store.DisposeAsync();
    }

    public ValueTask DisposeAsync() => DisposeCoreAsync();
    private async ValueTask DisposeCoreAsync() => await DisconnectAsync();
}
