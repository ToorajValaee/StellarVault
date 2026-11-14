# StellarVault

A .NET 10, Stellar-account-centered encrypted vault/database. The native **.NET MAUI Blazor Hybrid** app and **Blazor WebAssembly PWA** share Core services and Razor UI. StellarVault supports a BIP-39/SEP-5 mnemonic HD architecture and is implementing a separately versioned **Stellar Secret Key / linked-account** architecture. They are independent credential paths, not different passwords for one HD wallet. **Automatic legacy vault migration is not supported.**

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

## Secret Key linked-account Testnet preview

The imported Stellar S-key path uses a distinct on-chain record format and authorizes independently created child accounts with the root public key as a signer. The same UI component and C# transport run in PWA and MAUI. Testnet encrypted records/folders can be reviewed for create, update, delete and chain extension. Each operation requires a separate fee/reserve quote and explicit approval; no automatic transaction retries occur after an uncertain outcome.

**Secret Key binary files, encrypted offline drafts, Hybrid Sync, original pre-HD vault migration and historical transaction proof are incomplete. Mainnet S-key mutations are blocked.** The HD architecture remains separate and is not modified by the S-key backend. Read [docs/SECRET_KEY_CHAIN_OPTION.md](docs/SECRET_KEY_CHAIN_OPTION.md) for the exact security boundaries.

Relevant non-network protocol regression tests:

```bash
node --test tests/secret-key-chain.test.cjs tests/secret-key-ledger.test.cjs
```

## Security and release readiness

**Not yet production-ready.** Complete .NET 10 builds, platform testing, live Stellar Testnet recovery and independent security review before entering secrets protecting real funds. Current-state ledger recovery and mocked transaction receipts alone are not production proof. Do not use this preview with valuable secrets.

The landing site in `website/` is independently maintained.
