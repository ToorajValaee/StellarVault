using System.Security.Cryptography;

namespace StellarVault.Core.Services;

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
        byte[] result = new byte[plaintext.Length + TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, result.AsSpan(0, plaintext.Length),
            result.AsSpan(plaintext.Length, TagLength), associatedData);
        return result;
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
        int length = ciphertextAndTag.Length - TagLength;
        byte[] plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertextAndTag[..length], ciphertextAndTag[length..], plaintext, associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static void Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeyLength) throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(key));
        if (nonce.Length != NonceLength) throw new ArgumentException("AES-GCM requires a 12-byte nonce.", nameof(nonce));
    }
}
