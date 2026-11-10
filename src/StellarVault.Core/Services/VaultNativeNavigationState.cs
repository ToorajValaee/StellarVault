namespace StellarVault.Core.Services;

/// <summary>
/// Foreground-only routing hint between MAUI's Android Back button and the
/// native BlazorWebView shell. It holds no credentials, transaction intent,
/// queue revision or persistent navigation history.
/// </summary>
public sealed class VaultNativeNavigationState
{
    public event Action? BackRequested;

    public bool CanNavigateBack { get; private set; }

    public void SetSecondaryDestination(bool selected) =>
        CanNavigateBack = selected;

    public bool TryRequestBack()
    {
        if (!CanNavigateBack || BackRequested is null) return false;
        BackRequested.Invoke();
        return true;
    }

    public void Clear() => CanNavigateBack = false;
}
