using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>
/// Versioned, authenticated binary envelope for an already-derived 256-bit key.
/// Password derivation, persistence and mnemonic serialization are intentionally separate.
/// Layout: 4-byte magic (SVLT), 1-byte version, 12-byte nonce, ciphertext, 16-byte tag.
/// The header and caller-provided context are authenticated as AES-GCM associated data.
/// </summary>
public static class VaultV2Envelope
{
    private static ReadOnlySpan<byte> Magic => "SVLT"u8;
    public const byte Version = 2;
    public const int MaximumPlaintextLength = 1024 * 1024;
    public const int MaximumContextLength = 1024;
    public const int HeaderLength = 5;
    public const int MinimumLength = HeaderLength + VaultCryptoPrimitives.NonceLength + VaultCryptoPrimitives.TagLength;
    public const int MaximumLength = MinimumLength + MaximumPlaintextLength;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context)
    {
        if (plaintext.Length > MaximumPlaintextLength)
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Vault payload exceeds the maximum size.");
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = Version;
        byte[] aad = BindContext(header, context);
        try
        {
            byte[] payload = VaultCryptoPrimitives.Seal(key, plaintext, aad);
            byte[] result = new byte[HeaderLength + payload.Length];
            header.CopyTo(result, 0);
            payload.CopyTo(result, HeaderLength);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(aad); }
    }

    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> context)
    {
        if (envelope.Length < MinimumLength || envelope.Length > MaximumLength ||
            !envelope[..4].SequenceEqual(Magic) || envelope[4] != Version)
            throw new CryptographicException("Unsupported or invalid vault envelope.");
        byte[] aad = BindContext(envelope[..HeaderLength], context);
        try { return VaultCryptoPrimitives.Open(key, envelope[HeaderLength..], aad); }
        finally { CryptographicOperations.ZeroMemory(aad); }
    }

    private static byte[] BindContext(ReadOnlySpan<byte> header, ReadOnlySpan<byte> context)
    {
        if (context.Length > MaximumContextLength) throw new ArgumentOutOfRangeException(nameof(context));
        byte[] aad = new byte[HeaderLength + 4 + context.Length];
        header.CopyTo(aad);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(HeaderLength, 4), context.Length);
        context.CopyTo(aad.AsSpan(HeaderLength + 4));
        return aad;
    }
}
