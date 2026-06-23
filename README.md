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
