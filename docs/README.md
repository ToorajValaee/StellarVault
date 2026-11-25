# StellarVault documentation

StellarVault is a .NET 10 encrypted vault with PWA and MAUI consumers of the independent VaultEngine libraries, plus an authenticated read-only HTTP diagnostics API.

## Current architecture
- [Vault v2 format](architecture/vault-v2-format.md)
- [Stellar account database](architecture/stellar-account-database.md)
- [Chunked encrypted records](architecture/chunked-records.md)
- [Vault key management](architecture/vault-key-management.md)

The code under `src/` is authoritative. HD mnemonic and imported S-key strategies remain independent. See the [engine boundary](architecture/account-object-engine.md), [API contract](API.md), [deployment runbook](DEPLOYMENT.md), and [production release gate](verification/production-release-gate.md).

The current release branch is `main`. Landing, PWA and the authenticated read-only B2B API are deployed on the VPS. See [current release status](verification/current-release-status.md) for tested behavior and outstanding acceptance checks. Older dated design notes describe earlier implementation stages.
