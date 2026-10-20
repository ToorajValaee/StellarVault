# Step 05 — MAUI Blazor Hybrid host

**Status:** in progress — native project and adaptive shared-UI preview committed; **not build-verified**.

## Implemented
- MAUI project targeting Android, iOS, Mac Catalyst and Windows (on Windows build hosts).
- Platform entry points and BlazorWebView.
- Native preview renders `StellarVault.UI.Components.AdaptiveShell` and a disabled vault notice.
- Scoped styles and viewport-fit cover for phone/tablet layouts.

## Pending
- First native build, platform packaging, icons and signing.
- Real shared vault pages, session, network settings and encrypted Stellar storage.
- Native secret handling and tested JS/WebView cryptography.
- Browser-to-native recovery and ledger-write approval verification.

## Acceptance criteria
Same shared pages render on native targets and recover existing browser vaults without derivation or format changes. **Not yet satisfied.**
