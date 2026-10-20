using StellarVault.Core.Services;

namespace StellarVault.Maui;

public partial class App : Application
{
    private readonly VaultSessionManager _sessions;

    public App(VaultSessionManager sessions)
    {
        InitializeComponent();
        _sessions = sessions;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage());
        // Clear the unlocked data key when the native window leaves the foreground.
        // The preview UI remains disabled until end-to-end unlock is verified.
        window.Stopped += (_, _) => _sessions.Lock();
        window.Destroying += (_, _) => _sessions.Lock();
        return window;
    }
}
