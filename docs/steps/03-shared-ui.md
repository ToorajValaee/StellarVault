# Step 03 — Shared Razor UI

**Status:** in progress — a Razor Class Library has been committed and referenced by the existing browser host. Full UI extraction and compilation verification are pending.

## Implemented
- `src/StellarVault.UI/StellarVault.UI.csproj` references shared Core.
- `_Imports.razor` and `Components/PlatformBanner.razor` provide the initial portable component.
- Existing `StellarVaultPwa.csproj` references shared UI and excludes nested Razor sources from the host compilation.

## Next actions
1. Confirm browser and shared library build in CI.
2. Extract portable UI components one at a time, replacing direct browser services with portable interfaces.
3. Move layouts and pages only when route assembly discovery is configured.
4. Add MAUI BlazorWebView host after services are portable.
5. Run browser/native parity tests before moving existing root project.

## Acceptance criteria
Browser UI matches existing application; folder breadcrumbs, scoped record search, cost approvals, lock/unlock and recovery work; no duplicate Razor screens in hosts. **Not yet verified.**
