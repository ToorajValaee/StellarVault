# StellarVault

A fresh .NET 10, Stellar-account-centered encrypted vault/database. The native **.NET MAUI Blazor Hybrid** app and **Blazor WebAssembly PWA** share the same Core and Razor UI. **No legacy HD registry, v1 record migration, or mnemonic-based account allocation is supported.**

## Repository structure

```text
StellarVault.sln
src/
  StellarVault.Core/       Encrypted database, records, account authorization contracts
  StellarVault.UI/         Shared Razor components
  StellarVault.Maui/       Android, iOS, Windows, Mac Catalyst native host
  StellarVault.Pwa/        Browser/WebAssembly host, IndexedDB, PWA assets
tests/
  StellarVault.Core.CryptoTests/
docs/
  architecture/
website/                  Independent landing site
.github/workflows/        Cross-platform CI
Dockerfile                .NET 10 browser build and Nginx hosting
```

## Run the browser preview

```bash
dotnet restore src/StellarVault.Pwa/StellarVault.Pwa.csproj
dotnet run --project src/StellarVault.Pwa/StellarVault.Pwa.csproj
```

## Native development

Open `StellarVault.sln` and select `StellarVault.Maui` as the startup project. Install .NET 10 MAUI workload and the target platform toolchain. Build a target framework explicitly, for example `dotnet build src/StellarVault.Maui/StellarVault.Maui.csproj -f net10.0-android`.

## Security and release readiness

**Not yet production-ready.** Stellar wallet challenge signing currently fails closed; Create/Unlock are not available for a normal user. Encryption, record storage, and file UIs require complete build and end-to-end validation. GitHub-hosted CI runners have also failed to start in recent runs. Do not use this preview with valuable secrets.

The landing site in `website/` is independently maintained.
