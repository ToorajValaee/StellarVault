namespace StellarVault.Core.Services;

/// <summary>
/// Explicit destination for vault writes. Local encrypted storage is never
/// interchangeable with confirmed Stellar ledger persistence.
/// </summary>
public enum VaultStorageMode
{
    LocalEncrypted = 0,
    StellarLedger = 1
}

/// <summary>
/// Shared fail-closed storage selection. A connected ledger transport is required
/// for on-chain operations; callers cannot silently redirect them to local storage.
/// Does not treat an open transport as proof of transaction confirmation.
/// </summary>
public sealed class VaultStorageModeGate(IStellarLedgerRecords ledger)
{
    public VaultStorageMode Mode { get; private set; } = VaultStorageMode.LocalEncrypted;

    public void Select(VaultStorageMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == VaultStorageMode.StellarLedger && !ledger.IsConnected)
            throw new InvalidOperationException("Connect to Stellar before selecting ledger storage.");
        Mode = mode;
    }

    public void Require(VaultStorageMode expected)
    {
        if (Mode != expected)
            throw new InvalidOperationException($"Storage mode mismatch: requested {expected}, selected {Mode}.");
        if (expected == VaultStorageMode.StellarLedger && !ledger.IsConnected)
            throw new InvalidOperationException("Stellar ledger session disconnected; no local fallback is permitted.");
    }

    public void Reset() => Mode = VaultStorageMode.LocalEncrypted;
}
