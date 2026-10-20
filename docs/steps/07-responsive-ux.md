# Step 07 — Adaptive UX and accessibility

**Status:** in progress — first CSS and shared-shell changes committed; visual and device tests pending.

## Principles
- **Phone:** one-column content, bottom vault navigation, at least 44px touch targets, safe-area aware dialogs.
- **Tablet:** readable centered content, fluid grids, no horizontal scrolling or truncated account IDs.
- **Desktop:** retain high-information-density terminal layout and keyboard navigation.
- **Accessibility:** visible keyboard focus, semantic landmarks, reduced-motion support, text zoom and adequate hit areas.
- **Security:** never expose secrets in app switcher previews, debug logs or OS backups; design sensitive reveal/copy actions with explicit user intent.

## Test matrix
| Viewport | Checks |
| --- | --- |
| 320x568 / 375x667 | Header, navigation, forms, modal buttons, long addresses |
| 390x844 / 430x932 | Safe areas, keyboard and bottom nav, scroll |
| 768x1024 / 820x1180 | Portrait tablet grid, centered forms, modal width |
| 1024x768 | Landscape tablet, keyboard navigation |
| 1280x800 and above | Desktop information density, overflow |
| iOS standalone / Android Chrome / MAUI WebView | Insets, keyboard focus, scrolling and touch targets |

## Verification
Run browser screenshot and interaction tests plus physical/simulator checks. No screenshots or device tests have been completed by this milestone.
