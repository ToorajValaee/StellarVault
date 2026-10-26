# StellarVault shared Razor UI

Portable .NET 10 Razor Class Library referenced by the PWA and MAUI hosts.

Active components include `FreshVaultHome`, `AdaptiveShell`, `ManagedVaultSessionStatus`, `VaultRecordWorkspaceView`, and `VaultBinaryFilesView`. This library does not contain the legacy HD-wallet pages or router.

The Core owns encryption, session state and records. The PWA provides IndexedDB and JavaScript APIs; native MAUI provides local storage. File download UI currently uses browser JavaScript and is exposed only in the PWA.

## Current limits
- Account ownership signing is not integrated, so the fresh PWA vault-access controls are disabled.
- Record/file interfaces require an unlocked session and have not been build-verified end to end.
- No legacy data migration or v1 compatibility is supported.
