# Step 03 — Shared Razor UI

**Status:** in progress — shared Razor Class Library, portable adaptive shell, and native preview integration committed. Existing vault pages/layouts have **not** been extracted; compilation and device verification pending.

## Implemented
- `StellarVault.UI` Razor Class Library referenced by existing WebAssembly and MAUI hosts.
- `AdaptiveShell.razor` and isolated responsive CSS for phone/tablet/desktop widths.
- Native preview uses the same shared component.
- Existing browser styles improved for touch controls, narrow screens, long content and reduced motion.

## Next
1. Confirm browser and MAUI builds with correct .NET workloads.
2. Extract browser-neutral UI components from the existing vault screens.
3. Introduce portable session/storage interfaces so the shared UI does not depend directly on `BrowserStorage` or JavaScript.
4. Extract pages/layouts and configure router assembly discovery in both hosts.
5. Verify the same flows across browsers and native devices.

## Acceptance criteria
Same functional vault pages in browser and MAUI, with equivalent cost approval and folder behavior; responsive layout tests pass on small phones, tablets, desktop and landscape. **Not yet met.**
