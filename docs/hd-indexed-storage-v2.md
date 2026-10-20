# SVault v2 — HD-indexed storage migration

Status: design specification, **not yet implemented**. This branch isolates the breaking migration from `main`.

## Decisions

- New vaults use a BIP-39 mnemonic; derive Stellar Ed25519 accounts using SLIP-0010 and the documented hardened BIP-44 Stellar path `m/44'/148'/i'`.
- Reserve index 0 for vault control/registry and indexes 1+ for storage nodes. Never mix these paths with ordinary wallet payment accounts. Pin derivation vectors and library versions before mainnet deployment.
- Derive an independent AES-256-GCM key using domain-separated HKDF input from mnemonic seed, bound to network passphrase and vault identity. Do not use a Stellar account signing key as an encryption key.
- Eliminate random child keys, delegated root signers, and the `sv:next` chain in v2. Each derived storage account signs its own ManageData transactions.
- Maintain a compact authenticated allocation registry (version, highest allocated index, active index, generation). Encode into <=64-byte ManageData values; use generation/manifest and integrity checks. A root account can publish registry chunks.
- The registry is a *hint*, not the sole source of truth. Recovery must scan with a documented bounded gap policy and support manual extended scanning; never silently treat the first missing account as the end of the vault.
- Keep 64-byte encrypted chunks and commit manifest last. Authenticate record identity, generation, network and node index in encryption AAD.
- Use explicit state transitions for account allocation: derived -> funded -> initialized -> data committed -> registry committed. Recovery must reconcile every interrupted state.
- Ensure concurrent writers cannot silently overwrite registry/record state. Detect conflicts and retry/reconcile; consider an external coordination mechanism or strict single-writer policy.
- Check native reserves and fees before funding; do not fund speculative indexes. Allow recycling free capacity only after verified cleanup.
- Encrypt mnemonic locally at rest with a memory-hard password KDF; never log mnemonic, raw seed, decrypted secrets or signed payloads containing sensitive metadata.
- Treat deleting ManageData as current-state deletion, **not** erasure of ledger history.

## Required implementation sequence

1. Add a vetted BIP-39 + SLIP-0010 derivation library and reproducible test vectors.
2. Introduce v2 vault creation/unlock and locally encrypted mnemonic storage, replacing the root-secret prompt.
3. Implement indexed node discovery and authenticated registry serialization.
4. Replace `createAndAppendChild` and `loadChain` in `wwwroot/js/app.js` with deterministic account allocation and discovery.
5. Update `Services/StellarVaultStore.cs` to use node index and independent signing per node.
6. Add recovery, crash-injection, two-device concurrency, insufficient-reserve, and corrupted-registry tests.
7. Security audit and production deployment gate. Existing v1 vaults are deliberately unsupported.

## Current implementation notes

The main branch currently calls `stellarVault.stellar.createAndAppendChild`, which generates `StellarSdk.Keypair.random()` and delegates signing to the root. `Services/StellarVaultStore.cs` uses `sv:next` and checks `NodeDataEntryLimit`. Those paths must be replaced before claiming v2 works.
