using System.Security.Cryptography;
using System.Text;

namespace StellarVault.Core.Services;

/// <summary>
/// Verifies Freighter/SEP-53 signed ownership messages. The wallet signs the
/// SHA-256 digest of the SEP-53 prefix followed by the UTF-8 base64 encoding
/// of the exact domain-separated challenge bytes.
/// </summary>
public sealed class Sep53StellarEd25519Verifier(IStellarEd25519SignatureVerifier ed25519)
    : IStellarEd25519SignatureVerifier
{
    public bool Verify(string signerAccountId, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> signature)
    {
        if (challenge.IsEmpty || challenge.Length > 2048 || signature.Length != 64) return false;
        byte[] encoded = Encoding.ASCII.GetBytes("Stellar Signed Message:\n" + Convert.ToBase64String(challenge));
        try
        {
            byte[] digest = SHA256.HashData(encoded);
            try { return ed25519.Verify(signerAccountId, digest, signature); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
}
