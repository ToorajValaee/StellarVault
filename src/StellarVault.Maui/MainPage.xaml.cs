using StellarVault.UI.Services;
using VaultEngine.Stellar.Services;
using Microsoft.AspNetCore.Components.WebView;

namespace StellarVault.Maui;

public partial class MainPage : ContentPage
{
    private readonly VaultNativeNavigationState _navigation;
    private readonly NativeStartupStatus _startup;
    private bool _firstRenderReceived;
    private string _startupStage = "MAUI page created; waiting for WebView";
    private string _androidWebViewInfo = string.Empty;
    private CancellationTokenSource? _startupWatchdog;

    public MainPage(VaultNativeNavigationState navigation, NativeStartupStatus startup)
    {
        _navigation = navigation;
        _startup = startup;
        InitializeComponent();
        _startup.FirstRenderCompleted += OnFirstRenderCompleted;
        LogStartupStage(_startupStage);
    }

    private void OnWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        SetStartupStage("Initializing native WebView engine");
    }

    private void OnWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        SetStartupStage("WebView initialized; waiting for Blazor bridge");
#if ANDROID
        try
        {
            var api = (int)Android.OS.Build.VERSION.SdkInt;
            var webviewVersion = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? Android.Webkit.WebView.CurrentWebViewPackage?.VersionName
                : "unavailable before API 26";
            // Android API level / WebView version are public OS metadata,
            // never account identifiers or captured JavaScript messages.
            _androidWebViewInfo = $"Android API {api} / WebView {webviewVersion ?? "not found"}";
            LogStartupStage(_androidWebViewInfo);
        }
        catch (Exception)
        {
            _androidWebViewInfo = "Android WebView version unavailable";
        }
#endif
    }

    private void SetStartupStage(string stage)
    {
        _startupStage = stage;
        LogStartupStage(stage);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_firstRenderReceived)
                NativeStartupMessage.Text = stage + "…";
        });
    }

    private static void LogStartupStage(string stage)
    {
        // Only fixed, credential-free startup stages are ever logged.
        System.Diagnostics.Debug.WriteLine("StellarVaultStartup: " + stage);
#if ANDROID
        Android.Util.Log.Info("StellarVaultStartup", stage);
#endif
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
                        "Startup stopped at: " + _startupStage + ". " +
                        _androidWebViewInfo + ". " +
                        "If Blazor did not load, check the emulator's Android System WebView " +
                        "and capture logcat for StellarVaultStartup, chromium and AndroidRuntime.";
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnFirstRenderCompleted()
    {
        _firstRenderReceived = true;
        LogStartupStage("First Razor render completed");
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
