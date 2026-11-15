using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>
/// Validates canonical public-only S-key v1 skh manifest bytes. The exact
/// base64 value is an optimistic-concurrency token, not inclusion or signer
/// proof. Validating is independent of the encrypted record's decryption key.
/// </summary>
public static class VaultLinkedRemoteRevisionPolicy
{
    public static bool IsValid(string? revision)
    {
        if (revision is not { Length: 64 }) return false;
        byte[] raw;
        try { raw = Convert.FromBase64String(revision); }
        catch (FormatException) { return false; }
        try
        {
            if (raw.Length != 47 || raw[0] != 1 ||
                Convert.ToBase64String(raw) != revision)
                return false;
            for (var i = 1; i < 9; i++)
                if (!((raw[i] >= (byte)'0' && raw[i] <= (byte)'9') ||
                      (raw[i] >= (byte)'a' && raw[i] <= (byte)'f')))
                    return false;
            var count = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(9, 2));
            var size = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(11, 4));
            return count is >= 1 and <= 89 && size >= 28 &&
                   (long)(count - 1) * 64 < size &&
                   (long)count * 64 >= size;
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
}
