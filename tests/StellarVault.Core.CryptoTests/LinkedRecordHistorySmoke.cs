using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StellarVault.Pwa.Services;

internal static class LinkedRecordHistorySmoke
{
    private const string Root = "GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAWHF";
    private const string Other = "GAAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQCAIBAEAQDZ7H";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Record = "0123456789abcdef";
    private const string Generation = "a1b2c3d4";

    public static async Task RunAsync()
    {
        var ciphertext = Enumerable.Range(0, 100).Select(x => (byte)x).ToArray();
        var manifest = new byte[47];
        manifest[0] = 1;
        Encoding.ASCII.GetBytes(Generation).CopyTo(manifest, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(manifest.AsSpan(9, 2), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(manifest.AsSpan(11, 4), ciphertext.Length);
        SHA256.HashData(ciphertext).CopyTo(manifest, 15);

        static object Operation(string type, string? name = null, byte[]? value = null,
            string source = Root, string transaction = Hash) =>
            new { type, name, source_account = source, transaction_hash = transaction,
                transaction_successful = true,
                value = value is null ? null : Convert.ToBase64String(value) };

        var chunk0 = Operation("manage_data", $"skc:{Record}:{Generation}:0",
            ciphertext[..64]);
        var chunk1 = Operation("manage_data", $"skc:{Record}:{Generation}:1",
            ciphertext[64..]);
        var header = Operation("manage_data", "skh:" + Record, manifest);

        async Task<bool> VerifyAsync(object[] operations)
        {
            var document = JsonSerializer.Serialize(new
            {
                _embedded = new { records = operations }
            });
            using var client = new HttpClient(new StubHandler(document));
            var reader = new HorizonVaultLinkedRecordOperationInspector(client);
            var result = await reader.InspectCreateAsync(
                "https://horizon-testnet.stellar.org", Hash, Record);
            return result.ManifestAndChunksMatch;
        }

        Check(await VerifyAsync([chunk0, chunk1, header]),
            "Correct S-key historical manifest/chunk commitment rejected.");
        var tampered = (byte[])ciphertext.Clone();
        tampered[0] ^= 0x40;
        Check(!await VerifyAsync([
            Operation("manage_data", $"skc:{Record}:{Generation}:0", tampered[..64]),
            chunk1, header
        ]), "Tampered ciphertext passed historical SHA-256 validation.");
        Check(!await VerifyAsync([chunk0, header, chunk1]),
            "A chunk after the manifest was accepted.");
        Check(!await VerifyAsync([chunk0, chunk0, header]),
            "Duplicate historical chunk was accepted.");
        Check(!await VerifyAsync([
            chunk0,
            Operation("manage_data", $"skc:{Record}:{Generation}:1",
                ciphertext[64..], Other),
            header
        ]), "Different historical source account was accepted.");
        Check(!await VerifyAsync([
            Operation("manage_data", $"skc:{Record}:eeeeeeee:0", ciphertext[..64]),
            chunk1, header
        ]), "Mixed record generations were accepted.");
        Check(!await VerifyAsync([
            chunk0,
            Operation("manage_data", $"skc:{Record}:{Generation}:01",
                ciphertext[64..]),
            header
        ]), "Noncanonical base36 chunk index was accepted.");
        Check(!await VerifyAsync([
            chunk0,
            Operation("manage_data", $"skc:{Record}:{Generation}:1",
                ciphertext[64..], Root, "f".PadLeft(64, 'f')),
            header
        ]), "A chunk from another transaction was accepted.");

        Console.WriteLine("PASS: bounded historical S-key skh/skc reconstruction rejects tampering, duplicates, cross-account operations and wrong generations");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class StubHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var result = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(result);
        }
    }
}
