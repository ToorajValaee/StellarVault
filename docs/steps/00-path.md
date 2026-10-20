# Step 00 — Path and decisions

**Status:** implemented (documentation only)

## Objective
One product, one shared UI, one shared Core, two thin hosts. The public marketing site is a separate static deployment.

## Directory contract
- `website/` — landing, privacy, terms, SEO, static assets, Nginx/Docker; public site `svault.ir`.
- `src/StellarVault.Core/` — shared models, validation, platform-neutral vault logic.
- `src/StellarVault.UI/` — Razor Class Library for pages/components/layouts/styles.
- `src/StellarVault.Web/` — thin Blazor WebAssembly host for `app.svault.ir`.
- `src/StellarVault.Maui/` — thin MAUI Blazor Hybrid host for Android, iOS, Windows, macOS.
- `docs/steps/` — implementation and acceptance documents, one per milestone.

## Non-goals
Do not build separate Android/iOS/desktop UIs. Do not replace browser hosting with MAUI: MAUI Blazor Hybrid does not itself execute as a browser WebAssembly app. Do not change ledger formats during the UI refactor.

## Order
1. Document path.
2. Import landing assets intact.
3. Extract Core.
4. Extract UI.
5. Introduce web host.
6. Introduce MAUI host.
7. Cross-platform compatibility tests, then controlled release.

## Definition of done
All steps are documented and linked from the repository README; each subsequent milestone tracks implementation and verification independently.
