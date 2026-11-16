namespace VaultEngine.Stellar.Services;

/// <summary>
/// Public-only lookup of the exact hash calculated from a signed linked
/// transaction before Horizon broadcast. This is a search identifier, never
/// proof of inclusion, success, signer policy, or permission to retry.
/// </summary>
public static class VaultLinkedSignedSubmissionIdentity
{
    public static async Task<string?> TryReadAsync(
        IStellarSecretKeyLedger ledger, string root, string kind,
        string targetAccountId, string? recordId,
        string networkPassphrase, long signerEpoch)
    {
        try
        {
            if (!ledger.IsConnected || ledger.RootAccountId != root ||
                ledger.ConnectionEpoch != signerEpoch ||
                ledger.ConnectedNetworkPassphrase != networkPassphrase)
                return null;
            var diagnostic = await ledger.InspectUncertainSubmissionAsync(
                CancellationToken.None);
            if (!ledger.IsConnected || ledger.RootAccountId != root ||
                ledger.ConnectionEpoch != signerEpoch ||
                ledger.ConnectedNetworkPassphrase != networkPassphrase ||
                diagnostic is null ||
                diagnostic.Kind != kind ||
                diagnostic.RootAccountId != root ||
                diagnostic.TargetAccountId != targetAccountId ||
                diagnostic.RecordId != recordId ||
                diagnostic.NetworkPassphrase != networkPassphrase ||
                diagnostic.TransactionHash is not { Length: 64 } hash ||
                !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'
                                 or >= 'A' and <= 'F'))
                return null;
            return hash.ToLowerInvariant();
        }
        catch
        {
            // The original durable Submitting reservation still protects
            // against retry if the runtime has disappeared or is unavailable.
            return null;
        }
    }
}
