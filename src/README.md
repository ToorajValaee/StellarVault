# StellarVault source projects

All application projects are under `src/`:

- `StellarVault.Core/`: .NET 10 encrypted storage, sessions and Stellar authorization abstractions.
- `StellarVault.UI/`: shared Razor component library.
- `StellarVault.Maui/`: Android, iOS, Windows and Mac Catalyst Blazor Hybrid host.
- `StellarVault.Pwa/`: Blazor WebAssembly host, browser storage adapters and PWA assets.

Open the repository-root `StellarVault.sln`. There is no legacy PWA at the repository root and no v1 compatibility target. Hosts depend on the shared Core/UI projects. See the root README for commands and release limitations.
