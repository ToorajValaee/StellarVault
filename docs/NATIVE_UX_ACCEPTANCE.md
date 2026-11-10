# StellarVault native UX — platform acceptance

Updated 2026-11-10. This is a **MAUI presentation layer** over shared Core and Razor workflows, not a claim that the controls are native WinUI/UIKit/Jetpack Compose widgets.

## Design intent
The installed app should never look like a vertically concatenated website. It presents a stable application frame with three primary destinations: **Stellar** (existing on-chain records/files), **My vault** (encrypted device data and offline drafts), and **Sync** (staged remote changes and individually approved transactions). Native-only CSS is loaded after the common PWA styles. No business logic is copied into host views.

### Android phones
- Top app title and current connection state, with secure-looking modern cards and sensible spacing
- A **real independently scrollable content viewport** between the header, sticky quick-action strip, and persistent bottom navigation
- 44–48 px or larger touch targets; 16 px text inputs to reduce WebView zoom
- Keyboard-open long forms must scroll to their active field; the Android activity explicitly requests `SoftInput.AdjustResize` so the WebView gets a resized viewport
- Unlock and connect are available as bottom shortcuts, but disabled until explicit prerequisites exist
- Quick actions never sign/publish without the existing transaction approval flow
- Android system Back returns from My vault / Sync to Stellar; it never approves a transaction
- Account switch and app background events clear sensitive state; failed signer disconnect remains locked
- Landscape and 1024px+ tablets use the available width and a left navigation rail

### iPhone and iPad
- Home-indicator and notch safe areas preserved by viewport-fit and CSS env safe-area offsets
- iPhone keeps tabs + quick actions pinned outside the scrollable content; iPad at 720px+ uses a navigation sidebar
- Dynamic text/VoiceOver focus visibility, keyboard-safe editing, focus return on view changes require on-device inspection
- No automated iOS build or physical-device validation has been claimed by Android CI

### Windows
- The app opens in a resizable ~1180×800 desktop-sized window with 720×540 minimum
- The Windows-only CI pipeline publishes an unpackaged preview folder and archives it for desktop visual testing; distribution signing is separate
- A left navigation rail, full-height independently scrollable content, desktop card density and bottom command strip
- Keyboard tab sequence, scrolling wheel, resizing, scaling at 125–200%, and screen reader semantics must be tested on a Windows build
- No WinUI-native controls or Windows WebView2 runtime behavior can be certified by Linux Android CI

## Task-focused disclosure
- My vault: switch between **Device records** and **Offline drafts**; show one editor at a time
- Sync: switch between **Stage changes** and **Review & sync**; approval stays isolated in the original shared component
- On-chain Stellar storage retains its existing browse/record/file subviews
- Hybrid file creation, deletion and syncing remain disabled; direct on-chain file feature is unchanged

## Acceptance checklist (manual)
- [ ] First launch renders navigation and a visible page; never hangs indefinitely on the initializer
- [ ] Android and iOS: scroll long content from top to bottom without moving/losing tabs and quick actions
- [ ] Keyboard open: title, mnemonic, notes and parent folder fields remain focusable and reachable
- [ ] Local vault can be created, unlocked, locked, closed and reopened without clearing approved encrypted device data
- [ ] Switching between destinations doesn't submit any transaction or bypass ledger/queue ownership gates
- [ ] Switch between record/draft and staging/review segments; only one task editor visible
- [ ] Foreground loss locks both local data key and WebView signer
- [ ] Validate safe-area padding on cutout/gesture-navigation devices
- [ ] Android system Back returns to Stellar without dropping the app or changing queue/submission state
- [ ] Windows: window resizing and DPI scaling retain sidebar/scroll access
- [ ] Testnet manual review, fee approvals, interrupts and historical verification still behave as before
- [ ] Accessibility: focus outlines, contrast and screen reader labeling verified on each platform

## Automated gates
The existing PWA/Core/Android MAUI GitHub workflow builds shared C# and Android APK. `tests/maui-ledger-assets.test.cjs` guards root wiring, host asset list, native CSS ordering, scroll layout, navigation and sticky command strip. **These are static/build checks, not screenshots or input-event/end-to-end validation.**
