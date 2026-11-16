using System.Reflection;
using StellarVault.Core.Services;
using VaultEngine.Core;

internal static class StellarObjectRecordFieldsSmoke
{
    public static Task RunAsync()
    {
        // The mapper is private to the Stellar provider: invoke just that
        // boundary to prove the public engine descriptor retains the complete
        // legacy ledger field set without requiring a live signing session.
        var provider = new StellarAccountObjectStoreProvider(
            null!, null!, null!, null!, null!, null!,
            null!, new VaultRecordTypeFactory());
        var method = typeof(StellarAccountObjectStoreProvider).GetMethod(
            "BuildDraft", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Stellar ordinary record mapping was removed.");
        var tags = new[] { "work", "login" };
        var descriptor = new ObjectDescriptor(
            "new", VaultRecordTypeFactory.Credential, "Account",
            Fields: new ObjectRecordFields("user", "https://example.invalid",
                "private notes", tags));
        var draft = (LedgerRecordDraft)(method.Invoke(provider,
            [descriptor, "secret data"]) ??
            throw new Exception("Stellar provider did not produce a draft."));
        Check(draft.Title == "Account" && draft.Content == "secret data" &&
            draft.UserName == "user" && draft.Url == "https://example.invalid" &&
            draft.Notes == "private notes" &&
            draft.Tags is { Length: 2 } &&
            draft.Tags.SequenceEqual(tags, StringComparer.Ordinal),
            "Engine descriptor lost structured credential fields.");
        tags[0] = "changed";
        Check(draft.Tags![0] == "work",
            "Stellar provider retained a mutable caller-owned tag array.");

        var clear = (LedgerRecordDraft)(method.Invoke(provider,
            [descriptor with { Fields = new ObjectRecordFields() }, "new content"]) ??
            throw new Exception("Explicitly clearing structured fields was rejected."));
        Check(clear.UserName is null && clear.Url is null &&
            clear.Notes is null && clear.Tags is null,
            "Explicitly empty structured fields were not preserved.");

        Console.WriteLine("PASS: chain-neutral structured record field mapping and clear semantics");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
