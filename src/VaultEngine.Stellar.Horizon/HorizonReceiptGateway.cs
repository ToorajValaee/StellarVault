using System.Collections.ObjectModel;

namespace VaultEngine.Stellar.Horizon;

/// <summary>Immutable operator-configured allowlist; request data cannot select a URL or passphrase.</summary>
public sealed class ConfiguredNetworkCatalog : INetworkCatalog
{
    private readonly IReadOnlyDictionary<string, NetworkProfile> profiles;
    private readonly IReadOnlyList<NetworkDescriptor> descriptors;

    public ConfiguredNetworkCatalog(IEnumerable<NetworkProfile> configured)
    {
        var entries = new Dictionary<string, NetworkProfile>(StringComparer.Ordinal);
        foreach (var entry in configured)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Id) || entry.Id.Length > 64 ||
                !entry.Id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ||
                string.IsNullOrWhiteSpace(entry.ExpectedPassphrase) || entry.ExpectedPassphrase.Length > 512 ||
                !Uri.TryCreate(entry.HorizonUrl, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
                endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Network profiles require a lowercase ID, an HTTPS root and an expected passphrase.");
            if (!entries.TryAdd(entry.Id, entry with { HorizonUrl = endpoint.AbsoluteUri.TrimEnd('/') }))
                throw new ArgumentException("Duplicate configured network ID.");
        }
        if (entries.Count is < 1 or > 128)
            throw new ArgumentException("Configure between 1 and 128 network profiles.");
        profiles = new ReadOnlyDictionary<string, NetworkProfile>(entries);
        descriptors = Array.AsReadOnly(entries.Keys.Order(StringComparer.Ordinal).Select(id => new NetworkDescriptor(id)).ToArray());
    }

    public IReadOnlyList<NetworkDescriptor> List() => descriptors;
    public NetworkProfile Get(string id) => profiles.TryGetValue(id, out var profile)
        ? profile : throw new KeyNotFoundException("Network is not configured.");
}

public sealed class HorizonReceiptGateway(HttpClient client) : IReceiptGateway
{
    public async Task<NetworkAttestation> InspectNetworkAsync(NetworkProfile network, CancellationToken ct)
    {
        var finding = await new HorizonVaultNetworkInspector(client).InspectAsync(network.HorizonUrl, network.ExpectedPassphrase, ct);
        return new(finding.Matches, finding.NetworkIdHex);
    }

    public async Task<TransactionReceipt?> InspectReceiptAsync(NetworkProfile network, string hash, CancellationToken ct)
    {
        var receipt = await new HorizonVaultTransactionInspector(client).InspectAsync(network.HorizonUrl, hash, ct);
        return receipt is null ? null : new(receipt.Hash!, receipt.SourceAccount!, receipt.Ledger);
    }
}
