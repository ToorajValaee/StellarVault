# Step 06 — Compatibility and release

**Status:** planned

## Matrix
- Existing vault: unlock, refresh, read, write, update, delete.
- SEP-0005 and legacy BIP32-compatible accounts.
- Nested folders, parent breadcrumbs, current-folder filtering, opt-in descendants.
- Registry revisions, stale writes, sponsored reserve and fee approval.
- Backup/recovery across fresh browser profile and native installation.
- Browser/PWA, Android, iOS, Windows, Mac Catalyst.

## Release gates
Successful CI, platform builds, synthetic derivation vectors, cross-device recovery tests, and manual production-like staging approval. Merge only after gates pass. Never perform an automatic ledger migration.
