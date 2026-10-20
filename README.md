# StellarVault

Encrypted Stellar-backed vault with a browser/PWA application today and a shared-code, cross-platform architecture in progress.

> **Production status:** The existing Blazor WebAssembly application remains at the repository root. The MAUI migration is in progress on `feature/maui-shared-app`; it is not production-ready and is not merged into `main`.

## Product layout

| Location | Purpose | Status |
| --- | --- | --- |
| `StellarVaultPwa.csproj`, `Pages/`, `Components/`, `Services/`, `wwwroot/` | Existing browser/PWA vault | Existing implementation |
| `website/` | Public static marketing site, independently deployed | Import pending |
| `src/StellarVault.Core/` | Reusable vault data contracts and portable logic | Initial library committed |
| `src/StellarVault.UI/` | Shared Razor screens and components | Planned |
| `src/StellarVault.Web/` | Thin browser host | Planned |
| `src/StellarVault.Maui/` | One native host for Android, iOS, Windows and Mac Catalyst | Planned |
| `docs/steps/` | AI-readable implementation path and acceptance criteria | Active |

**Start here:** [Development map](docs/README.md) → [Step 00: path](docs/steps/00-path.md) → [Step 01: landing](docs/steps/01-landing-website.md).

## Why two hosts?

MAUI Blazor Hybrid runs Razor UI in native applications, not directly as a browser WebAssembly app. Both hosts will reference the **same** Razor Class Library and Core. There will not be separate Android/iOS/desktop UIs.

## Build current browser application

```bash
dotnet restore StellarVaultPwa.csproj
dotnet build StellarVaultPwa.csproj -c Release
node --test tests/*.test.cjs
```

## Vault compatibility

Do not change SEP-0005/legacy derivation, Stellar account allocation, registry encoding, encrypted record format, or folder identifiers as a side effect of project reorganization. Test recovery with synthetic fixtures and fresh environments before releasing.

## Landing website

The landing site is intended for `svault.ir`; the vault application for `app.svault.ir`. The supplied landing package's full HTML, styles, scripts, images and Docker configuration must be imported into `website/` intact. Documentation currently exists, but the actual website files have **not** yet been committed to this branch.

## Previous PWA release notes

#### Stellar Vault PWA v1.3.5

This build restores the Android installation panel from the earlier PWA version while keeping the later Android scrolling and iOS viewport fixes.

### Android installation behavior

- On Android Chrome, an **INSTALL ON ANDROID** panel appears shortly after page load, unless the app is already running in standalone mode.
- When Chrome has emitted `beforeinstallprompt`, the **INSTALL** button opens Chrome's native installation dialog directly.
- If Chrome has not emitted the event yet, the panel remains visible. Tapping **INSTALL** reveals the Chrome menu installation path instead of silently doing nothing.
- When the event arrives later, the same button automatically becomes a native install action.
- iPhone and iPad retain the Safari **Share → Add to Home Screen** instructions.

### Important browser limitation

A web page cannot create Chrome's native PWA install event. The app can retain the event and invoke it from the button only after Chrome decides the site is installable.

### Deployment refresh

After deploying:

1. Remove any older installed copy from Android.
2. In Chrome, clear the site's stored data.
3. Reopen the HTTPS URL directly in Chrome.
4. Reload once after the new service worker takes control.


### v1.3.6 — iOS safe-area regression fix

This release is based on v1.3.5 and preserves all earlier features and fixes, including Android scrolling, Android install UI, edit/delete, cost approval modals, custom network discovery, contact information, PWA support, and viewport stabilization.

The iOS platform and standalone classes are now applied in `index.html` before the CSS file loads. A final iOS-only CSS override restores the top safe-area inset for the sticky header, preventing the installed app from rendering beneath the iPhone status/navigation area on first paint. Android layout rules are unchanged.

