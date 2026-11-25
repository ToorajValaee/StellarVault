using System.Net;
using System.Text;
using System.Text.Json;
using StellarVault.Pwa.Services;

internal static class LinkedExpansionHistorySmoke
{
    private const string Root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
    private const string Child = "GAAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQDZ7H";
    private const string Tail = "GCEZWKCA5VLDNRLN3RPRJMRZOX3Z6G5CHCGSNFHEYVXM3XOJMDS674JZ";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static async Task RunAsync()
    {
        static object Create(string destination = Child, string source = Root,
            string funder = Root, string starting = "1.5000000") =>
            new { type = "create_account", source_account = source, account = destination,
                funder, starting_balance = starting,
                transaction_hash = Hash, transaction_successful = true };
        static object Options(string signer = Root, int master = 0, int high = 1) =>
            new { type = "set_options", source_account = Child,
                signer_key = signer, signer_weight = 1, master_key_weight = master,
                low_threshold = 1, med_threshold = 1, high_threshold = high,
                transaction_hash = Hash, transaction_successful = true };
        static object Pointer(string account = Root, string target = Child) =>
            new { type = "manage_data", source_account = account, name = "sv:next:v1",
                value = Convert.ToBase64String(Encoding.ASCII.GetBytes(target)),
                transaction_hash = Hash, transaction_successful = true };
        static object Payment(string receiver = Root, string amount = "0.5000000") =>
            new { type = "payment", source_account = Root, to = receiver,
                asset_type = "native", amount, transaction_hash = Hash, transaction_successful = true };

        async Task<bool> MatchesAsync(object[] operations)
        {
            var json = JsonSerializer.Serialize(new { _embedded = new { records = operations } });
            using var client = new HttpClient(new StubHandler(json));
            var inspector = new HorizonVaultLinkedExpansionOperationInspector(client);
            return (await inspector.InspectAsync(
                "https://horizon-testnet.stellar.org", Hash, Root, Child)).OperationsMatch;
        }

        Check(await MatchesAsync([Create(), Options(), Pointer()]),
            "Expected initial root-to-child expansion was rejected.");
        Check(await MatchesAsync([Create(), Options(), Payment(Tail), Pointer(Tail)]),
            "Expected existing-tail expansion with reserve payment was rejected.");
        Check(!await MatchesAsync([Create(), Options(), Payment(Tail), Pointer()]),
            "A root pointer was accepted with a payment funding a different tail.");
        Check(!await MatchesAsync([Create(), Options(), Payment(Tail, "0"), Pointer(Tail)]),
            "Zero reserve payment was accepted.");
        Check(!await MatchesAsync([Create(), Pointer(), Options()]),
            "Out-of-order linked signer installation was accepted.");
        Check(!await MatchesAsync([Create(), Options(master: 1), Pointer()]),
            "Child master signer was not disabled.");
        Check(!await MatchesAsync([Create(), Options(signer: Child), Pointer()]),
            "Incorrect root signer was accepted.");
        Check(!await MatchesAsync([Create(), Options(high: 2), Pointer()]),
            "Incorrect child threshold was accepted.");
        Check(!await MatchesAsync([Create(), Options(), Pointer(target: Root)]),
            "Parent pointer to wrong child was accepted.");
        Check(!await MatchesAsync([Create(destination: Root), Options(), Pointer()]),
            "CreateAccount destination mismatch was accepted.");
        Check(!await MatchesAsync([Create(funder: Child), Options(), Pointer()]),
            "Incorrect CreateAccount funder was accepted.");
        Check(!await MatchesAsync([Create(starting: "0.0000000"), Options(), Pointer()]),
            "Zero funding for newly created child was accepted.");
        Check(!await MatchesAsync([Create(), Options(), Pointer(), Pointer()]),
            "Duplicate parent-pointer operation was accepted.");
        Check(!await MatchesAsync([Create(), Options(), Payment(), Pointer()]),
            "Unnecessary root-tail payment was accepted.");

        Console.WriteLine("PASS: historical linked account expansion operations reject invalid signer, threshold, order and pointer history");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class StubHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}
