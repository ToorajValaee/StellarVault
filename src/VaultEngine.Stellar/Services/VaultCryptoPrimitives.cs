using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto;

namespace VaultEngine.Stellar.Services;

/// <summary>Portable .NET 10 cryptographic building blocks for a future v2 vault format.
/// This type does not replace the existing vault storage format or implement password KDF.</summary>
public static class VaultCryptoPrimitives
{
    public const int KeyLength = 32;
    public const int NonceLength = 12;
    public const int TagLength = 16;

    public static byte[] NewRandomBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return RandomNumberGenerator.GetBytes(count);
    }

    /// <summary>Encrypt with a caller-managed nonce that must be unique for this key.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> nonce)
    {
        Validate(key, nonce);
        // System.Security.Cryptography.AesGcm is not available under browser WASM.
        // Managed GCM preserves the existing 12-byte nonce / 16-byte tag format.
        return Transform(true, key, plaintext, associatedData, nonce);
    }

    /// <summary>Generate a fresh random nonce and prefix it to authenticated ciphertext.
    /// Format: 12-byte nonce || ciphertext || 16-byte tag. Never reuse a key/nonce pair.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        byte[] nonce = NewRandomBytes(NonceLength);
        byte[] encrypted = Encrypt(key, plaintext, associatedData, nonce);
        byte[] envelope = new byte[NonceLength + encrypted.Length];
        nonce.CopyTo(envelope, 0);
        encrypted.CopyTo(envelope, NonceLength);
        return envelope;
    }

    /// <summary>Open a nonce-prefixed authenticated envelope.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> associatedData)
    {
        if (envelope.Length < NonceLength + TagLength)
            throw new CryptographicException("Invalid encrypted envelope.");
        return Decrypt(key, envelope[NonceLength..], associatedData, envelope[..NonceLength]);
    }

    /// <summary>Decrypt authenticated ciphertext; authentication failures erase the plaintext buffer.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ciphertextAndTag,
        ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> nonce)
    {
        Validate(key, nonce);
        if (ciphertextAndTag.Length < TagLength)
            throw new CryptographicException("Invalid authenticated ciphertext.");
        try { return Transform(false, key, ciphertextAndTag, associatedData, nonce); }
        catch (InvalidCipherTextException ex)
        {
            throw new CryptographicException("Authenticated vault ciphertext failed verification.", ex);
        }
    }

    private static byte[] Transform(bool encrypt, ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> input, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> nonce)
    {
        // Key/nonce buffers are temporary; never cache them in the cipher instance.
        byte[] keyBytes = key.ToArray();
        byte[] nonceBytes = nonce.ToArray();
        byte[] aadBytes = associatedData.ToArray();
        byte[] inputBytes = input.ToArray();
        byte[] output = new byte[encrypt ? inputBytes.Length + TagLength : inputBytes.Length - TagLength];
        try
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(encrypt, new AeadParameters(new KeyParameter(keyBytes), TagLength * 8,
                nonceBytes, aadBytes));
            int written = cipher.ProcessBytes(inputBytes, 0, inputBytes.Length, output, 0);
            written += cipher.DoFinal(output, written);
            if (written != output.Length)
                throw new CryptographicException("Unexpected authenticated encryption output length.");
            return output;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(nonceBytes);
            CryptographicOperations.ZeroMemory(aadBytes);
            CryptographicOperations.ZeroMemory(inputBytes);
        }
    }

    private static void Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeyLength) throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(key));
        if (nonce.Length != NonceLength) throw new ArgumentException("AES-GCM requires a 12-byte nonce.", nameof(nonce));
    }
}
