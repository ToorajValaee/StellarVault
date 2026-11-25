namespace StellarVault.UI.Services;

/// <summary>
/// Native application lifecycle signal shared with its BlazorWebView.
/// Losing foreground privileges revokes the in-memory Stellar signing session,
/// in addition to locking the device vault key. Does not persist secrets.
/// </summary>
public sealed class VaultNativeForegroundLifecycle
{
    public event Action? ForegroundLost;

    public void NotifyForegroundLost() => ForegroundLost?.Invoke();
}
