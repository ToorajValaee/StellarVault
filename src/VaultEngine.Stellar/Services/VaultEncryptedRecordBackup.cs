using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace VaultEngine.Stellar.Services;

/// <summary>
/// Portable password-encrypted backup of ordinary record interchange only.
/// Does NOT contain binary file bodies, signing keys, encrypted local journals,
/// identity credentials or on-chain transaction history.
/// Wire v1: "SVRB" || version || 16-byte salt || nonce || ciphertext || GCM tag.
/// The entire header is bound as authenticated associated data.
/// </summary>
public static class VaultEncryptedRecordBackup
{
    private static ReadOnlySpan<byte> Magic => "SVRB"u8;
    private const byte Version = 1;
    private const int SaltLength = 16;
    private const int HeaderLength = 4 + 1 + SaltLength;
    private const int MemoryKiB = 65536;
    private const int Iterations = 3;
    private const int Parallelism = 1;
    public const int MaximumBackupBytes = VaultRecordInterchange.MaximumBytes +
        HeaderLength + VaultCryptoPrimitives.NonceLength + VaultCryptoPrimitives.TagLength;

    public static async Task<byte[]> EncryptAsync(
        VaultRecordInterchange.Archive archive, string passphrase,
        CancellationToken ct = default)
    {
        RequirePassphrase(passphrase);
        byte[] plaintext = VaultRecordInterchange.ExportJson(archive);
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = Version;
        RandomNumberGenerator.Fill(header.AsSpan(5, SaltLength));
        byte[]? key = null;
        byte[]? ciphertext = null;
        try
        {
            key = await DeriveKeyAsync(passphrase, header.AsMemory(5, SaltLength), ct);
            ct.ThrowIfCancellationRequested();
            ciphertext = VaultCryptoPrimitives.Seal(key, plaintext, header);
            var result = new byte[header.Length + ciphertext.Length];
            header.CopyTo(result, 0);
            ciphertext.CopyTo(result, header.Length);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(header);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public static async Task<VaultRecordInterchange.Archive> DecryptAsync(
        ReadOnlyMemory<byte> backup, string passphrase,
        CancellationToken ct = default)
    {
        RequirePassphrase(passphrase);
        if (backup.Length is < HeaderLength + VaultCryptoPrimitives.NonceLength +
            VaultCryptoPrimitives.TagLength or > MaximumBackupBytes ||
            !backup.Span[..4].SequenceEqual(Magic) || backup.Span[4] != Version)
            throw new CryptographicException("Unsupported or invalid encrypted record backup.");
        var header = backup.Span[..HeaderLength].ToArray();
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            key = await DeriveKeyAsync(passphrase, header.AsMemory(5, SaltLength), ct);
            ct.ThrowIfCancellationRequested();
            plaintext = VaultCryptoPrimitives.Open(key,
                backup.Span[HeaderLength..], header);
            ct.ThrowIfCancellationRequested();
            return VaultRecordInterchange.ImportJson(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void RequirePassphrase(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length < 12 || passphrase.Length > 1024 ||
            string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException(
                "Encrypted record backups require a passphrase of 12–1024 characters.",
                nameof(passphrase));
    }

    private static async Task<byte[]> DeriveKeyAsync(
        string passphrase, ReadOnlyMemory<byte> salt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] password = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        byte[] domain = Encoding.ASCII.GetBytes("StellarVault/portable-record-backup/v1");
        var saltInput = new byte[domain.Length + salt.Length];
        try
        {
            domain.CopyTo(saltInput, 0);
            salt.Span.CopyTo(saltInput.AsSpan(domain.Length));
            byte[] separatedSalt = SHA256.HashData(saltInput);
            try
            {
                using var argon = new Argon2id(password)
                {
                    Salt = separatedSalt,
                    MemorySize = MemoryKiB,
                    Iterations = Iterations,
                    DegreeOfParallelism = Parallelism
                };
                byte[] key = await argon.GetBytesAsync(VaultCryptoPrimitives.KeyLength);
                if (ct.IsCancellationRequested)
                {
                    CryptographicOperations.ZeroMemory(key);
                    ct.ThrowIfCancellationRequested();
                }
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(separatedSalt); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(domain);
            CryptographicOperations.ZeroMemory(saltInput);
        }
    }
}
