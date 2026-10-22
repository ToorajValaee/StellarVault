using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Transient in-memory privacy lock. No passphrase, mnemonic or key is persisted here.
/// The ledger signer remains in process until explicit lock or document exit.
/// </summary>
public sealed class VaultPrivacyGate(VaultSessionManager sessions, VaultSessionWorkflow workflow)
{
    private string? _accountId;
    private bool _suspended;
    public bool IsSuspended => _suspended;
    public string? SuspendedAccountId => _suspended ? _accountId : null;
    public event Action? Changed;

    public void Suspend()
    {
        if (_suspended) return;
        _accountId = sessions.ActiveAccountId;
        if (_accountId is null) return;
        _suspended = true;
        sessions.Lock();
        Changed?.Invoke();
    }

    public async Task ResumeAsync(string passphrase)
    {
        if (!_suspended || _accountId is null) throw new InvalidOperationException("No suspended vault session.");
        if (string.IsNullOrWhiteSpace(passphrase)) throw new ArgumentException("Enter the vault passphrase.");
        await workflow.UnlockAsync(_accountId, passphrase);
        _suspended = false;
        _accountId = null;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _accountId = null;
        _suspended = false;
        Changed?.Invoke();
    }
}
