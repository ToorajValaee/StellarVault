using System.Security.Cryptography;
using System.Text;

namespace StellarVault.Core.Services;

/// <summary>Atomic credential record: public KDF settings and authenticated wrapped data key
/// live in one file, avoiding an orphaned salt after interrupted creation or rotation.</summary>
public sealed class VaultPassphraseCredentials
{
    private static readonly byte[] Magic = "SVPC"u8.ToArray();
    public const int Length = 4 + 1 + 16 + 4 + 4 + 4 + VaultKeyEnvelope.WrappedLength;

    public static byte[] Encode(VaultKdfParameters parameters, ReadOnlySpan<byte> envelope)
    {
        parameters.Validate();
        if (envelope.Length != VaultKeyEnvelope.WrappedLength)
            throw new CryptographicException("Invalid wrapped vault key length.");
        byte[] record = new byte[Length];
        Magic.CopyTo(record, 0);
        record[4] = parameters.Version;
        parameters.Salt.CopyTo(record, 5);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(21, 4), parameters.MemoryKiB);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(25, 4), parameters.Iterations);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(29, 4), parameters.Parallelism);
        envelope.CopyTo(record.AsSpan(33));
        return record;
    }

    public static (VaultKdfParameters Parameters, byte[] Envelope) Decode(ReadOnlySpan<byte> record)
    {
        if (record.Length != Length || !record[..4].SequenceEqual(Magic))
            throw new CryptographicException("Invalid vault credential record.");
        var parameters = new VaultKdfParameters(record[4], record.Slice(5, 16).ToArray(),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(record.Slice(21, 4)),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(record.Slice(25, 4)),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(record.Slice(29, 4)));
        parameters.Validate();
        return (parameters, record[33..].ToArray());
    }
}

/// <summary>Immutable credential storage contract with atomic create and compare-and-swap.</summary>
public interface IVaultPassphraseCredentialStore
{
    Task<byte[]?> ReadAsync(string accountId, CancellationToken ct = default);
    Task<bool> CreateAsync(string accountId, ReadOnlyMemory<byte> record, CancellationToken ct = default);
    Task<bool> ReplaceAsync(string accountId, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, CancellationToken ct = default);
}

/// <summary>Passphrase workflow. Returns caller-owned plaintext key bytes, which MUST be
/// moved into a disposable session or cleared immediately. Account ownership is NOT proven.</summary>
public sealed class VaultPassphraseLifecycle(IVaultPassphraseCredentialStore store, IVaultKeyProtector protector)
{
    /// <summary>Read wrapped credential metadata for local-vault discovery.
    /// The caller owns the returned buffer and must clear it.</summary>
    public Task<byte[]?> ReadCredentialForDiscoveryAsync(string accountId, CancellationToken ct = default) =>
        store.ReadAsync(accountId, ct);

    public async Task<byte[]> CreateAsync(string accountId, string passphrase, CancellationToken ct = default)
    {
        var parameters = VaultKdfParameters.Create();
        byte[] wrappingKey = await VaultPassphraseKdf.DeriveAsync(passphrase, accountId, parameters, ct);
        byte[] dataKey = VaultKeyEnvelope.GenerateDataKey();
        try
        {
            byte[] wrapped = await protector.WrapAsync(dataKey, wrappingKey, accountId, ct);
            try
            {
                byte[] record = VaultPassphraseCredentials.Encode(parameters, wrapped);
                try
                {
                    if (!await store.CreateAsync(accountId, record, ct))
                        throw new InvalidOperationException("Vault credentials already exist for this account.");
                    return dataKey;
                }
                finally { CryptographicOperations.ZeroMemory(record); }
            }
            finally { CryptographicOperations.ZeroMemory(wrapped); }
        }
        catch { CryptographicOperations.ZeroMemory(dataKey); throw; }
        finally { CryptographicOperations.ZeroMemory(wrappingKey); }
    }

    public async Task<byte[]> UnlockAsync(string accountId, string passphrase, CancellationToken ct = default)
    {
        byte[] record = await store.ReadAsync(accountId, ct)
            ?? throw new FileNotFoundException("Vault credentials do not exist.");
        try
        {
            var (parameters, wrapped) = VaultPassphraseCredentials.Decode(record);
            try
            {
                byte[] wrappingKey = await VaultPassphraseKdf.DeriveAsync(passphrase, accountId, parameters, ct);
                try { return await protector.UnwrapAsync(wrapped, wrappingKey, accountId, ct); }
                finally { CryptographicOperations.ZeroMemory(wrappingKey); }
            }
            finally { CryptographicOperations.ZeroMemory(wrapped); }
        }
        finally { CryptographicOperations.ZeroMemory(record); }
    }

    public async Task RotateAsync(string accountId, string oldPassphrase, string newPassphrase, CancellationToken ct = default)
    {
        byte[] original = await store.ReadAsync(accountId, ct)
            ?? throw new FileNotFoundException("Vault credentials do not exist.");
        try
        {
            var (parameters, wrapped) = VaultPassphraseCredentials.Decode(original);
            byte[]? dataKey = null;
            try
            {
                byte[] oldKey = await VaultPassphraseKdf.DeriveAsync(oldPassphrase, accountId, parameters, ct);
                try { dataKey = await protector.UnwrapAsync(wrapped, oldKey, accountId, ct); }
                finally { CryptographicOperations.ZeroMemory(oldKey); }
                var nextParameters = VaultKdfParameters.Create();
                byte[] nextKey = await VaultPassphraseKdf.DeriveAsync(newPassphrase, accountId, nextParameters, ct);
                try
                {
                    byte[] nextWrapped = await protector.WrapAsync(dataKey, nextKey, accountId, ct);
                    try
                    {
                        byte[] replacement = VaultPassphraseCredentials.Encode(nextParameters, nextWrapped);
                        try
                        {
                            if (!await store.ReplaceAsync(accountId, original, replacement, ct))
                                throw new InvalidOperationException("Vault credentials changed during rotation.");
                        }
                        finally { CryptographicOperations.ZeroMemory(replacement); }
                    }
                    finally { CryptographicOperations.ZeroMemory(nextWrapped); }
                }
                finally { CryptographicOperations.ZeroMemory(nextKey); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
                if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally { CryptographicOperations.ZeroMemory(original); }
    }
}
