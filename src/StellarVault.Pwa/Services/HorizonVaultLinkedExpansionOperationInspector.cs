using System.Net;
using System.Text;
using System.Text.Json;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Read-only bounded check of the exact atomic operation shape used by the
/// independent S-key v1 linked-account expansion signer. A matching Horizon
/// operation trace does NOT prove signatures, prior tail lineage, pre-existing
/// signer state or ledger consensus. It must NEVER release a queued mutation.
/// </summary>
public sealed class HorizonVaultLinkedExpansionOperationInspector(HttpClient client)
{
    private const int MaxResponseBytes = 256 * 1024;

    public sealed record Finding(bool OperationsMatch, string? ParentAccount,
        string Explanation);

    public async Task<Finding> InspectAsync(
        string horizonUrl, string transactionHash, string rootAccount,
        string childAccount, CancellationToken ct = default)
    {
        static Finding Reject(string reason) => new(false, null, reason);
        static bool Address(string? value) => value is { Length: 56 } &&
            value[0] == 'G' &&
            value.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7');
        if (!Address(rootAccount) || !Address(childAccount) || childAccount == rootAccount ||
            transactionHash is not { Length: 64 } || !transactionHash.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(horizonUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            origin.AbsolutePath != "/")
            return Reject("Invalid linked expansion historical inspection scope.");

        var expected = new Uri(origin, "transactions/" +
            transactionHash.ToLowerInvariant() + "/operations?limit=200");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, expected);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Reject("Expansion transaction operations are not available.");
            response.EnsureSuccessStatusCode();
            var effective = response.RequestMessage?.RequestUri;
            if (effective is null || effective.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(effective.Authority, expected.Authority,
                    StringComparison.OrdinalIgnoreCase) ||
                effective.AbsolutePath != expected.AbsolutePath ||
                effective.Query != expected.Query || effective.Fragment.Length != 0 ||
                response.Content.Headers.ContentLength is > MaxResponseBytes)
                return Reject("Expansion operation endpoint changed or exceeded limits.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (count == 0) break;
                if (bounded.Length + count > MaxResponseBytes)
                    return Reject("Expansion operation response exceeded the size limit.");
                bounded.Write(buffer, 0, count);
            }
            bounded.Position = 0;
            using var json = await JsonDocument.ParseAsync(bounded,
                cancellationToken: ct);
            var document = json.RootElement;
            if (document.ValueKind != JsonValueKind.Object ||
                !document.TryGetProperty("_embedded", out var embedded) ||
                embedded.ValueKind != JsonValueKind.Object ||
                !embedded.TryGetProperty("records", out var records) ||
                records.ValueKind != JsonValueKind.Array ||
                records.GetArrayLength() is not (3 or 4))
                return Reject("Expected exactly three or four expansion operations.");

            var ops = records.EnumerateArray().ToArray();
            for (var i = 0; i < ops.Length; i++)
            {
                var op = ops[i];
                if (op.ValueKind != JsonValueKind.Object ||
                    !op.TryGetProperty("transaction_successful", out var success) ||
                    success.ValueKind != JsonValueKind.True ||
                    !GetString(op, "transaction_hash").Equals(
                        transactionHash, StringComparison.OrdinalIgnoreCase))
                    return Reject("Expansion trace includes unrelated or unsuccessful operations.");
            }

            var create = ops[0];
            if (GetString(create, "type") != "create_account" ||
                GetString(create, "source_account") != rootAccount ||
                GetString(create, "account") != childAccount)
                return Reject("CreateAccount does not fund the expected new linked child.");
            var options = ops[1];
            if (GetString(options, "type") != "set_options" ||
                GetString(options, "source_account") != childAccount ||
                GetString(options, "signer_key") != rootAccount ||
                !IntValue(options, "signer_weight", 1) ||
                !IntValue(options, "master_key_weight", 0) ||
                !IntValue(options, "low_threshold", 1) ||
                !IntValue(options, "med_threshold", 1) ||
                !IntValue(options, "high_threshold", 1))
                return Reject("Child set_options does not establish the expected root signer and thresholds.");

            if (ops.Length == 4)
            {
                var funding = ops[2];
                if (GetString(funding, "type") != "payment" ||
                    GetString(funding, "source_account") != rootAccount ||
                    !Address(GetString(funding, "to")) ||
                    GetString(funding, "asset_type") != "native")
                    return Reject("The optional tail-reserve payment is inconsistent.");
            }

            var pointer = ops[^1];
            var parent = GetString(pointer, "source_account");
            if (GetString(pointer, "type") != "manage_data" ||
                !Address(parent) ||
                GetString(pointer, "name") != "sv:next:v1" ||
                (ops.Length == 3 && parent != rootAccount) ||
                (ops.Length == 4 && (parent == rootAccount ||
                    GetString(ops[2], "to") != parent)))
                return Reject("Linked parent pointer is not in the expected account.");

            var encoded = GetString(pointer, "value");
            byte[] value;
            try { value = Convert.FromBase64String(encoded); }
            catch (FormatException) { return Reject("Parent pointer is not valid base64."); }
            try
            {
                if (value.Length != 56 ||
                    Encoding.ASCII.GetString(value) != childAccount)
                    return Reject("The parent pointer does not target the created child.");
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(value); }

            return new(true, parent,
                "Historical expansion operations match child creation, root signer and parent pointer. Previous parent-chain lineage, signatures, signer history and ledger consensus remain unverified.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Reject("Historical expansion operation inspection was unavailable or invalid.");
        }
    }

    private static string GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static bool IntValue(JsonElement obj, string name, int expected) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var actual) && actual == expected;
}
