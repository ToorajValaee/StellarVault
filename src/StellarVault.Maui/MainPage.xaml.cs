using StellarVault.Core.Services;

namespace StellarVault.Maui;

public partial class MainPage : ContentPage
{
    private readonly VaultNativeNavigationState _navigation;
    private readonly NativeStartupStatus _startup;
    private bool _firstRenderReceived;
    private CancellationTokenSource? _startupWatchdog;

    public MainPage(VaultNativeNavigationState navigation, NativeStartupStatus startup)
    {
        _navigation = navigation;
        _startup = startup;
        InitializeComponent();
        _startup.FirstRenderCompleted += OnFirstRenderCompleted;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_firstRenderReceived)
        {
            _startupWatchdog?.Cancel();
            _startupWatchdog?.Dispose();
            _startupWatchdog = new CancellationTokenSource();
            _ = ReportUnresponsiveStartupAsync(_startupWatchdog.Token);
        }
    }

    private async Task ReportUnresponsiveStartupAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
            if (!_firstRenderReceived)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (_firstRenderReceived) return;
                    StartupSpinner.IsRunning = false;
                    NativeStartupMessage.Text =
                        "The secure interface did not start. Close and reopen StellarVault. " +
                        "If this persists, capture the Android app log: " +
                        "the native WebView may be missing a resource or failed to render.";
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnFirstRenderCompleted()
    {
        _firstRenderReceived = true;
        _startupWatchdog?.Cancel();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            NativeStartupOverlay.IsVisible = false;
            StartupSpinner.IsRunning = false;
        });
    }

    protected override bool OnBackButtonPressed()
    {
        if (_navigation.TryRequestBack()) return true;
        return base.OnBackButtonPressed();
    }
}
