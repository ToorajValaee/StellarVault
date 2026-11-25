# StellarVault

StellarVault is an encrypted vault for records, folders and files, with a browser PWA and a Stellar-backed storage workflow.

- [Website](https://svault.ir)
- [Open the PWA](https://app.svault.ir)
- [B2B API documentation](docs/API.md)

## How it works

Choose either a BIP-39 recovery phrase with deterministic Stellar HD accounts, or an imported Stellar secret key with an independent linked-account strategy. These credential paths are separate; they do not open the same vault interchangeably.

Records are encrypted on your device before publication to the selected Stellar network. Each write requires a fee/reserve review and explicit approval. Writes also require consent to an encrypted transaction-recovery journal. Uncertain submissions must be investigated before another attempt; they are never retried automatically.

Optional encrypted device storage supports offline drafts and folders, protected by a separate vault passphrase. Offline changes remain local until you reconnect, review costs and approve publication. Locking a session does not erase encrypted device data.

## B2B API

The authenticated API provides tenant-isolated network discovery and read-only transaction receipt inspection, including operator-configured private Stellar networks. API keys grant access only to configured network aliases.

The API does not store customer vaults, accept signing keys, submit transactions or confirm vault mutations. A successful receipt is not proof that a particular vault record was published. See [API setup and integration](docs/API.md).

## Current limitations

StellarVault remains a preview pending final release acceptance and independent security review. Use disposable accounts and synthetic data while evaluation continues. Do not use it for valuable secrets yet.

Mainnet S-key writes, automatic legacy migration and whole-file-tree deletion are unavailable. Large files remain subject to browser, device, ledger capacity and reserve limits. Streaming file exports require a browser supporting the Save File Picker API. Native app releases and EVM support are outside the current web release scope.

Keep independent backups of recovery credentials and important data. Clearing browser storage can remove encrypted drafts and recovery metadata. StellarVault cannot recover lost credentials or protect against a compromised device or malicious browser extension.

## Build locally

Install .NET 10 and Node.js, then run:

```sh
dotnet run --project src/StellarVault.Pwa/StellarVault.Pwa.csproj
```

The landing site is in `src/StellarVault.Landing/wwwroot`. The PWA and API consume the shared VaultEngine libraries; reusable Razor components live in `src/StellarVault.UI`.

See the [documentation index](docs/README.md), [deployment guide](docs/DEPLOYMENT.md) and [API documentation](docs/API.md). GitHub verification is manual-only and does not deploy or submit ledger transactions.
