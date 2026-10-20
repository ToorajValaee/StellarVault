# Step 02 — Shared vault Core

**Status:** in progress — `src/StellarVault.Core/StellarVault.Core.csproj` created and linked to the existing models and error-message source. Browser host references Core. CI added; green build not yet confirmed.

## Next actions
- Identify platform-neutral services and JS interop boundaries.
- Move services behind stable interfaces with browser/native implementations where needed.
- Preserve record JSON shape, folder IDs, ledger registry and encryption format.
- Add regression tests using **synthetic** vectors for SEP-0005 and legacy derivation.

## Acceptance criteria
Existing browser app builds and reads/writes the same existing vaults. No duplicate type definitions. No private keys or recovery phrases logged.
