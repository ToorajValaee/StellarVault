# StellarVault documentation

StellarVault is a .NET 10 encrypted vault with PWA and MAUI consumers of the independent VaultEngine libraries, plus an authenticated read-only HTTP diagnostics API.

## Current architecture
- [Vault v2 format](architecture/vault-v2-format.md)
- [Stellar account database](architecture/stellar-account-database.md)
- [Chunked encrypted records](architecture/chunked-records.md)
- [Vault key management](architecture/vault-key-management.md)

The code under `src/` is authoritative. HD mnemonic and imported S-key strategies remain independent. See the [engine boundary](architecture/account-object-engine.md), [API contract](API.md), [deployment runbook](DEPLOYMENT.md), and [production release gate](verification/production-release-gate.md).

Current launch preparation is on `architecture/pluggable-vault-records-and-chains`. Production merge/deployment is conditional on release evidence; local build success does not establish security or recoverability.
