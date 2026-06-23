# Stellar Vault PWA v1.3.3

## Android install behavior

- Android no longer shows a manual installation-tip banner.
- The custom **INSTALL** banner appears only after Chrome fires `beforeinstallprompt`.
- Tapping **INSTALL** immediately opens Chrome's native PWA installation dialog.
- The banner remains hidden when the app is already installed or Chrome has not yet made the native prompt available.

## iPhone and iPad

iOS does not provide `beforeinstallprompt`, so the app continues to display manual Safari instructions for **Share → Add to Home Screen**.

## Upgrade notes

After deploying, clear the old PWA/service-worker cache or uninstall and reinstall the existing app so the updated JavaScript is loaded.


## v1.3.4 Android install reliability

- Captures `beforeinstallprompt` in `index.html` before Blazor and the Stellar SDK load.
- Registers the service worker before the WebAssembly application starts.
- Removes the old Android dismissal key that could hide a later native prompt.
- Keeps Android native-install-only behavior; no Android instruction banner is shown.
- Adds `stellarPwaInstallState()` for Android Chrome remote debugging.
- Adds explicit `prefer_related_applications: false` to the manifest.

Chrome may withhold `beforeinstallprompt` until its engagement criteria are met. On a new Android profile, interact with the page and keep it open for at least 30 seconds, then reload once.
