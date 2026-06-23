# Stellar Vault PWA v1.3.5

This build restores the Android installation panel from the earlier PWA version while keeping the later Android scrolling and iOS viewport fixes.

## Android installation behavior

- On Android Chrome, an **INSTALL ON ANDROID** panel appears shortly after page load, unless the app is already running in standalone mode.
- When Chrome has emitted `beforeinstallprompt`, the **INSTALL** button opens Chrome's native installation dialog directly.
- If Chrome has not emitted the event yet, the panel remains visible. Tapping **INSTALL** reveals the Chrome menu installation path instead of silently doing nothing.
- When the event arrives later, the same button automatically becomes a native install action.
- iPhone and iPad retain the Safari **Share → Add to Home Screen** instructions.

## Important browser limitation

A web page cannot create Chrome's native PWA install event. The app can retain the event and invoke it from the button only after Chrome decides the site is installable.

## Deployment refresh

After deploying:

1. Remove any older installed copy from Android.
2. In Chrome, clear the site's stored data.
3. Reopen the HTTPS URL directly in Chrome.
4. Reload once after the new service worker takes control.


## v1.3.6 — iOS safe-area regression fix

This release is based on v1.3.5 and preserves all earlier features and fixes, including Android scrolling, Android install UI, edit/delete, cost approval modals, custom network discovery, contact information, PWA support, and viewport stabilization.

The iOS platform and standalone classes are now applied in `index.html` before the CSS file loads. A final iOS-only CSS override restores the top safe-area inset for the sticky header, preventing the installed app from rendering beneath the iPhone status/navigation area on first paint. Android layout rules are unchanged.
