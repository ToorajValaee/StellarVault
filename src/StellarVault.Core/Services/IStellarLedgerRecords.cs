namespace StellarVault.Core.Services;

/// <summary>Separate HD-v2 on-chain record workflow. A successful Publish must
/// include a confirmed Stellar transaction; no IndexedDB fallback is allowed.</summary>
public interface IStellarLedgerRecords
{
    bool IsConnected { get; }
    Task ConnectAsync(string mnemonic, string horizonUrl, string networkPassphrase);
    Task<bool> HasRegistryAsync();
    Task InitializeRegistryAsync();
    Task<IReadOnlyList<LedgerRecordItem>> ReadAsync();
    Task<string> PublishAsync(string title, string content);
    Task DisconnectAsync();
}
public sealed record LedgerRecordItem(string Id, string Title, string Content);

/// <summary>Native hosts fail closed until their signing transport is implemented.</summary>
public sealed class UnavailableStellarLedgerRecords : IStellarLedgerRecords
{
    public bool IsConnected => false;
    private static Exception Unavailable() => new NotSupportedException("Stellar ledger transport is not configured on this platform.");
    public Task ConnectAsync(string mnemonic, string horizonUrl, string networkPassphrase) => Task.FromException(Unavailable());
    public Task<bool> HasRegistryAsync() => Task.FromException<bool>(Unavailable());
    public Task InitializeRegistryAsync() => Task.FromException(Unavailable());
    public Task<IReadOnlyList<LedgerRecordItem>> ReadAsync() => Task.FromException<IReadOnlyList<LedgerRecordItem>>(Unavailable());
    public Task<string> PublishAsync(string title, string content) => Task.FromException<string>(Unavailable());
    public Task DisconnectAsync() => Task.CompletedTask;
}
