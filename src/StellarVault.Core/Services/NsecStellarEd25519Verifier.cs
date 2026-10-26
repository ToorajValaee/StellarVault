using NSec.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>Concrete Ed25519 verification backed by the vetted NSec/libsodium implementation.
/// Only canonical, checksummed Stellar G-addresses are accepted.</summary>
public sealed class NsecStellarEd25519Verifier : IStellarEd25519SignatureVerifier
{
    public bool Verify(string signerAccountId, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (!StellarAccountAddress.IsValid(signerAccountId) || signature.Length != 64)
            return false;
        byte[] rawPublicKey = StellarAccountAddress.DecodePublicKey(signerAccountId);
        try
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = PublicKey.Import(algorithm, rawPublicKey, KeyBlobFormat.RawPublicKey);
            return algorithm.Verify(publicKey, message, signature);
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(rawPublicKey);
        }
    }
}
