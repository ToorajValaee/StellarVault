using System.Security.Cryptography;
using System.Text;
using VaultEngine.Stellar.Services;

internal static class VaultRecordInterchangeSmoke
{
    public static async Task RunAsync()
    {
        var published = new[]
        {
            new LedgerRecordItem("aaaaaaaaaaaaaaaa", "Personal", "Folder notes",
                null, ["personal"], true),
            new LedgerRecordItem("bbbbbbbbbbbbbbbb", "Credential", "Secret password",
                "aaaaaaaaaaaaaaaa", ["login", "important"], false,
                "alice", "https://example.com", "2FA backup"),
            new LedgerRecordItem("cccccccccccccccc", "Notes", "line 1\nline 2",
                null, [], false),
            new LedgerRecordItem("dddddddddddddddd", "FILE TREE NODE",
                VaultLinkedTreeFilePolicy.Prefix + "{}")
        };
        var archive = VaultRecordInterchange.FromPublished(published);
        Check(archive.Records.Length == 3 &&
              archive.Records.All(x => x.Id != "dddddddddddddddd"),
            "File internals were incorrectly exported as records.");
        byte[] json = VaultRecordInterchange.ExportJson(archive);
        try
        {
            var restored = VaultRecordInterchange.ImportJson(json);
            Check(restored.Records.Length == 3 &&
                restored.Records.Single(x => x.Title == "Credential").FolderId ==
                    "aaaaaaaaaaaaaaaa" &&
                restored.Records.Single(x => x.Title == "Credential").UserName ==
                    "alice", "JSON roundtrip lost ordinary fields or folder identity.");

            var staged = new List<LedgerRecordDraft>();
            var ids = new[] { "eeeeeeeeeeeeeeee", "ffffffffffffffff", "1234567890abcdef" };
            var count = await VaultRecordInterchange.StageAsync(restored, (draft, _) =>
            {
                staged.Add(draft);
                return Task.FromResult(ids[staged.Count - 1]);
            });
            Check(count == 3 && staged.Count == 3 &&
                staged[0].IsFolder && staged[1].FolderId == ids[0] &&
                staged[1].Content == "Secret password" &&
                staged[1].UserName == "alice",
                "Import failed to stage folder first and remap record parent.");
            // Even an adapter that returns colliding 16-hex IDs must not
            // silently produce ambiguous imported parent/child references.
            try
            {
                await VaultRecordInterchange.StageAsync(restored,
                    (_, _) => Task.FromResult("abcdef0123456789"));
                throw new Exception("Duplicate newly assigned import IDs were accepted.");
            }
            catch (InvalidDataException) { }

            var missing = archive with
            {
                Records = archive.Records.Select(x => x.Title == "Credential"
                    ? x with { FolderId = "9999999999999999" } : x).ToArray()
            };
            MustReject(() => VaultRecordInterchange.Validate(missing),
                "Missing folder parent imported.");
            var cycle = archive with
            {
                Records = archive.Records.Select(x => x.IsFolder
                    ? x with { FolderId = x.Id } : x).ToArray()
            };
            MustReject(() => VaultRecordInterchange.Validate(cycle),
                "Cyclic folder parent imported.");
            MustReject(() => VaultRecordInterchange.Validate(archive with
            {
                Records = [archive.Records[0], archive.Records[0]]
            }), "Duplicate record ID imported.");
            MustReject(() => VaultRecordInterchange.ImportJson(
                Encoding.UTF8.GetBytes("{\"Format\":\"stellar-vault-records\",\"Version\":99,\"Records\":[]}")),
                "Unexpected schema version accepted.");
            MustReject(() => VaultRecordInterchange.ImportJson(
                new byte[VaultRecordInterchange.MaximumBytes + 1]),
                "Oversized imported JSON accepted.");

            // Password-encrypted portable backup uses a randomized salt and
            // authenticated ciphertext. It must fail closed on wrong password,
            // corrupted header, altered ciphertext and unsupported versions.
            const string backupPassword = "a deliberately long backup passphrase";
            var firstBackup = await VaultEncryptedRecordBackup.EncryptAsync(
                restored, backupPassword);
            var secondBackup = await VaultEncryptedRecordBackup.EncryptAsync(
                restored, backupPassword);
            try
            {
                Check(!firstBackup.AsSpan().SequenceEqual(secondBackup),
                    "Encrypted backup reused deterministic salt or nonce.");
                var decrypted = await VaultEncryptedRecordBackup.DecryptAsync(
                    firstBackup, backupPassword);
                Check(decrypted.Records.Length == restored.Records.Length &&
                    decrypted.Records.Single(x => x.Title == "Credential").Content ==
                        "Secret password", "Encrypted backup did not preserve records.");
                async Task RejectEncryptedAsync(byte[] data, string password)
                {
                    try
                    {
                        await VaultEncryptedRecordBackup.DecryptAsync(data, password);
                    }
                    catch (CryptographicException) { return; }
                    throw new Exception("Unsafe encrypted backup was accepted.");
                }
                await RejectEncryptedAsync(firstBackup, "the wrong backup password");
                var headerDamaged = firstBackup.ToArray();
                try
                {
                    headerDamaged[4] ^= 1;
                    await RejectEncryptedAsync(headerDamaged, backupPassword);
                }
                finally { CryptographicOperations.ZeroMemory(headerDamaged); }
                var payloadDamaged = firstBackup.ToArray();
                try
                {
                    payloadDamaged[^1] ^= 1;
                    await RejectEncryptedAsync(payloadDamaged, backupPassword);
                }
                finally { CryptographicOperations.ZeroMemory(payloadDamaged); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(firstBackup);
                CryptographicOperations.ZeroMemory(secondBackup);
            }

            var flat = new VaultRecordInterchange.Archive(
                VaultRecordInterchange.Format, VaultRecordInterchange.Version,
                [new("1", "CSV title", "A \"quoted\", multi\nline entry",
                    null, ["alpha", "beta"], false, "user", "https://site.test", "note")]);
            var csv = VaultRecordInterchange.ExportCsv(flat);
            try
            {
                var fromCsv = VaultRecordInterchange.ImportCsv(
                    new UTF8Encoding(false, true).GetString(csv));
                Check(fromCsv.Records.Length == 1 &&
                      fromCsv.Records[0].Content == flat.Records[0].Content &&
                      fromCsv.Records[0].Tags.SequenceEqual(["alpha", "beta"]),
                    "Quoted CSV multiline roundtrip failed.");
            }
            finally { CryptographicOperations.ZeroMemory(csv); }
            MustReject(() => VaultRecordInterchange.ExportCsv(archive),
                "CSV silently discarded folder hierarchy.");
            MustReject(() => VaultRecordInterchange.ExportCsv(flat with
            {
                Records = [flat.Records[0] with { Content = "=SUM(1,2)" }]
            }), "Spreadsheet formula injection was exported.");
            MustReject(() => VaultRecordInterchange.Validate(archive with
            {
                Records = [archive.Records[0] with
                    { Content = VaultLinkedTreeFilePolicy.Prefix + "{}" }]
            }), "Reserved file payload was imported as ordinary records.");
        }
        finally { CryptographicOperations.ZeroMemory(json); }
        Console.WriteLine("PASS: versioned record JSON/CSV and local-only folder-aware import");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    private static void MustReject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or
                                   ArgumentException or NotSupportedException) { return; }
        throw new Exception(message);
    }
}
