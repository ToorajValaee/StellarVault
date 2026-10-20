# Step 05 — MAUI Blazor Hybrid host

**Status:** planned

## Actions
Create `src/StellarVault.Maui/` with one MAUI BlazorWebView host for Android, iOS, Windows and Mac Catalyst. Reference shared UI/Core. Implement native secure storage and JS/WebView bridge only where necessary. Validate SDK/workloads on supported build machines.

## Acceptance criteria
Same shared pages render on native targets; platform integrations are isolated; deterministic derivation and ciphertext match browser behavior.
