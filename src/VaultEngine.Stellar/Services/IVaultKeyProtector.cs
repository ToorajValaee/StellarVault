namespace VaultEngine.Stellar.Services;

/// <summary>Platform-specific AES-256-GCM wrapper for the vault data key.
/// Implementations MUST preserve the SVKW/v1 credential envelope exactly.</summary>
public interface IVaultKeyProtector
{
    Task<byte[]> WrapAsync(byte[] dataKey, byte[] wrappingKey, string accountId, CancellationToken ct = default);
    Task<byte[]> UnwrapAsync(byte[] envelope, byte[] wrappingKey, string accountId, CancellationToken ct = default);
}

/// <summary>Native .NET AES-GCM implementation; not registered in browser WASM.</summary>
public sealed class NativeVaultKeyProtector : IVaultKeyProtector
{
    public Task<byte[]> WrapAsync(byte[] dataKey, byte[] wrappingKey, string accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(VaultKeyEnvelope.Wrap(dataKey, wrappingKey, accountId));
    }
    public Task<byte[]> UnwrapAsync(byte[] envelope, byte[] wrappingKey, string accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(VaultKeyEnvelope.Unwrap(envelope, wrappingKey, accountId));
    }
}
