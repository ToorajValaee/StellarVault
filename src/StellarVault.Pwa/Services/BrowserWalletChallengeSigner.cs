using Microsoft.JSInterop;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>Bridge for a future user-approved wallet that supports signing arbitrary
/// domain-separated bytes. This bridge NEVER imports secret keys or signs locally.
/// A wallet provider must be explicitly supplied by the browser environment.</summary>
public sealed class BrowserWalletChallengeSigner(IJSRuntime js) : IStellarChallengeSigner
{
    public async Task<(string SignerAccountId, byte[] Signature)> SignAsync(
        StellarOwnershipChallenge challenge, CancellationToken cancellationToken = default)
    {
        if (!StellarOwnershipChallengeCodec.IsWellFormed(challenge))
            throw new ArgumentException("Invalid ownership challenge.", nameof(challenge));

        var response = await js.InvokeAsync<BrowserWalletSignature>(
            "stellarVaultV2Wallet.signChallenge", cancellationToken,
            challenge.AccountId, challenge.NetworkPassphrase,
            Convert.ToBase64String(challenge.Payload));

        if (response is null || !StellarAccountAddress.IsValid(response.SignerAccountId) ||
            string.IsNullOrWhiteSpace(response.SignatureBase64))
            throw new UnauthorizedAccessException("Wallet did not provide a valid signer response.");

        byte[] signature;
        try { signature = Convert.FromBase64String(response.SignatureBase64); }
        catch (FormatException) { throw new UnauthorizedAccessException("Invalid wallet signature encoding."); }
        if (signature.Length == 64) return (response.SignerAccountId!, signature);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(signature);
        throw new UnauthorizedAccessException("Wallet must return a 64-byte Ed25519 signature.");
    }

    private sealed class BrowserWalletSignature
    {
        public string? SignerAccountId { get; set; }
        public string? SignatureBase64 { get; set; }
    }
}
