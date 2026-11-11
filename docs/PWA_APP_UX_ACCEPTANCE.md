# StellarVault PWA — first customer-facing app UX

Updated: 2026-11-11. PWA is the primary preview surface while MAUI native startup is being diagnosed. It remains **early access**, not independently security-certified production software.

## Product experience

The unlocked `/vault` route is a task-based application shell, not one continuously scrolling form. It retains the familiar StellarVault terminal identity (dark vault surfaces, phosphor accents, technical status and audit language) with contemporary app navigation.

- **Records** is the default screen. It keeps the original on-chain record/folder browser, breadcrumb navigation, search, record detail, account information and direct on-chain file actions. A missing signer session is explicitly labeled **NOT SIGNED IN**, never mistaken for an internet outage.
- **Offline drafts** is visible only for unlocked accounts that explicitly opted into local encrypted persistence. Editing is independent from network publication. No device-to-device synchronization is implied.
- **Hybrid Sync** is visible only with the same retention/unlock prerequisites. Within Sync, **Review & Sync** is separate from **Stage remote changes**. Existing fee approval, per-item state, conflicts, unknown transaction hashes and read-only recovery controls remain in place; nothing automatically broadcasts.
- **New item** is a focused editor route with Record / Folder / File choices. Files are *direct on-chain* only; the rejected Hybrid local-file-draft action is no longer offered.
- The header retains theme and network behavior. The desktop gets a sidebar with safe shortcuts; tablet/phone and installed PWA get accessible persistent bottom tabs, a scrollable content region, safe-area inset spacing and 44px+ targets.

This is a PWA-only presentation restructuring; Core and MAUI business logic are not forked.

## Manual acceptance before distribution

- [ ] Initial access, account/network setup and passphrase consent display without horizontal overflow or hidden actions
- [ ] Locked and unlocked routes direct users to the correct screen; background privacy handling and explicit Lock still work
- [ ] Mobile bottom tabs remain visible while scrolling long records; browser viewport and virtual keyboard do not obscure editor fields
- [ ] Desktop navigation rail remains usable at 100–200% browser zoom and window resize
- [ ] Records, nested folders, search, new record, edit, direct on-chain file and fee approval behave as before
- [ ] Offline record and folder drafts remain private and editable after reload, with correct current unlocked account/network
- [ ] Sync staging/review are separately reachable; uncertain submissions show warnings and cannot be automatically retried
- [ ] Mainnet confirmations and transaction-cost ceilings still require explicit individual approval
- [ ] Service worker update deploy is atomic and the installed PWA gets the latest hashed bundle after an update
- [ ] Keyboard focus order, screen reader navigation labels, Escape/close modal behavior and reduced-motion preferences work on touch and desktop
- [ ] Testnet end-to-end publish, reload, historical reconciliation and collision recovery pass on at least one real browser/device

## Automated coverage

`tests/pwa-release-smoke.test.cjs` checks the route, distinct task destinations, on-chain browser retention and editor navigation. Build/asset/cryptographic checks run in `.github/workflows/fresh-v2-build.yml`. **Static tests and CI cannot replace browser interaction and security testing.**


## 2026-11-11 — Responsive explorer and CRT visual acceptance

- [ ] All PWA panels, folder/record cards, field groupings and dialogs use consistent squared pixel corners and dim dot-matrix surfaces in green and amber themes; warning/error alerts remain clearly distinct.
- [ ] On a wide Windows/browser workspace, the folder tree and content list remain simultaneously visible, preserve nested hierarchy, and show the active folder.
- [ ] Narrow the browser or install on mobile: tree becomes familiar folder/object tiles without horizontal overflow, with breadcrumb and New controls reachable.
- [ ] Open/rename/move/delete nested folders and verify records remain scoped correctly; keyboard navigation and cycle-guarded tree remain usable.
- [ ] Switching Records/Drafts/Sync keeps pending fee approvals and encrypted state safe; no transaction is submitted by switching screens.
