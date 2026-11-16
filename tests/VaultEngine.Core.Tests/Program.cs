using VaultEngine.Core;

await AccountObjectStoreContractSmoke.RunAsync();
await AtomicFileObjectDestinationSmoke.RunAsync();
if (typeof(AccountObjectStoreRouter).Assembly.GetReferencedAssemblies()
    .Any(a => a.Name!.StartsWith("StellarVault") || a.Name.StartsWith("VaultEngine.Stellar")))
    throw new Exception("Engine assembly depends on a consumer/provider.");
var stellar = new ObservationProvider("stellar");
var evm = new ObservationProvider("evm-test-double");
var engine = new TransactionObservationRouter([stellar, evm]);
await engine.InspectAsync(new("stellar", "customer-one"), "id-a");
await engine.InspectAsync(new("evm-test-double", "network-b"), "id-b");
if (stellar.Calls != 1 || evm.Calls != 1) throw new Exception("Provider dispatch crossed scopes.");
try { await engine.InspectAsync(new("unregistered", "network"), "id"); throw new Exception("Unknown provider accepted."); }
catch (NotSupportedException) { }
Console.WriteLine("PASS: standalone engine has no StellarVault/provider dependencies and dispatches multiple providers.");

sealed class ObservationProvider(string id) : ITransactionObservationProvider
{
    public string ProviderId => id;
    public int Calls { get; private set; }
    public Task<TransactionObservation?> InspectAsync(string network, string id, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<TransactionObservation?>(new(id, null, null, network, DateTimeOffset.UtcNow, "test-double"));
    }
}
