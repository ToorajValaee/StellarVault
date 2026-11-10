using StellarVault.Core.Services;

namespace StellarVault.Maui;

public partial class MainPage : ContentPage
{
    private readonly VaultNativeNavigationState _navigation;

    public MainPage(VaultNativeNavigationState navigation)
    {
        _navigation = navigation;
        InitializeComponent();
    }

    protected override bool OnBackButtonPressed()
    {
        // Only consume Android Back if an active secondary destination exists.
        // No network submission, approval, or local unlock is triggered here.
        if (_navigation.TryRequestBack()) return true;
        return base.OnBackButtonPressed();
    }
}
