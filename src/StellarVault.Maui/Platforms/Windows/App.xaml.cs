namespace StellarVault.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App() => InitializeComponent();
    protected override MauiApp CreateMauiApp() => StellarVault.Maui.MauiProgram.CreateMauiApp();
}
