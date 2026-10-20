using Microsoft.Extensions.Logging;
using StellarVault.Core.Services;

namespace StellarVault.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        // One application-owned session; never register an unlocked key as a scoped UI service.
        builder.Services.AddSingleton<VaultSessionManager>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
