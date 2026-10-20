# Shared Razor UI

This Razor Class Library is the first portable UI project. It references StellarVault.Core and is intended to be consumed by both the Blazor WebAssembly host and MAUI Blazor Hybrid host.

**Status:** active extraction. `CostEstimatePanel` and `CostApprovalModal` have moved from the browser project to this Razor Class Library. The Access, Vault, Add Record pages, MainLayout, and ledger/record modals are now in this library. The browser Router discovers the shared pages via AdditionalAssemblies. Build and native parity testing remain pending. Keep browser-specific session, storage and JavaScript bridge dependencies outside portable components until abstractions are in place.

## PWA v2 session preview

The browser host now registers an isolated `VaultSessionManager` and fail-closed `IVaultAccountAuthorizer`, exposing `/vault-v2-preview`. Both MAUI and PWA render the portable `ManagedVaultSessionStatus` component. The legacy browser `VaultSession` and production routes are unchanged. This is UI/session integration only: browser credential persistence, browser storage providers, external wallet signing, and live ownership verification must be implemented before v2 create/unlock can be activated. Never use an in-memory credential backend as a substitute for durable vault storage or enable a bypass verifier.

## Fresh v2 product direction

There is **no requirement to retain, read, migrate, or preserve compatibility with legacy vault records**. The PWA entry point now renders `FreshVaultHome` directly, without the old HD-registry Router or legacy browser session injection. The old implementation files remain in source temporarily for controlled cleanup; they are not a supported migration path. Do not restore legacy account-chain records in the new database design. The new native and browser interfaces remain authorization-gated until wallet proof and persistent storage are complete.

## Browser persistent storage adapter (fresh v2)

The PWA now loads `wwwroot/js/vault-v2-indexeddb.js` and registers `IndexedDbVaultPassphraseCredentialStore`, `IndexedDbVaultObjectStore`, `IndexedDbVaultRecordIndex`, and `VaultSessionWorkflow`. Three IndexedDB object stores hold opaque wrapped credentials, content-addressed ciphertext and record pointers. Credential creation and pointer updates use IndexedDB transactions for atomic compare-and-swap within one browser database. IndexedDB is origin-local and subject to eviction, user deletion and malicious rollback; it is **not** a cross-device or independently durable backend. No legacy record data is read or migrated.

Vault creation/unlock remain disabled because the browser still registers `DenyVaultAccountAuthorizer`. Browser persistence and crypto interoperability, storage eviction recovery, and external signer proof must be verified before enabling writes.

## Browser lifecycle security

The PWA root now listens for `visibilitychange` (hidden), `pagehide`, and `freeze` events through `vault-v2-lifecycle.js` and requests `VaultSessionManager.Lock()`. Lock invalidates pending asynchronous unlock operations via the session epoch. Events are best-effort: browsers may terminate, suspend, or kill pages without delivering them, and JavaScript-to-.NET callbacks may not finish before the page freezes. Do not use this mechanism alone as a guarantee of key erasure; the WebAssembly runtime and OS still control memory lifetime. The authorization-deny default remains active.
