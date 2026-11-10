using StellarVault.Core.Services;

namespace StellarVault.Maui;

public partial class App : Application
{
    private readonly VaultSessionManager _sessions;
    private readonly VaultNativeForegroundLifecycle _lifecycle;

    public App(VaultSessionManager sessions, VaultNativeForegroundLifecycle lifecycle)
    {
        InitializeComponent();
        _sessions = sessions;
        _lifecycle = lifecycle;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "StellarVault"
        };
#if WINDOWS
        // Start the Windows host as a genuine desktop workspace with room for
        // the navigation rail and independent content pane.
        window.Width = 1180;
        window.Height = 800;
        window.MinimumWidth = 720;
        window.MinimumHeight = 540;
#endif
        // Clear the unlocked data key when the native window leaves the foreground.
        // The preview UI remains disabled until end-to-end unlock is verified.
        window.Stopped += (_, _) => RevokeForeground();
        window.Destroying += (_, _) => RevokeForeground();
        return window;
    }

    private void RevokeForeground()
    {
        // The WebView signer must be revoked even if no local vault was opened.
        // A backgrounded on-chain-only session also holds live signing keys.
        _sessions.Lock();
        _lifecycle.NotifyForegroundLost();
    }
}
