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
