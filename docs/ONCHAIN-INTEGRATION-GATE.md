# On-chain storage integration gate

## Goal
A record is considered saved only when its ciphertext and manifest are committed to the selected Stellar network, and can subsequently be reconstructed from ledger state on a clean device. IndexedDB may cache ledger data but must not be the sole source of truth.

## Reusable protocol source
- `docs/legacy-ledger-reference/StellarVaultStore.cs`: original `svh:` manifests, `svc:` 64-byte ManageData chunks, generation and ciphertext hashes, upload/cleanup transaction logic.
- `docs/legacy-ledger-reference/app.js`: original browser SDK signing, session, transaction submission.
- `src/StellarVault.Pwa/wwwroot/js/ledger-compat/`: original HD encryption, bootstrap, sponsorship, allocation and discovery.
- `src/StellarVault.Pwa/wwwroot/js/hd-registry.js`: authenticated HD chain registry.

These files are reference assets and not yet wired to `VaultRecordWorkspace`.

## Non-negotiable integration checks
1. Account identity: SEP-5 only. User mnemonic signs transactions locally, and private key material never reaches Horizon.
2. Network binding: testnet/public/custom Horizon and exact network passphrase for transaction signatures and encryption domain separation.
3. Root funding, signer policy, sponsorship, reserves and fee estimates checked before a ledger mutation.
4. Write staged ciphertext chunks to the correct account, then publish authenticated manifest as the commit marker; report tx hashes and partial-commit recovery.
5. Read ledger entries from Horizon; authenticate ciphertext hashes, generations and encryption binding; reconstruct the original record with no IndexedDB dependencies.
6. Make the shared Core record workspace choose ledger-backed object/pointer services for connected vault sessions, while preserving local-only mode explicitly.
7. Verify create, modify, delete/tombstone, large payloads, multiple child accounts, stale manifests, Horizon outages and cross-device recovery.
8. Do not claim a save succeeded if the ledger did not confirm it. Do not silently substitute local storage on write failure.

## Release smoke test
On private Horizon: create a record, inspect actual ManageData entries and transaction hashes, close PWA, clear IndexedDB on a different browser/device, reopen with mnemonic, discover chain, decrypt the same record, update it, then verify the newer value and deleted state remotely.

Until all checks pass, the current PWA is **local-only for record writes** and cannot be labeled publishable StellarVault.
