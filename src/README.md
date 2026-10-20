# Shared application migration

See [migration plan](../docs/architecture/maui-migration.md).

The current production Blazor WebAssembly project remains at the repository root until the shared UI and Core libraries are extracted and tested. Do not duplicate vault implementations across mobile, desktop and web.

Planned projects: StellarVault.Core, StellarVault.UI, StellarVault.Web, StellarVault.Maui.
