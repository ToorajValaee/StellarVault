# StellarVault MAUI Blazor Hybrid host

**Status: preview scaffold only — not a functioning vault, not build-verified.**

This is one native host targeting Android, iOS, Mac Catalyst and Windows (Windows target included when building on Windows). It references the shared `StellarVault.UI` Razor Class Library and `StellarVault.Core`.

The initial native screen is **intentionally non-functional**: no mnemonic input, key derivation, ledger calls, or local secret persistence. Do not expose vault controls until the existing browser crypto bridge, vault session and storage abstractions have been migrated and tested for cross-platform compatibility.

## Build prerequisites
Install the .NET 8 MAUI workloads and the relevant Android/iOS/Mac Catalyst/Windows toolchains. Apple targets require compatible macOS tooling. Example Android build on a configured machine:

```bash
dotnet workload install maui
dotnet build src/StellarVault.Maui/StellarVault.Maui.csproj -f net8.0-android
```

These commands have **not** been executed in this environment. Version and platform configuration may need adjustment after the first native CI build.

## Release gates
- UI, session and browser/native crypto compatibility tests.
- Test vectors for SEP-0005 and legacy account derivation.
- Encrypted record interoperability and cross-device recovery.
- Clear network cost approvals for all ledger writes.
- Native secret lifecycle and platform security review.

See [Step 05](../../docs/steps/05-maui-host.md).


## Native vault session lifecycle

The MAUI host registers `VaultSessionManager` as a singleton and locks its active session on the window `Stopped` and `Destroying` events. This is defense in depth for future unlock integration; the native preview still disables secrets and transactions. Platform-specific lifecycle handling, timeout locking, secure passphrase derivation, and end-to-end validation remain required before enabling the vault. The session manager is not the existing browser `IVaultOperations` service; no claim of complete UI migration is made.
