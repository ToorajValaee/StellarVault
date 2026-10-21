# StellarVault native host (.NET MAUI Blazor Hybrid)

The `StellarVault.Maui.csproj` targets Android, iOS, Mac Catalyst, and Windows from a single native project. Its screen uses `StellarVault.UI`, and its filesystem persistence and session services use `StellarVault.Core`.

## Build
Use the .NET 10 SDK, MAUI workloads, and platform-specific tooling. Android example:

```bash
dotnet workload install maui-android
dotnet build src/StellarVault.Maui/StellarVault.Maui.csproj -f net10.0-android
```

Apple targets require macOS/Xcode and signing. Windows requires a supported Windows development environment. Builds and device testing are not yet verified.

## Security and release status
- Ownership authorization remains deny-by-default; native Create/Unlock currently cannot succeed.
- Session lock hooks exist for native lifecycle changes.
- Native file selection/export, wallet signing, packaging and end-to-end tests are unfinished.
- No support for old HD-derived records or v1 migration.
