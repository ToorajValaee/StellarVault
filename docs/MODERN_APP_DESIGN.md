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


## 2026-11-11 — Inspector, design controls and stylesheet consolidation

- Record detail uses a desktop sidecar with a drag handle (pointer devices) and left/right arrow-key sizing at 24px increments. The width is bounded between 320px and 720px (and 58% of viewport width). It does **not** save secret data or width preferences to device storage.
- When the record inspector opens on a wide workspace, the folder tree is hidden and the remaining file list uses compact full-width rows. The inspector is visually separate rather than a dimming modal backdrop. Phone/tablet still present a sheet and omit the resize handle.
- Network chooser controls have a three-column button layout; the Horizon URL and Detect Network action are separated with explicit spacing. Checkbox/radio geometry, status alerts, detail fields and approvals use the green/neutral product design.
- Obsolete `src/StellarVault.Pwa/wwwroot/css/app.css` and `src/StellarVault.Maui/wwwroot/native-app.css` are deleted; both hosts load `modern-app.css`, and MAUI additionally loads `modern-native.css`. Packaging, CI paths and smoke-test references were updated.
- **Migration detail:** required structural/layout rules from the deleted files were moved into the beginning of the corresponding modern stylesheets. This avoids breaking unrelated legacy components but means terminal-era selectors remain in consolidated source until a follow-up selector-by-selector cleanup. Appearance overrides are loaded later and own the visible modern theme. Do not claim that legacy selector migration is complete.
- **Static review only:** verified stylesheet references, MAUI packaging paths, inspector markup, keyboard/pointer code, network and checkbox selector presence, and test-path references. This does not prove computed CSS, browser drag mechanics, scroll sizing, focus traps, or MAUI layout correctness.
- **Still needed before UI acceptance:** actual builds, PWA screenshots at 1440/1366/1024/390px, Android screenshots, screen-reader review, network popup overlap test, record inspector min/max resize and focus behavior, Sync/Drafts/Settings/editors and all danger/approval dialogs.


## 2026-11-11 — Screenshot-based refinement and removal of migrated terminal visuals

Based on actual user screenshots:
- **Large empty strip** between explorer and inspector: fixed the double-width reservation. The `sv-pwa-app` workspace now reduces its total width once (by `--sv-record-inspector-width`), while its stage has zero inspector-specific margin. This leaves room for real full-width list rows.
- **Entry scrolling (12 words)**: desktop mnemonic now uses four columns, smaller field and form spacing, and less decorative vertical padding. Longer 15/18/21/24-word phrases and small/zoomed screens may still need scrolling, intentionally.
- **Old account modal/consent styling**: unified typography on inherited Razor components; redesigned stats, fields, radio cards, selected choices and warning notices. Kept account identifiers in a readable modern monospace font but removed legacy embossing/shadows.
- **Dark sidebar legibility**: added explicit rail background, navigation text and icon color at host-specific specificity.
- **Remove visual legacy CSS instead of stacking it**: removed old color, CRT grid, shadow, font, border and related visual declarations from migrated pre-modern PWA and native foundation sections. Kept structural layout properties for compatibility with existing components. Original `app.css` and `native-app.css` files remain deleted.
- Source checks: brace balance 0 in both stylesheet files, new links/MAUI assets confirmed, dark terminal root colors absent. Added snapshot-regression source assertions. **No runtime build or browser screenshot verification has been executed**.
- Remaining risk: CSS cascade in shared Razor-scoped and native screens must be tested in a running host; source assertions cannot establish visual acceptance. Also evaluate named `terminal-*` classes for future semantic renaming, while preserving workflows.


## 2026-11-11 — Approved desktop reference, structural implementation

This iteration follows the user's desktop reference image:
- The Vault is now a **real three-column desktop workspace**: navigation rail, folder tree + explorer, and a right inspector. The inspector is a grid track rather than a fixed overlay or width subtraction; the existing pointer/keyboard resize handle changes its track width.
- The folder tree uses parent depth, independent expand/collapse controls and selected-folder styling. It is based on existing folder IDs and ancestry, not a mock tree.
- The explorer shows breadcrumbs, the active folder title and item count, list/grid controls, and file-manager Name/Type/Storage columns. Storage replaces the mockup's Updated column because a trustworthy updated timestamp is not currently exposed for every record. Existing three-dot menus remain available for folders, records, files and drafts.
- New/edit record and folder, and direct on-chain file upload now open **inside the right pane on desktop**. The standalone `/add` and `/edit/{id}` routes remain for deep links and fallback use. The same Add component and existing transaction review/fee approval logic serve both destinations — no duplicate on-chain implementation.
- `AdaptiveShell.razor` has an `Embedded` option so the nested editor does not render a second app header or main landmark. The Add component blocks dismissals/mode changes while a transaction approval is active, a submission is underway, or an upload stage is uncertain.
- Record preview now has Details and Activity tabs plus a top-right action menu and close button. The Activity tab exposes record identity, folder, kind and storage; it explicitly does **not** claim a history not provided by the current ledger reader. URL opening is restricted to valid http/https destinations.
- Sidebar branding and the Stellar network connection section align more closely with the reference; controls use tighter 8–9px corners.

**Verification performed:** code-level inspection of markup balance and CSS brace balance; checked callbacks, command methods, select/expand state, grid rules, update paths, and new smoke-test assertions. **Not performed:** .NET build, actual rendered PWA/MAUI screenshots, keyboard/focus acceptance, or real network transaction tests. Visual fidelity and compiler correctness remain unverified until CI/device test.


## 2026-11-11 — Routed editor separation and remembered network

