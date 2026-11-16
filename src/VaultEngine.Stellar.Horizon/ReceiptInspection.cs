namespace VaultEngine.Stellar.Horizon;

public sealed record NetworkProfile(string Id, string HorizonUrl, string ExpectedPassphrase);
public sealed record NetworkDescriptor(string Id);
public sealed record TransactionReceipt(string Hash, string SourceAccount, long Ledger);
public sealed record NetworkAttestation(bool Matches, string? NetworkId);
public sealed record ReceiptInspection(string Network, string NetworkId, TransactionReceipt Receipt, DateTimeOffset ObservedAtUtc);

public interface INetworkCatalog
{
    IReadOnlyList<NetworkDescriptor> List();
    NetworkProfile Get(string id);
}

public interface IReceiptGateway
{
    Task<NetworkAttestation> InspectNetworkAsync(NetworkProfile network, CancellationToken ct);
    Task<TransactionReceipt?> InspectReceiptAsync(NetworkProfile network, string hash, CancellationToken ct);
}

public sealed class NetworkIdentityException : Exception
{
    public NetworkIdentityException() : base("Configured endpoint did not attest the expected network.") { }
}

/// <summary>Transport-independent read-only use case. Endpoint attestation is not consensus proof.</summary>
public sealed class InspectTransactionReceipt(INetworkCatalog networks, IReceiptGateway gateway, TimeProvider clock)
{
    public async Task<ReceiptInspection?> ExecuteAsync(string networkId, string hash, CancellationToken ct = default)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("Provide exactly 64 hexadecimal characters.", nameof(hash));
        var network = networks.Get(networkId);
        var attestation = await gateway.InspectNetworkAsync(network, ct);
        if (!attestation.Matches || string.IsNullOrEmpty(attestation.NetworkId))
            throw new NetworkIdentityException();
        var receipt = await gateway.InspectReceiptAsync(network, hash.ToLowerInvariant(), ct);
        return receipt is null ? null : new(network.Id, attestation.NetworkId, receipt, clock.GetUtcNow());
    }
}
