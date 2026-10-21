# StellarVault documentation

StellarVault is a fresh .NET 10 encrypted, Stellar-account-centered database with PWA and MAUI hosts.

## Current architecture
- [Vault v2 format](architecture/vault-v2-format.md)
- [Stellar account database](architecture/stellar-account-database.md)
- [Chunked encrypted records](architecture/chunked-records.md)
- [Vault key management](architecture/vault-key-management.md)

The code under `src/` is authoritative. Previous HD-account registry / v1 compatibility plans are not part of the current product. The project is not yet production-ready: wallet proof integration and application builds still need validation.

Work only on `feature/maui-shared-app`, without automatic merge or deployment.