- The previous inline Add/Edit approach mounted the routed `Add.razor` page as a child; the user reported that the right pane remained empty. The actual ledger create/edit/upload implementation has now been moved into route-free `VaultItemEditor.razor`. `Add.razor` is a thin `/add` and `/edit/{RecordId}` wrapper, and the Vault inspector mounts the shared editor with `ErrorBoundary` and an explicit standalone fallback. Ledger fee approval, upload recovery and local-draft logic remain in one implementation.
- `stellarVaultNetworkPreference` stores only the selected preset and a validated custom Horizon origin URL in browser/WebView local storage. No mnemonic, account ID, network passphrase, encrypted key, or signing material is persisted by this feature.
- Both the PWA unlock screen and native WebView restore a remembered preference when no active signing session is present. A custom URL must be probed again for its network passphrase before connecting. Mainnet warnings and consent remain unaffected.
- The early-access warning is still correct: `PWA-RELEASE-GATE.md` marks audit and manual release validation as incomplete. Do not remove it without security release evidence.
- **Validation remaining:** compile of shared Razor components, runtime Add/Edit pane rendering, actual network-preference restart behavior, browser storage denial, native Android WebView persistence, and CI tests. Static checks and changed tests do not substitute for these.


## 2026-11-11 — Automatic verification of saved custom network

A remembered custom Horizon URL is the **selected preference**, not a verified signing network. The saved preference intentionally omits the network passphrase, so the PWA and native host now automatically probe the remembered endpoint after startup. When the probe returns a valid network passphrase, the current session's network identity is configured and unlock can proceed under existing mnemonic, local-storage and mainnet guards. If the probe fails, the custom selection and URL remain visible but the UI explicitly asks the user to retry Detect network. No stored or stale passphrase is trusted, and no signing starts during automatic detection. Runtime confirmation of this flow is still outstanding.


## 2026-11-11 — Modal safety, help, transparency, and theme

- **Signed ledger approval and account information are modal**, unlike the ordinary record inspector. Their backdrop now covers the viewport with a dark translucent blur and a higher stacking level than explorer row menus. The embedded editor sidecar is elevated while a cost approval is present so its dialog does not appear underneath records.
- PWA dialog lifecycle guidance uses focus containment, background `inert`, focus restoration, and Tab cycling while the modal exists. Blurred visuals are not considered a substitute for preventing background interaction. Busy transaction approval still cannot be dismissed.
- Modal and inspector close buttons use borderless 36px icon targets with visible keyboard focus rings.
- Product branding now uses a scalable shield and a consistent network symbol rather than generic star/target glyphs.
- The header color toggle now selects a real light/dark theme and persists its preference. The prior amber/green CSS-variable-only toggle was effectively hidden by hard-coded modern surfaces. Native WebView initializes its theme bridge before optional ledger runtime scripts load. An old stored `amber` value migrates to `dark`.
- Added `/help` as a dedicated, public documentation route covering onboarding, records, local consent/sync, Stellar reserves/fees, security and repository transparency; desktop sidebar and mobile vault navigation expose the link.
- Added a direct `https://github.com/ToorajValaee/StellarVault` source link and a `Powered by badje.ir` attribution badge in the sidebar. The help page explicitly avoids claiming that open source code alone guarantees security. The existing independent security review warning remains appropriate.

**Status:** Committed and checked structurally, but **not browser-rendered or device-tested in this tool session**. Important acceptance checks: pending ledger approval on desktop while a record menu is open, account modal background blur, Tab focus loop and restored focus, approval Busy blocking, light/dark mode across Vault/entry/help/native, external-link destinations, narrow mobile help layout.


## 2026-11-11 — Integrated Help navigation and restrained footer

- The primary sidebar's Vault item now uses a compact line-art vault lock, rather than a chess-pawn glyph. The product shield brand stays distinct.
- Documentation & Help uses `SelectAppView("help")` just like the other workspace views, rendering the new `VaultHelpContent.razor` inside the same content stage and shared sidebar. Mobile Help also switches content in place. The duplicated dedicated Help sidebar and its redundant app links have been removed from the rendered help content.
- Legacy `/help` deep links redirect to `/vault?view=help`, so direct links still work for unlocked users. A locked user must first unlock to access the Vault workspace.
- Source transparency via GitHub remains in the left navigation. `Powered by badje.ir` is now a small text-only attribution beneath the sidebar connection status, without a large badge, border, or promotion block.
- Static checks required: verify no second Help sidebar, active Help state in desktop/mobile navigation, no accidental create/actions when Help is active, and footer contrast at narrow heights. Actual screenshot/build validation is outstanding.


## 2026-11-11 — Unified Inspector / Settings / Drafts / Sync theme

- Removed the redundant `Folders` action from the primary left rail. Folder navigation remains available through the actual nested explorer tree and breadcrumbs.
- Removed the old `MODE / ENCRYPT → CHUNK → SIGN → SUBMIT / TARGET / STELLAR LEDGER` readout from the shared Add/Edit record and folder editor. The real signed-operation fee review remains in its dedicated approval dialog.
- Scoped Settings, Drafts and Sync into `sv-workspace-surface` panels. Shared draft, sync review, and staged remote mutation components use `sv-hybrid-panel` classes and modern headings/labels without modifying ledger submission, mutation state transitions, or consent.
- A single pair of work-surface palettes drives the PWA light/dark surfaces, text, inputs, tabs, controls, cards and warning boundaries. Added high-specificity dark selectors for record detail's third-column inspector and its tabs, menus, notes, fields and activity metadata, to override older hard-coded light declarations.
- Added source-based regression assertions and bumped the CSS resource version.
- **Verification limitation:** static Razor tag balance and stylesheet brace balance passed. Not yet run: .NET build, functional PWA theme switching, screenshots on desktop and mobile, accessibility contrast tests, end-to-end draft synchronization, or real Stellar transaction signing. Those remain essential acceptance criteria.
