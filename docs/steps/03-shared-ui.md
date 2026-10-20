# Step 03 — Shared Razor UI

**Status:** in progress — shared Razor Class Library, portable adaptive shell, and native preview integration committed. Access, Vault, Add Record and MainLayout now reside in the shared library; compilation and device verification pending.

## Implemented
- Android CI initially failed during SDK/workload evaluation: SDK 10 selected `NETSDK1202` for net8 Android and Linux evaluated iOS (`NETSDK1147`). Added a .NET 8 SDK pin and Linux-only Android target evaluation. Native compilation remains unverified until rerun.
- PR #2 commit `1df28c5` passed both Migration build and HD v2 validation workflows, including the full browser build and JavaScript regressions.
- Added an independent `android-preview` CI job to install the Android MAUI workload and compile the disabled native shell; native build outcome pending.
- CI confirmed the scoped CSS exclusion eliminated `BLAZOR102`. Browser compilation then exposed `CS0122` for `RecordManifest` crossing the Core/host boundary. Promoted the record to public without modifying its fields; regression contract added. Browser build rerun pending.
- PR #2 CI confirms the shared Razor UI compiles with 0 warnings and 0 errors, and the JavaScript regression suite passes after the HD allocator fix. Browser build then failed with `BLAZOR102` due to root-project inclusion of nested `.razor.css` files; explicit exclusion committed, pending rerun.
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
