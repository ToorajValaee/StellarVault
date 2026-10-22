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
            new LedgerRecordItem(r.Record.Id, r.Record.Title, r.Record.Secret, r.Record.FolderId,
                r.Record.Tags, r.Record.IsFolder, r.Record.UserName, r.Record.Url, r.Record.Notes)).ToArray();
    }

    public Task<string> PublishAsync(string title, string content) =>
        PublishDetailedAsync(new LedgerRecordDraft(title, content));

    public async Task<string> PublishDetailedAsync(LedgerRecordDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrEmpty(draft.Content))
            throw new ArgumentException("Record title and contents are required.");
        if (draft.Title.Length > 256 || draft.Tags?.Length > 24 ||
            draft.Tags?.Any(t => t.Length > 64) == true)
            throw new ArgumentException("Record title or tags exceed the supported limits.");
        var now = DateTimeOffset.UtcNow;
        var record = new VaultRecord
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            Title = draft.Title.Trim(),
            Secret = draft.Content,
            FolderId = draft.FolderId,
            Tags = draft.Tags ?? [],
            Kind = draft.IsFolder ? "folder" : "record",
            UserName = draft.UserName ?? string.Empty,
            Url = draft.Url ?? string.Empty,
            Notes = draft.Notes ?? string.Empty,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        string hash = await RequireStore().AddAsync(record);
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("Stellar did not return a confirmed transaction hash.");
        return hash;
    }

    public async Task<LedgerMutationResult> UpdateAsync(string id, LedgerRecordDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var snapshot = await RequireStore().ReadAsync();
        var current = snapshot.Records.SingleOrDefault(x => x.Record.Id == id)
            ?? throw new InvalidOperationException("Record no longer exists; synchronize first.");
        if (snapshot.Warnings.Count > 0)
            throw new InvalidDataException("Ledger recovery contains warnings. Synchronize before editing.");
        if (current.Record.IsFolder != draft.IsFolder)
            throw new InvalidOperationException("Cannot change an existing record into a folder or vice versa.");
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrEmpty(draft.Content)
            || draft.Title.Length > 256 || draft.Tags?.Length > 24 ||
            draft.Tags?.Any(t => t.Length > 64) == true)
            throw new ArgumentException("Invalid title, content or tags.");
        if (draft.FolderId == id) throw new ArgumentException("A folder cannot contain itself.");
        if (!string.IsNullOrWhiteSpace(draft.FolderId))
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal) { id };
            var ancestor = draft.FolderId;
            while (ancestor is not null)
            {
                if (!ancestors.Add(ancestor))
                    throw new InvalidOperationException("Folder move would create a cycle.");
                ancestor = snapshot.Records.SingleOrDefault(x => x.Record.Id == ancestor && x.Record.IsFolder)?.Record.FolderId
                    ?? (snapshot.Records.Any(x => x.Record.Id == ancestor && x.Record.IsFolder)
                        ? null : throw new InvalidOperationException("Target folder does not exist."));
            }
        }
        var updated = new VaultRecord {
            Id = id, Title = draft.Title.Trim(), Secret = draft.Content,
            UserName = draft.UserName ?? string.Empty, Url = draft.Url ?? string.Empty,
            Notes = draft.Notes ?? string.Empty, Tags = draft.Tags ?? [],
            FolderId = string.IsNullOrWhiteSpace(draft.FolderId) ? null : draft.FolderId,
            Kind = current.Record.Kind, CreatedUtc = current.Record.CreatedUtc,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        var result = await RequireStore().UpdateAsync(current, updated);
        return new LedgerMutationResult(result.TransactionHash, result.Warning);
    }

    public async Task<LedgerMutationResult> DeleteAsync(string id)
    {
        var snapshot = await RequireStore().ReadAsync();
        if (snapshot.Warnings.Count > 0)
            throw new InvalidDataException("Ledger recovery contains warnings. Synchronize before deleting.");
        var current = snapshot.Records.SingleOrDefault(x => x.Record.Id == id)
            ?? throw new InvalidOperationException("Record no longer exists; synchronize first.");
        if (current.Record.IsFolder && snapshot.Records.Any(x => x.Record.FolderId == id))
            throw new InvalidOperationException("Move or delete the folder's contents before deleting the folder.");
        var result = await RequireStore().DeleteAsync(current);
        return new LedgerMutationResult(result.TransactionHash, result.Warning);
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
