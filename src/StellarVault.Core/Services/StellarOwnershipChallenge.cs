using System.Security.Cryptography;
using System.Text;

namespace StellarVault.Core.Services;

/// <summary>A short-lived request for proof of control of an authorized Stellar signer.
/// The full challenge payload (not just the nonce) must be signed.</summary>
public sealed record StellarOwnershipChallenge(
    string AccountId, string NetworkPassphrase, byte[] Payload, DateTimeOffset ExpiresAt);

/// <summary>Validate the exact signed payload, not only mutable challenge metadata.
/// This prevents an independently constructed challenge object from substituting
/// an unrelated signature payload while claiming a valid account/network/expiry.</summary>
public static class StellarOwnershipChallengeCodec
{
    private const string Domain = "StellarVault/ownership/v1";

    public static bool IsWellFormed(StellarOwnershipChallenge? challenge)
    {
        if (challenge is null || !StellarAccountAddress.IsValid(challenge.AccountId) ||
            string.IsNullOrWhiteSpace(challenge.NetworkPassphrase) ||
            challenge.Payload is null || challenge.Payload.Length > 2048 ||
            challenge.Payload.Length < 80)
            return false;

        try
        {
            using var stream = new MemoryStream(challenge.Payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadString() != Domain ||
                reader.ReadString() != challenge.AccountId ||
                reader.ReadString() != challenge.NetworkPassphrase ||
                reader.ReadInt64() != challenge.ExpiresAt.ToUnixTimeSeconds() ||
                reader.ReadInt32() != 32 ||
                reader.ReadBytes(32).Length != 32)
                return false;
            return stream.Position == stream.Length;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or
                                    ArgumentException or FormatException or DecoderFallbackException)
        {
            return false;
        }
    }
}

/// <summary>Platform wallet bridge. The implementation must return a signer public
/// StrKey and signature of the exact challenge bytes without exporting private keys.</summary>
public interface IStellarChallengeSigner
{
    Task<(string SignerAccountId, byte[] Signature)> SignAsync(
        StellarOwnershipChallenge challenge, CancellationToken cancellationToken = default);
}

/// <summary>Independent ledger/signature verifier. It must load the account's CURRENT
/// signers, verify the Ed25519 signature and enforce applicable signer permissions.
/// The requester must not be allowed to provide or override the signer set.</summary>
public interface IStellarOwnershipProofVerifier
{
    Task<bool> VerifyAsync(StellarOwnershipChallenge challenge, string signerAccountId,
        ReadOnlyMemory<byte> signature, CancellationToken cancellationToken = default);
}

/// <summary>One-shot, short-lived ownership challenge orchestration. An authorized
/// verifier and wallet signer must both be configured before this can succeed.
/// Never accept an address or a locally asserted signature as ownership proof.</summary>
public sealed class StellarChallengeAccountAuthorizer(
    IStellarChallengeSigner signer,
    IStellarOwnershipProofVerifier verifier,
    string networkPassphrase) : IVaultAccountAuthorizer
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task VerifyOwnershipAsync(string accountId, CancellationToken cancellationToken = default)
    {
        StellarAccountAddress.Validate(accountId);
        if (string.IsNullOrWhiteSpace(networkPassphrase))
            throw new InvalidOperationException("A network passphrase is required for account authorization.");
        await _gate.WaitAsync(cancellationToken);
        byte[]? nonce = null;
        byte[]? payload = null;
        byte[]? signature = null;
        try
        {
            nonce = RandomNumberGenerator.GetBytes(32);
            var expires = DateTimeOffset.UtcNow.Add(Lifetime);
            // Length-prefix variable strings to prevent ambiguous encodings.
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("StellarVault/ownership/v1");
                writer.Write(accountId);
                writer.Write(networkPassphrase);
                writer.Write(expires.ToUnixTimeSeconds());
                writer.Write(nonce.Length);
                writer.Write(nonce);
            }
            payload = buffer.ToArray();
            var challenge = new StellarOwnershipChallenge(accountId, networkPassphrase, payload, expires);
            var response = await signer.SignAsync(challenge, cancellationToken);
            signature = response.Signature;
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= expires)
                throw new UnauthorizedAccessException("Stellar authorization challenge expired.");
            if (signature is null || signature.Length != 64 ||
                !StellarAccountAddress.IsValid(response.SignerAccountId) ||
                !await verifier.VerifyAsync(challenge, response.SignerAccountId, signature, cancellationToken))
                throw new UnauthorizedAccessException("Stellar ownership proof was rejected.");
            if (DateTimeOffset.UtcNow >= expires)
                throw new UnauthorizedAccessException("Stellar authorization challenge expired.");
        }
        finally
        {
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            _gate.Release();
        }
    }
}
