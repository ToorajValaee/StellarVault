# Shared Razor UI

This Razor Class Library is the first portable UI project. It references StellarVault.Core and is intended to be consumed by both the Blazor WebAssembly host and MAUI Blazor Hybrid host.

**Status:** active extraction. `CostEstimatePanel` and `CostApprovalModal` have moved from the browser project to this Razor Class Library. The Access, Vault, Add Record pages, MainLayout, and ledger/record modals are now in this library. The browser Router discovers the shared pages via AdditionalAssemblies. Build and native parity testing remain pending. Keep browser-specific session, storage and JavaScript bridge dependencies outside portable components until abstractions are in place.

## PWA v2 session preview

The browser host now registers an isolated `VaultSessionManager` and fail-closed `IVaultAccountAuthorizer`, exposing `/vault-v2-preview`. Both MAUI and PWA render the portable `ManagedVaultSessionStatus` component. The legacy browser `VaultSession` and production routes are unchanged. This is UI/session integration only: browser credential persistence, browser storage providers, external wallet signing, and live ownership verification must be implemented before v2 create/unlock can be activated. Never use an in-memory credential backend as a substitute for durable vault storage or enable a bypass verifier.

## Fresh v2 product direction

There is **no requirement to retain, read, migrate, or preserve compatibility with legacy vault records**. The PWA entry point now renders `FreshVaultHome` directly, without the old HD-registry Router or legacy browser session injection. The old implementation files remain in source temporarily for controlled cleanup; they are not a supported migration path. Do not restore legacy account-chain records in the new database design. The new native and browser interfaces remain authorization-gated until wallet proof and persistent storage are complete.
