using VaultEngine.Core;
using System.Net;
using System.Text;
using VaultEngine.Stellar.Horizon;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("Invalid network configuration was accepted.");
}
Check(typeof(HorizonReceiptGateway).Assembly.GetName().Name == "VaultEngine.Stellar.Horizon",
    "Horizon integration is not independently packaged");
Check(!typeof(HorizonReceiptGateway).Assembly.GetReferencedAssemblies().Any(a =>
    a.Name!.StartsWith("StellarVault.") || a.Name.StartsWith("Microsoft.AspNetCore") ||
    a.Name == "VaultEngine.Stellar"), "Horizon integration depends on a consumer, host framework or wallet provider");
TenantAuthSmoke.Run();
await ApiHostPolicySmoke.RunAsync();
var profile = new NetworkProfile("customer-one", "https://ledger.example", "Customer private network");
var catalog = new ConfiguredNetworkCatalog(new[] { profile });
Check(catalog.Get("customer-one").HorizonUrl == "https://ledger.example", "Custom network lookup failed");
Check(catalog.List().Single().Id == profile.Id, "Discovery does not include customer network");
foreach (var url in new[] { "http://ledger.example", "https://user:pass@ledger.example", "https://ledger.example/path", "https://ledger.example/?secret=x", "https://ledger.example/#fragment" })
    Reject(() => new ConfiguredNetworkCatalog(new[] { profile with { HorizonUrl = url } }));
Reject(() => new ConfiguredNetworkCatalog(new[] { profile, profile }));
Reject(() => new ConfiguredNetworkCatalog(new[] { profile with { ExpectedPassphrase = " " } }));
Reject(() => new ConfiguredNetworkCatalog(new[] { profile with { Id = "../bad" } }));
var hash = new string('a', 64);
var handler = new FakeHorizon(profile.ExpectedPassphrase, hash);
using var http = new HttpClient(handler);
var useCase = new InspectTransactionReceipt(catalog, new HorizonReceiptGateway(http), TimeProvider.System);
var result = await useCase.ExecuteAsync(profile.Id, hash);
Check(result?.Network == profile.Id && result.Receipt.Ledger == 123 && result.NetworkId.Length == 64, "Customer receipt was not network-bound");
Check(handler.ReceiptRequests == 1, "Unexpected receipt request count");
handler.Passphrase = "Wrong network";
try { await useCase.ExecuteAsync(profile.Id, hash); throw new Exception("Mismatched network accepted"); }
catch (NetworkIdentityException) { }
Check(handler.ReceiptRequests == 1, "Receipt fetched despite wrong network");
try { await useCase.ExecuteAsync("unknown", hash); throw new Exception("Unknown network accepted"); }
catch (KeyNotFoundException) { }
try { await useCase.ExecuteAsync(profile.Id, "invalid"); throw new Exception("Malformed hash accepted"); }
catch (ArgumentException) { }
handler.Passphrase = profile.ExpectedPassphrase;
handler.Oversized = true;
Check(await useCase.ExecuteAsync(profile.Id, hash) is null, "Oversized chunked receipt accepted");
handler.Oversized = false;
handler.Redirect = true;
try { await useCase.ExecuteAsync(profile.Id, hash); throw new Exception("Redirected root accepted"); }
catch (NetworkIdentityException) { }
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await useCase.ExecuteAsync(profile.Id, hash, cancelled.Token); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { }
handler.Redirect = false;
var engine = new TransactionObservationRouter([new StellarTransactionObservationProvider(useCase)]);
var observed = await engine.InspectAsync(new("stellar", profile.Id), hash);
Check(observed?.TransactionId == hash && observed.BlockNumber == 123 &&
    observed.NetworkIdentity.Length == 64, "API infrastructure did not execute through the shared engine");
Console.WriteLine("PASS: custom network discovery, configuration guards, network identity, bounded receipts, redirects and cancellation.");

sealed class FakeHorizon(string passphrase, string hash) : HttpMessageHandler
{
    public string Passphrase { get; set; } = passphrase;
    public int ReceiptRequests { get; private set; }
    public bool Oversized { get; set; }
    public bool Redirect { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isRoot = request.RequestUri!.AbsolutePath == "/";
        if (!isRoot) ReceiptRequests++;
        var json = isRoot
            ? System.Text.Json.JsonSerializer.Serialize(new { network_passphrase = Passphrase })
            : Oversized ? new string(' ', 262145) : $$"""{"hash":"{{hash}}","successful":true,"source_account":"GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF","ledger":123}""";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = Redirect ? new HttpRequestMessage(HttpMethod.Get, "https://different.example/") : request,
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)))
        };
        response.Content.Headers.ContentType = new("application/json");
        return Task.FromResult(response);
    }
}
