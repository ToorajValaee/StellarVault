# Step 03 — Shared Razor UI

**Status:** in progress — shared Razor Class Library, portable adaptive shell, and native preview integration committed. Access, Vault, Add Record and MainLayout now reside in the shared library; compilation and device verification pending.

## Implemented
- CI on draft PR #2 confirmed shared UI builds standalone; full browser build failed with `NETSDK1082` because of the ASP.NET Core framework reference. Switched the RCL to `Microsoft.AspNetCore.Components.Web` package; awaiting CI recheck.
- HD allocator CI revealed an invalid `rootKeypair.rawPublicKey()` call; corrected registry payload handling and added regression assertions. Awaiting CI recheck.
- Added source-layout regression assertions for shared pages, record details and ledger approval components; CI now separately compiles the shared Razor UI before the browser host.
- Extracted `Index`, `Vault`, `Add`, `MainLayout` and `RecordDetailModal` into the Razor Class Library; browser Router includes the shared assembly. Removed original browser copies, and updated regression test paths.
- `CostEstimatePanel` and `CostApprovalModal` were physically extracted to `src/StellarVault.UI/Components`; browser-only originals removed and web imports updated. Native host can reference them once real vault service integration is ready.
- All three main vault pages and the approval modal now inject `IVaultClientServices` instead of concrete `BrowserStorage`; `BrowserStorage` implements the new composite contract and web DI registers it.
- Node regression test `tests/migration-contracts.test.cjs` checks interface injection and prevents native preview accidentally exposing active vault operations.
- Access, Vault, Add Record and Main Layout now inject `IVaultOperations`; Main Layout also injects `IVaultPreferences`. The browser host provides these interfaces via its existing services. This is dependency decoupling, not yet moving the pages into the shared RCL.
- `IVaultOperations` now defines the existing vault read/write, cost-estimation, registry and lock/unlock signatures. Browser `VaultSession` implements it and DI resolves the same scoped instance. No encryption or derivation changes.
- Portable interfaces for non-secret preferences, platform actions and read-only session status in `StellarVault.Core.Abstractions`.
- Existing browser `BrowserStorage` and `VaultSession` implement these interfaces, registered in browser DI without changing crypto behavior.
- Shared `SecurityStatus` component rendered by the MAUI preview.
- `StellarVault.UI` Razor Class Library referenced by existing WebAssembly and MAUI hosts.
- `AdaptiveShell.razor` and isolated responsive CSS for phone/tablet/desktop widths.
- Native preview uses the same shared component.
- Existing browser styles improved for touch controls, narrow screens, long content and reduced motion.

## Next
1. Confirm browser and MAUI builds with correct .NET workloads.
2. Extract browser-neutral UI components from the existing vault screens.
3. Expand the new portable interfaces to cover real vault operations, then migrate UI injection from concrete browser services.
4. Extract pages/layouts and configure router assembly discovery in both hosts.
5. Verify the same flows across browsers and native devices.

## Acceptance criteria
Same functional vault pages in browser and MAUI, with equivalent cost approval and folder behavior; responsive layout tests pass on small phones, tablets, desktop and landscape. **Not yet met.**
