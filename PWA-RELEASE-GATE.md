# StellarVault PWA release gate

This repository is **not yet certified for real-secret/mainnet production use**.
A successful compile, publish, or automated test is not a security audit.

## Automated release checks

The `browser-and-core` GitHub Actions job must succeed, including:
- Release build and publish of the PWA and shared libraries
- Existence checks for the deployed `wwwroot` entrypoint, WebAssembly runtime,
  service worker and asset manifest, local encryption modules, and ledger adapter
- JavaScript syntax validation of the published app scripts
- Browser checkpoint/recent-vault tests and core cryptography regression tests

Deploy **the uploaded `stellarvault-pwa-release` artifact's contents**, not
the project source tree. Host on HTTPS with history fallback to `index.html`;
serve WebAssembly with the correct MIME type. Ensure the service worker and
its asset manifest are accessible at the app origin.

## Required manual release checks — blocking

Use test credentials and a dedicated Stellar Testnet account.

- [ ] Fresh install and reload on Chrome, Firefox, Safari, and mobile browsers.
- [ ] Create local vault, lock, unlock and cold-start reopen with the correct passphrase.
- [ ] Wrong passphrase fails without deleting or replacing existing encrypted records.
- [ ] Create, read, update, delete encrypted text records; verify after refresh.
- [ ] Save, reopen and export an encrypted file; compare exported bytes to original.
- [ ] Test file-size rejection, record conflicts, quota/storage errors and IndexedDB denial.
- [ ] Confirm local-only saving does not create Stellar transactions.
- [ ] Confirm privacy lock hides decrypted content and mnemonic is cleared after use.
- [ ] Connect funded Testnet root, initialize registry where necessary, read records.
- [ ] Publish record on Testnet; verify explicit cost approval, transaction receipt,
      on-chain visibility, and no duplicate submission on network uncertainty.
- [ ] Upload multipart file, interrupt after a stage, refresh and reconcile,
      resume with same file, verify SHA-256 and final downloaded bytes.
- [ ] Repeat uncertain transaction/result and fee-change scenarios without automatic retry.
- [ ] Upgrade from previous PWA release while offline/online; verify old vault remains accessible.
- [ ] Verify offline behavior is explicit and **cannot** represent stale on-chain data as current.
- [ ] Test network switching, custom network validation, and correct account namespaces.
- [ ] Independently review cryptographic design, browser key handling, XSS/CSP,
      third-party SDK, dependency supply chain and client-side storage threat model.
- [ ] Validate backup/recovery expectations and permanent data-loss messaging with real users.
- [ ] Verify hosting headers, HTTPS, privacy policy, incident response and rollback procedure.

## Shipping policy

Release initially as **Testnet/early access only** until all release blockers
above have been evidenced and reviewed. Never claim production-grade security
for personal secrets based only on local tests or CI success.

Do not merge to `main` or deploy to a public production domain solely
because this checklist has been added.
