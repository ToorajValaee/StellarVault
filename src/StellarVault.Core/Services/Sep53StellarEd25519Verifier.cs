using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>
/// Verifies Freighter/SEP-53 signed ownership messages. The wallet signs the
/// SHA-256 digest of the SEP-53 prefix followed by the exact original
/// challenge bytes, without Base64 encoding (SEP-53 canonical layout).
/// </summary>
public sealed class Sep53StellarEd25519Verifier(IStellarEd25519SignatureVerifier ed25519)
    : IStellarEd25519SignatureVerifier
{
    public bool Verify(string signerAccountId, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature)
    {
        if (challenge.IsEmpty || challenge.Length > 2048 || signature.Length != 64) return false;
        ReadOnlySpan<byte> prefix = "Stellar Signed Message:\n"u8;
        byte[] encoded = new byte[prefix.Length + challenge.Length];
        prefix.CopyTo(encoded);
        challenge.CopyTo(encoded.AsSpan(prefix.Length));
        try
        {
            byte[] digest = SHA256.HashData(encoded);
            try { return ed25519.Verify(signerAccountId, digest, signature); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
}
