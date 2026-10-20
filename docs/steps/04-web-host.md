# Step 04 — Thin WebAssembly host

**Status:** planned

## Actions
Create `src/StellarVault.Web/` and reference shared UI/Core. Move browser-only bootstrap, PWA manifest/service worker, browser storage and JS interop here. Keep current root `StellarVaultPwa.csproj` working until parity tests pass.

## Acceptance criteria
Browser build and PWA behavior work, existing vaults recover unchanged, HTTPS/manifest/service worker remain valid.
