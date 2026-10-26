using System.Security.Cryptography;
using System.Text;

namespace StellarVault.Core.Services;

/// <summary>Versioned authenticated wrapping of a randomly generated 256-bit vault data key.
/// The wrapping key MUST be independently obtained from a secure KDF or platform keystore;
/// raw passwords and Stellar secret seeds must never be passed as wrapping keys.</summary>
public static class VaultKeyEnvelope
{
    private static readonly byte[] Magic = "SVKW"u8.ToArray();
    public const byte Version = 1;
    public const int DataKeyLength = 32;
    public const int WrappedLength = 4 + 1 + 12 + DataKeyLength + 16;

    public static byte[] GenerateDataKey() => RandomNumberGenerator.GetBytes(DataKeyLength);

    /// <summary>Wrap the vault data key, binding it to the canonical Stellar account address.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> dataKey, ReadOnlySpan<byte> wrappingKey, string accountId)
    {
        Validate(dataKey, wrappingKey, accountId);
        byte[] result = new byte[WrappedLength];
        Magic.CopyTo(result, 0);
        result[4] = Version;
        RandomNumberGenerator.Fill(result.AsSpan(5, 12));
        byte[] aad = BuildContext(accountId);
        try
        {
            using var aes = new AesGcm(wrappingKey, 16);
            aes.Encrypt(result.AsSpan(5, 12), dataKey, result.AsSpan(17, 32),
                result.AsSpan(49, 16), aad);
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
        finally { CryptographicOperations.ZeroMemory(aad); }
    }

    /// <summary>Unwrap an authenticated data key. Wrong key/account or tampering fails closed.</summary>
    public static byte[] Unwrap(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> wrappingKey, string accountId)
    {
        ValidateKey(wrappingKey, nameof(wrappingKey));
        ValidateAccount(accountId);
        if (envelope.Length != WrappedLength || !envelope[..4].SequenceEqual(Magic) || envelope[4] != Version)
            throw new CryptographicException("Unsupported or malformed vault key envelope.");
        byte[] key = new byte[DataKeyLength];
        byte[] aad = BuildContext(accountId);
        try
        {
            using var aes = new AesGcm(wrappingKey, 16);
            aes.Decrypt(envelope.Slice(5, 12), envelope.Slice(17, 32),
                envelope.Slice(49, 16), key, aad);
            return key;
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
        finally { CryptographicOperations.ZeroMemory(aad); }
    }

    private static byte[] BuildContext(string accountId) =>
        Encoding.UTF8.GetBytes("StellarVault/key-wrap/v1\0" + accountId);

    private static void Validate(ReadOnlySpan<byte> dataKey, ReadOnlySpan<byte> wrappingKey, string accountId)
    {
        ValidateKey(dataKey, nameof(dataKey));
        ValidateKey(wrappingKey, nameof(wrappingKey));
        ValidateAccount(accountId);
    }

    private static void ValidateKey(ReadOnlySpan<byte> key, string name)
    {
        if (key.Length != DataKeyLength) throw new ArgumentException("Expected 256-bit key.", name);
    }

    private static void ValidateAccount(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        StellarAccountAddress.Validate(accountId);
    }
}
