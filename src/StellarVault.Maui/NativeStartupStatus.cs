namespace StellarVault.Maui;

/// <summary>
/// Credential-free MAUI host startup signal. The native XAML loading surface
/// must not rely on WebView JavaScript to report a failure to render.
/// </summary>
public sealed class NativeStartupStatus
{
    public event Action? FirstRenderCompleted;
    public void NotifyFirstRender() => FirstRenderCompleted?.Invoke();
}
