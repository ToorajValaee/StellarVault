using VaultEngine.Core;

namespace VaultEngine.Stellar.Horizon;

/// <summary>HTTP/Horizon adapter; the reusable engine owns provider selection.</summary>
public sealed class StellarTransactionObservationProvider(InspectTransactionReceipt inspect)
    : ITransactionObservationProvider
{
    public string ProviderId => "stellar";
    public async Task<TransactionObservation?> InspectAsync(
        string networkId, string transactionId, CancellationToken ct = default)
    {
        var finding = await inspect.ExecuteAsync(networkId, transactionId, ct);
        return finding is null ? null : new(finding.Receipt.Hash, finding.Receipt.SourceAccount,
            finding.Receipt.Ledger, finding.NetworkId, finding.ObservedAtUtc,
            "endpoint-attested-successful-receipt");
    }
}
