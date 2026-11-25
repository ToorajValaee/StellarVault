# Vault data-key wrapping

`VaultKeyEnvelope.GenerateDataKey()` creates a random 32-byte AES-256 vault data key. `Wrap(dataKey, wrappingKey, stellarAccountId)` encrypts it using AES-256-GCM, a fresh 96-bit nonce, and authenticated account binding. `Unwrap(envelope, wrappingKey, stellarAccountId)` returns the original key only after authentication. Format is SVKW magic, version 1, nonce, 32-byte encrypted key and 16-byte tag.

The wrapping key **must** be 32 cryptographically random or independently derived bytes from an approved key derivation and key management workflow. Never pass a raw passphrase, Stellar seed, or Stellar secret key as a wrapping key. Password derivation is implemented separately by VaultPassphraseKdf using Argon2id and bounded, serialized parameters; VaultPassphraseLifecycle composes it with key wrapping.

Store the envelope as confidential application metadata; an envelope alone is not an account-ownership proof. The core validates canonical Stellar G-address StrKey CRC16 checksums. The application must verify Stellar account ownership and authorization separately. Clear temporary key buffers after use; managed runtime copies and GC mean zeroization is best-effort. Key rotation can rewrap the same data key without rewriting every encrypted chunk. Do not consider the overall unlock flow production ready until the KDF, secure key lifecycle, and account authorization are integrated and validated.

## Persistent lifecycle

`LocalStellarVault.Keys` exposes `CreateAsync(account, wrappingKey)`, `UnlockAsync(account, wrappingKey)`, and `RewrapAsync(account, oldWrappingKey, newWrappingKey)`. The filesystem envelope store writes an account-address-hashed filename inside the private `keys/` directory. Provisioning uses create-if-absent; rotation uses compare-and-swap against the exact prior envelope. Updates use temporary files, atomic replacement on supported local filesystems, and a cooperating-process lock. They do not guarantee power-loss durability or prevent malicious disk rollback. The returned 32-byte plaintext data key is owned by the caller and must be zeroed after use. **No password-to-key derivation is provided here.** Do not pass user passphrases directly as wrapping keys.

## Unlocked sessions

`LocalStellarVault.CreateSessionAsync(accountId, wrappingKey)` and `UnlockSessionAsync(accountId, wrappingKey)` return `VaultUnlockedSession`. Call `ExecuteAsync(async (db, key, ct) => ...)` to run account database operations with a short-lived key copy. The session serializes operations and zeroes temporary key copies on completion. Dispose the session after use to zero its owned data key. Do not call `Dispose` from inside an `ExecuteAsync` callback, as disposal waits for in-flight operations; instead dispose from the owning scope. Never cache, capture, or return the provided key memory. .NET cannot guarantee that all managed/runtime copies are cleared.

Session lifecycle is wired through VaultSessionManager and host privacy/lifecycle components. Native device acceptance is outside the current web deployment scope. The local session does not perform Stellar ownership proof. A 32-byte wrapping key must be supplied by a secure KDF or platform keystore; passing a raw passphrase remains prohibited.

## Application session ownership

`VaultSessionManager` owns one active `VaultUnlockedSession` and exposes `Activate(session)`, `ExecuteAsync(operation)`, `Lock()`, `IsUnlocked`, `ActiveAccountId`, and `StateChanged`. The manager disposes the previous session when switching accounts and clears the active session on lock. UI components can subscribe to `StateChanged` and must unsubscribe on disposal. The caller must wire mobile backgrounding, app inactivity, explicit lock, and account-switch events. Do not call synchronous `Lock` from inside an active `ExecuteAsync` callback, because disposal waits for in-flight operations. This is application state management, not account authentication or an Argon2id KDF.
