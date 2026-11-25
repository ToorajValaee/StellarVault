# Native UX review ? 2026-11-17

Source review of the MAUI Blazor Hybrid host and shared native workspace. No installed Android/Windows/iOS runtime review was performed.

## Findings and implemented changes

The existing interface stacks folder shortcuts, collections, search, folders and records. Windows and iOS also share rail rules too early. Disconnected state incorrectly says offline even though connectivity is unknown.

- Windows: system font, compact navigation/rows, wider workspace and side-by-side folder/list panes at desktop widths. Ctrl+1?4 changes destination through existing guarded buttons; Ctrl+F focuses a visible search. No shortcut submits, retries or approves a transaction.
- Android: touch-sized controls and rounded destination targets. Existing system Back and foreground lock integration remains.
- iPhone: bottom tabs through landscape widths with safe-area spacing; wider iPad keeps existing adaptive rail. Apple system fonts.
- Shared native: quick folders collapse by default, tighter page spacing, visible keyboard focus, honest Connected/Disconnected label. Transaction warnings, fees and approval text remain visible.

## Native value already implemented

System file picker, verified file-save integration, encrypted filesystem queue and background credential revocation are concrete native capabilities. They must remain scoped to explicit retention consent and existing account/network policies.

## Remaining UX work

- Replace the large monolithic native workspace with presentation-only destination components while preserving session/quote ownership across navigation.
- Build a proper list/detail desktop workspace and mobile detail destination; currently inspectors can still extend the scroll.
- Add optional-field progressive disclosure to the shared editor through an explicit native parameter, preserving PWA behavior and validation.
- Assess Windows command menus, drag/drop and credential clipboard expiry; none is implemented by this batch.
- Biometric unlock, OS credential autofill and share-sheet intake require platform-specific security design and are not current capabilities.
- Validate hardware keyboard, TalkBack/VoiceOver/Narrator, large text, Android system Back, iOS safe areas and Windows resize at 100?200% scaling on actual hosts.

Builds/tests deferred under current repository policy. CSS and source changes are unvalidated on devices; do not call this native release ready.
