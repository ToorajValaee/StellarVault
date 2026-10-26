using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace StellarVault.Core.Services;

/// <summary>Versioned Argon2id parameters persisted alongside, not inside, the wrapped data key.
/// Salt is public; the passphrase and derived wrapping key must never be persisted.</summary>
public sealed record VaultKdfParameters(byte Version, byte[] Salt, int MemoryKiB, int Iterations, int Parallelism)
{
    public static VaultKdfParameters Create() =>
        new(1, RandomNumberGenerator.GetBytes(16), 65536, 3, 1);

    public void Validate()
    {
        if (Version != 1 || Salt is null || Salt.Length != 16 ||
            MemoryKiB < 65536 || MemoryKiB > 262144 ||
            Iterations < 3 || Iterations > 10 ||
            Parallelism < 1 || Parallelism > 4)
            throw new CryptographicException("Unsupported or unsafe vault KDF parameters.");
    }
}

/// <summary>Passphrase-derived 256-bit wrapping keys using a vetted Argon2id library.
/// This derivation does not establish ownership of the associated Stellar account.</summary>
public static class VaultPassphraseKdf
{
    public static async Task<byte[]> DeriveAsync(string passphrase, string accountId,
        VaultKdfParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(parameters);
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("A nonempty passphrase is required.", nameof(passphrase));
        StellarAccountAddress.Validate(accountId);
        parameters.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        // Domain-separate per Stellar account even when two accounts have equal passphrases.
        byte[] passwordBytes = Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));
        byte[] saltContext = Encoding.UTF8.GetBytes("StellarVault/passphrase-wrap/v1\0" + accountId);
        byte[] saltInput = new byte[parameters.Salt.Length + saltContext.Length];
        try
        {
            parameters.Salt.CopyTo(saltInput, 0);
            saltContext.CopyTo(saltInput, parameters.Salt.Length);
            byte[] domainSalt = SHA256.HashData(saltInput);
            try
            {
                using var argon = new Argon2id(passwordBytes)
                {
                    Salt = domainSalt,
                    DegreeOfParallelism = parameters.Parallelism,
                    Iterations = parameters.Iterations,
                    MemorySize = parameters.MemoryKiB
                };
                // GetBytesAsync is provided by the Argon2 library; caller owns returned key.
                byte[] key = await argon.GetBytesAsync(VaultKeyEnvelope.DataKeyLength);
                if (cancellationToken.IsCancellationRequested)
                {
                    CryptographicOperations.ZeroMemory(key);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(domainSalt); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(saltContext);
            CryptographicOperations.ZeroMemory(saltInput);
        }
    }
}
