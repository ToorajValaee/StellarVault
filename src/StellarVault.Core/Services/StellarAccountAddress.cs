namespace StellarVault.Core.Services;

/// <summary>Validates canonical Stellar Ed25519 public-account StrKey (G...) addresses,
/// including the XDR CRC16 checksum. Does not prove account ownership or existence.</summary>
public static class StellarAccountAddress
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const byte AccountVersion = 6 << 3;

    public static bool IsValid(string? address)
    {
        if (address is null || address.Length != 56 || address[0] != 'G') return false;
        Span<byte> decoded = stackalloc byte[35];
        int bits = 0, value = 0, position = 0;
        foreach (char character in address)
        {
            int digit = Alphabet.IndexOf(character);
            if (digit < 0) return false;
            value = (value << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (position >= decoded.Length) return false;
                decoded[position++] = (byte)(value >> bits);
                value &= (1 << bits) - 1;
            }
        }
        if (position != 35 || bits != 0 || value != 0 || decoded[0] != AccountVersion)
            return false;
        // Stellar StrKey uses CRC16-XModem (polynomial 0x1021, init 0),
        // serialized least-significant byte first. The reflected CRC16
        // variant (0x8408) is NOT compatible with Stellar addresses.
        ushort crc = 0;
        for (int i = 0; i < 33; i++)
        {
            crc ^= (ushort)(decoded[i] << 8);
            for (int j = 0; j < 8; j++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return decoded[33] == (byte)crc && decoded[34] == (byte)(crc >> 8);
    }

    /// <summary>Decode a checksummed Stellar G-address into its raw 32-byte Ed25519 public key.</summary>
    public static byte[] DecodePublicKey(string address)
    {
        Validate(address);
        const string alphabet = Alphabet;
        byte[] raw = new byte[35];
        int value = 0, bits = 0, offset = 0;
        foreach (char character in address)
        {
            value = (value << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                raw[offset++] = (byte)(value >> bits);
                value &= (1 << bits) - 1;
            }
        }
        return raw.AsSpan(1, 32).ToArray();
    }

    public static void Validate(string address)
    {
        if (!IsValid(address))
            throw new ArgumentException("Expected a valid canonical Stellar G-account StrKey with CRC16 checksum.", nameof(address));
    }
}
