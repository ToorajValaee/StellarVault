using Android.App;
using Android.Content.PM;
using Android.Views;

namespace StellarVault.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    // Resize the WebView viewport when the soft keyboard opens so the
    // independent native content scroller can reach focused form fields.
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity { }
