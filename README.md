# Stellar Vault PWA v1.3.2

This update fixes Android scrolling on the access page and makes the Android
installation guidance visible even when Chrome delays or suppresses the native
`beforeinstallprompt` event.

## Android scrolling fix

The iOS visual viewport stabilizer previously ran on every browser. On Android,
focus and visual viewport events could restore the page to an old scroll
position. The workaround is now strictly scoped to iOS. Android uses normal
document scrolling with `pan-y` and pinch zoom enabled.

## Android install guidance

When Chrome fires `beforeinstallprompt`, the banner opens the native install
flow. If that event has not fired after startup, Android users still see a
manual instruction banner:

1. Open Chrome menu (⋮).
2. Choose **Install app** or **Add to Home screen**.
3. Confirm installation.

The development service worker now has a real fetch handler, which makes local
install testing more representative. Published builds still use
`service-worker.published.js`.

## Update steps

```powershell
dotnet clean
Remove-Item -Recurse -Force bin, obj -ErrorAction SilentlyContinue
dotnet restore
dotnet build
dotnet run
```

On Android Chrome, clear the previous PWA cache after deployment:

1. Chrome → Site settings → Storage → Clear.
2. Remove an older installed copy from the Home Screen, if present.
3. Reopen the HTTPS site and wait about two seconds for the install guidance.

For a production check, use the published output rather than only `dotnet run`:

```powershell
dotnet publish -c Release
```
