# StellarVault — modern app design system

Date: 2026-11-11

## Product direction

The previously requested phosphor terminal/CRT visual identity is **superseded** by a premium modern security/productivity app concept. The user's accepted visual references are the desktop PWA concept with sidebar, folder explorer and record-detail inspector, and the mobile concept with top bar, object-storage browsing and persistent bottom navigation.

This is a presentation change only. Shared .NET Core, encryption, native signer, local retention permission, explicit fee approval, Testnet/Mainnet guardrails and the prohibition on Hybrid file operations remain unchanged.

## Implementation

- **PWA:** `src/StellarVault.Pwa/wwwroot/css/modern-app.css` is loaded **last** in `index.html`, after the old stylesheet and scoped Razor CSS. It owns modern neutral/light surfaces, deep green accents, unified rounded geometry, typography, shadows, focus indicators, and responsive layouts. The prior dot-matrix, scanline/noise, hard-edged terminal style is overridden, not allowed to leak into new app screens. Amber theme is now a warm neutral variant.
- **MAUI:** `src/StellarVault.Maui/wwwroot/modern-native.css` is loaded last in the native host's HTML. Android and iOS remain mobile-oriented with safe-area bottom navigation and a separate scrollable task view. Windows and wide tablets keep a sidebar/tree explorer. This remains a MAUI BlazorWebView host with platform-adaptive composition, not platform-native XAML/UIKit widgets.
- **Explorer:** the existing PWA folder hierarchy, nested selection, records and permissions stay intact. Wide browser workspaces show a folder tree plus content cards; narrow widths show two-column file-manager tiles. Record detail opens as a right-hand inspector on wide screens and a mobile sheet on phones, retaining original close behavior, secret masking, copy and transaction actions.
- **Object cards:** the PWA `Vault.razor` now gives folder and secure-item cards distinct affordances and a compact full-text search. Native `NativeVaultWorkspace.razor` already provides separate record/folder/file editor destinations and browsing grids.
- **Workflow safety:** never expose recovery/secret values in default summaries, never use a visual navigation event to submit a transaction, and never convert a blocked Hybrid file operation into an available action.

## Review before preview release

1. Desktop PWA: navigation rail, folders tree, item cards, right inspector, all keyboard and zoom breakpoints.
2. Android PWA: actual installed app tab bar, safe areas, search, folder-grid and record detail scroll/focus.
3. Native Android: first render and native tabs, bottom action bar, separate on-chain/local/hybrid editors, file picker and background lock.
4. Windows MAUI: window resizing, native explorer and keyboard focus.
5. Screen-reader labeling, reduced motion, warning/approval clarity, theme update after SW cache refresh, and user acceptance.

## Rollout

PWA preview artifact is independently generated in `.github/workflows/pwa-preview-release.yml`. The combined pipeline separately compiles Android; Windows preview has its own runner. A green CI build does not verify visual layout or live Testnet interaction. Keep the old CSS assets temporarily to avoid regressions in legacy components while migrating to the final modern design layer; progressively delete legacy rules once visual acceptance is complete.


## 2026-11-11 — Shared shell, entry sizing and control consistency

- `AdaptiveShell.razor` now provides a green shield-like mark, product-case branding and contextual header security indicator, with separate `sv-web-shell` and `sv-native-shell` host classes. These are presentation-only changes.
- `modern-app.css` includes a final scoped, high-specificity control layer for PWA buttons, inputs, editor tabs, dialogs, header controls and focus rings to override remaining terminal-era button definitions (notably /add and /edit).
- The unlock page was compacted: smaller field spacing, reduced showcase height/padding, and fewer stacked margins. **Do not force overflow hidden**: 24-word recovery phrases, mainnet warnings, expanded options, small displays and zoom may legitimately require scrolling.
- `modern-native.css` aligns the shared header with the updated native-host design without changing mnemonic handling or native authorizations.
- `index.html` explicitly versions the modern CSS URL to help distinguish a stale PWA stylesheet when checking screenshots.
- Security behaviors remain unchanged: mnemonic validation, local-storage consent, account selection, network guards, signatures and fee approvals.
- **Not yet validated:** A browser/Android runtime screenshot, dotnet build, accessibility tests, CSS computed-style inspection, and real device scroll height. Do not call this visually verified.
- **Next verification:** Check desktop 1440×900 and 1366×768, tablet 820×1180, phone 390×844, phone landscape, 200% zoom, 12/24-word entry, expanded local-storage settings, Add/Edit, Sync/Drafts, setup, modal approval and native Android.
