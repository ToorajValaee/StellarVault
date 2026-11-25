using System.Text.Json;
using Microsoft.JSInterop;
using StellarVault.Pwa.Services;
using VaultEngine.Stellar.Services;

var js = new WireRuntime();
await using var ledger = new BrowserSecretKeyLedger(js);
await ledger.ConnectAsync("SAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABSU2",
    "https://synthetic.invalid", "Synthetic test network");
var draft = new LedgerRecordDraft("Synthetic title", "Synthetic content", null,
    ["synthetic-tag"], false, "synthetic-user", "https://example.invalid", "Synthetic notes");
await ledger.QuoteCreateAsync("0123456789abcdef", draft);
await ledger.VerifyAsync(new string('a', 64), VaultMutationKind.CreateRecord,
    "0123456789abcdef", draft, null);
if (js.Checked != 2) throw new Exception("Both quote and historical comparison must use the wire draft contract");
Console.WriteLine("PASS: actual S-key host adapter preserves every draft field under JS interop camel-case serialization.");
await RenderedUiSmoke.RunAsync();

sealed class WireRuntime : IJSRuntime
{
    const string Root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
    public int Checked { get; private set; }
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, default, args);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
    {
        ct.ThrowIfCancellationRequested();
        if (identifier.EndsWith(".plan") || identifier.EndsWith(".verifyHistoricalRecord"))
        {
            var draft = args![identifier.EndsWith(".plan") ? 3 : 4]!;
            // Same naming policy as JSRuntime. A raw CLR LedgerRecordDraft fails
            // these checks even though a mock inspecting CLR properties passes.
            var wire = JsonSerializer.SerializeToElement(draft, draft.GetType(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var expected = new Dictionary<string, string> { ["Title"]="Synthetic title",
                ["Content"]="Synthetic content", ["UserName"]="synthetic-user",
                ["Url"]="https://example.invalid", ["Notes"]="Synthetic notes" };
            foreach (var (key, value) in expected)
                if (wire.GetProperty(key).GetString() != value) throw new Exception("Draft wire field lost: " + key);
            if (wire.GetProperty("IsFolder").GetBoolean() ||
                wire.GetProperty("FolderId").ValueKind != JsonValueKind.Null ||
                wire.GetProperty("Tags")[0].GetString() != "synthetic-tag" ||
                wire.EnumerateObject().Count() != 8)
                throw new Exception("Draft wire metadata changed");
            Checked++;
        }
        object? result = identifier switch
        {
            "stellarVaultSecretLedger.open" => new { RootAccountId = Root },
            "stellarVaultSecretLedger.plan" => new { QuoteId = new string('b',32), Kind="create",
                TargetAccountId=Root, Operations=1, Transactions=1, NetworkFeeXlm="0.00001",
                PeakReserveXlm="0", NetReserveChangeXlm="0", FundingTopUpXlm="0", CreatesChildNode=false },
            "stellarVaultSecretLedger.verifyHistoricalRecord" => true,
            _ => null
        };
        return ValueTask.FromResult(result is null ? default! : JsonSerializer.Deserialize<TValue>(JsonSerializer.Serialize(result))!);
    }
}
