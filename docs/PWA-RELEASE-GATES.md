# StellarVault PWA release gates

**Status: NOT APPROVED FOR PRODUCTION.** Do not deploy for custody of real keys or valuable secrets based only on a successful compilation.

## P0: functional and cryptographic validation
- [ ] Reproduce a clean `dotnet restore` and Release `dotnet publish` of `src/StellarVault.Pwa/StellarVault.Pwa.csproj` with .NET 10.
- [ ] Run `dotnet run --project tests/StellarVault.Core.CryptoTests/StellarVault.Core.CryptoTests.csproj -c Release`, including official SEP-53 signature vector.
- [ ] Verify SEP-5 BIP-39 derivation vectors and confirm only public account IDs reach Horizon.
- [ ] Verify disposable mnemonic + passphrase can create a local-only vault, lock and reopen after browser restart; no Stellar ownership claim.
- [ ] Reject wrong passphrase and tampered credential envelopes; test separate network namespaces. SWallet signer verification belongs to a later release.
- [ ] Add a fresh-user error state distinct from wrong-passphrase and existing-vault errors.
- [ ] Prove encrypted text CRUD and file CRUD (upload, download, tombstone) round-trip on disk with no plaintext persistence.
- [ ] Validate optimistic concurrency and interrupted catalog writes; provide recovery for committed-but-unlisted records.
- [ ] Confirm origin-local IndexedDB quotas, eviction behavior and secure backup/export strategy; warn users that vault data does not automatically sync to another device.
- [ ] Independent security review of wallet, session lifecycle, KDF, encryption, and browser-origin threats.

## P1: browser delivery
- [ ] Validate PWA install and offline assets on desktop Chrome, Android Chrome, and iOS Safari.
- [ ] Verify correct scoped CSS (`StellarVault.Pwa.styles.css`) and no missing assets/console errors.
- [ ] Remove stale service workers and test upgrade without encrypted data loss.
- [ ] Use HTTPS and pin/ship third-party JavaScript with integrity checks; add CSP and other security headers.
- [ ] Verify build reproducibility, artifact provenance and rollback procedure.
- [ ] Review record/file size limits and memory consumption under representative workloads.

## Publishing
The web host lives at `src/StellarVault.Pwa/`; `Dockerfile` publishes it with the .NET 10 SDK to an Nginx static host. Deployment must remain manual until all P0 gates pass. **This local-only preview is not a wallet-authenticated or on-chain storage release.**
