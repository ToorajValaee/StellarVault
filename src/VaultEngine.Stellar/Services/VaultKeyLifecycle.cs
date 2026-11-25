using System.Security.Cryptography;

namespace VaultEngine.Stellar.Services;

/// <summary>Persistent storage for opaque wrapped vault keys, keyed by Stellar account.
/// Implementations must provide atomic create-if-absent and compare-and-swap semantics.</summary>
public interface IVaultKeyEnvelopeStore
{
    Task<byte[]?> ReadAsync(string accountId, CancellationToken cancellationToken = default);
    Task<bool> CreateAsync(string accountId, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken = default);
    Task<bool> ReplaceAsync(string accountId, ReadOnlyMemory<byte> expectedEnvelope,
        ReadOnlyMemory<byte> nextEnvelope, CancellationToken cancellationToken = default);
}

/// <summary>Provision, unlock, and rewrap vault data keys without ever persisting plaintext keys.
/// The caller is responsible for obtaining wrapping keys from a secure KDF or platform keystore.</summary>
public sealed class VaultKeyLifecycle(IVaultKeyEnvelopeStore store)
{
    public async Task<byte[]> CreateAsync(string accountId, ReadOnlyMemory<byte> wrappingKey,
        CancellationToken cancellationToken = default)
    {
        byte[] dataKey = VaultKeyEnvelope.GenerateDataKey();
        try
        {
            byte[] wrapped = VaultKeyEnvelope.Wrap(dataKey, wrappingKey.Span, accountId);
            try
            {
                if (!await store.CreateAsync(accountId, wrapped, cancellationToken))
                    throw new InvalidOperationException("A vault key already exists for this account.");
                return dataKey;
            }
            finally { CryptographicOperations.ZeroMemory(wrapped); }
        }
        catch { CryptographicOperations.ZeroMemory(dataKey); throw; }
    }

    public async Task<byte[]> UnlockAsync(string accountId, ReadOnlyMemory<byte> wrappingKey,
        CancellationToken cancellationToken = default)
    {
        byte[] wrapped = await store.ReadAsync(accountId, cancellationToken)
            ?? throw new FileNotFoundException("Vault key envelope does not exist.");
        try { return VaultKeyEnvelope.Unwrap(wrapped, wrappingKey.Span, accountId); }
        finally { CryptographicOperations.ZeroMemory(wrapped); }
    }

    /// <summary>Rotate wrapping credentials while keeping the encrypted data key unchanged.
    /// A concurrent rotation fails instead of overwriting a newer envelope.</summary>
    public async Task RewrapAsync(string accountId, ReadOnlyMemory<byte> oldWrappingKey,
        ReadOnlyMemory<byte> newWrappingKey, CancellationToken cancellationToken = default)
    {
        byte[] original = await store.ReadAsync(accountId, cancellationToken)
            ?? throw new FileNotFoundException("Vault key envelope does not exist.");
        try
        {
            byte[] dataKey = VaultKeyEnvelope.Unwrap(original, oldWrappingKey.Span, accountId);
            try
            {
                byte[] replacement = VaultKeyEnvelope.Wrap(dataKey, newWrappingKey.Span, accountId);
                try
                {
                    if (!await store.ReplaceAsync(accountId, original, replacement, cancellationToken))
                        throw new InvalidOperationException("Vault key envelope changed during rotation.");
                }
                finally { CryptographicOperations.ZeroMemory(replacement); }
            }
            finally { CryptographicOperations.ZeroMemory(dataKey); }
        }
        finally { CryptographicOperations.ZeroMemory(original); }
    }
}
