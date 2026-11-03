# AI coding guidance — StellarVault

Read [docs/ENGINEERING_HANDOFF.md](docs/ENGINEERING_HANDOFF.md) before modifying code. Update it with each meaningful repository change. Keep this file aligned with the product architecture and unresolved blockers.

## Architecture and product contract

- Shared .NET 10 domain/services: `src/StellarVault.Core`; PWA host: `src/StellarVault.Pwa`; reusable UI: `src/StellarVault.UI`.
- Stellar on-chain vault is the primary mode. Optional encrypted IndexedDB local mode must be explicitly enabled. Do not silently switch to local persistence or broadcast transactions.
- Preserve the original on-chain `/vault` experience and account/network isolation. Keep mnemonic/keys out of logs, URL parameters and unencrypted persistent storage.
- Offline mutation queues are encrypted at rest, account/network scoped, and use compare-and-swap durable state transitions. Never retry Submitting/Uncertain mutations blindly.

## Safety-critical Sync rules

- The manual publishing coordinator exists but is **not exposed in the UI**. Publication requires fresh per-mutation explicit approval, remote conflict checks, and fee/reserve review.
- The default `IVaultLedgerMutationVerifier` intentionally rejects confirmation. **Do not replace it** based solely on a successful Horizon receipt or a matching `svh:<recordId>` ManageData manifest operation.
- HD-v2 writer `src/StellarVault.Pwa/LegacyLedger/StellarVaultStore.cs` writes encrypted `svc:` chunks then a `svh:` manifest, potentially across multiple transactions, and returns only the last hash. Independent confirmation still requires network/account provenance, chunk integrity, and matching decrypted record fields.
- Horizon inspection utilities are read-only diagnostics; never interpret their results as an authorization to mark queued mutations Confirmed.

## Developer workflow

- Prefer complete end-to-end implementation batches instead of isolated cosmetic checks.
- Review the existing source/API contracts before edits; preserve compatibility for future MAUI sharing.
- Current user preference is to defer builds/tests until explicitly requested. State clearly when validation has not run; never claim production readiness without evidence.
- Keep docs and AI handoff truthful: distinguish implemented source from integration-tested/production-ready behavior.

## Current Horizon evidence boundaries (2026-11-03)

- `HorizonVaultManifestInspector.InspectManifestAsync` validates the 51-byte HD-v2 manifest envelope and returns metadata, **not** complete independent publication proof.
- `HorizonVaultPublicationDiagnostic.InspectForNodeAsync` optionally checks the **operation** source account against the expected HD child. A sponsored transaction's receipt source may be different.
- Manifest metadata (including ciphertext digest and generation) is only a commitment; verify historical `svc:` writes, their account lineage, SHA-256 assembled ciphertext, expected decrypted record fields, and network identity before considering any confirmation integration.
- Never confuse `ManifestFound` with a verified queue mutation. Fail closed on ambiguous/missing operations and incomplete pages. The default verifier and UI publishing feature remain disabled.

## Current-state ciphertext diagnostics (2026-11-03)

- `HorizonVaultCurrentChunkInspector` reads only the current Horizon account `data` snapshot, requiring an exact matching HD-v2 manifest, generation-indexed `svc:` chunks, exact chunk lengths and SHA-256 ciphertext commitment. It returns no encrypted bytes.
- `HorizonVaultPublicationDiagnostic.InspectCurrentSnapshotAsync` binds the read-only snapshot check to a successful receipt and an explicitly supplied HD child operation-source account. It must not mark a queue item Confirmed.
- A current account snapshot is mutable and **not** evidence that all chunks were written before the manifest transaction. Network passphrase and deterministic root-to-child lineage are still unverified; the verifier remains deny-by-default.
- Cap account JSON at 2 MiB and ciphertext inspected at 1 MiB; larger data must fail closed until a paginated/historical verifier is designed.

## Historical chunk provenance diagnostics (2026-11-03)

- `HorizonVaultHistoricalChunkInspector` scans bounded descending `/accounts/{HD-child}/operations` pages, anchored *before* the final manifest operation ID. It reconstructs the latest successful generation-specific `svc:` writes, verifies their unique transaction receipts, checks ledger ordering, and recomputes the manifest's SHA-256 ciphertext commitment.
- Limits: 64 chunks, 16 pages × 200 operations, 512 KiB per response. Missing/pruned history, malformed writes, cursor failures, unexpected redirects and unmatched digests fail closed.
- `HorizonVaultPublicationDiagnostic.InspectHistoricalChunksAsync` is intentionally read-only and requires the expected child operation source account. This is not full verification: network passphrase, deterministic child derivation/lineage and decrypted record fields are still unproven. The actual mutation verifier remains deny-by-default and Sync UI broadcasting stays unavailable.
- IMPORTANT: Horizon cannot independently establish its network identity merely from transaction hashes/receipts. Bind known network passphrase and trusted endpoint separately before any future confirmation action.

## 2026-11-03 network identity and historical ledger binding
- `HorizonVaultNetworkInspector` compares the selected exact Stellar network passphrase against a bounded HTTPS Horizon root response and computes SHA-256 network ID. This is an endpoint-attested identity check, **not an independent consensus proof**. Never accept a passphrase merely discovered from an arbitrary endpoint as the expected value.
- `HorizonVaultPublicationDiagnostic.InspectBoundHistoryAsync` composes the expected passphrase check with the historical ciphertext diagnostic. It deliberately does not implement `IVaultLedgerEvidenceProvider` or enable confirmation.
- Historical chunk operations now require a positive ledger number matching each successful receipt; different ledger numbers sharing a transaction hash are rejected. Account operations/history may be pruned and still require root-HD lineage and matching decrypted record content.

## Horizon schema compatibility correction (2026-11-03)

- Do not require `ledger` from a Horizon ManageData operation object. The documented operation representation provides `id`, `transaction_hash`, `transaction_successful`, `source_account`, `name`, and `value`; determine ledger height through the corresponding successful transaction receipt.
- Historical chunk provenance uses strict operation IDs before the manifest operation, and successful chunk receipt ledger numbers no later than the final receipt ledger. Receipt ledger equality can be valid for multiple operations in one transaction/ledger.
- The historical coordinator now fetches the final transaction receipt and manifest once each, rather than performing a redundant preliminary diagnostic and fetching again.
- Treat this correction as source-only until tested against real Horizon operation pages; do not enable mutation confirmation, signing, or broadcast.

## 2026-11-03 connected HD-v2 record comparison

- `BrowserStellarLedgerRecords.InspectConnectedRecordAsync` compares the decrypted **current** record against an approved `LedgerRecordDraft` using the already-unlocked HD-v2 session, explicitly verifying expected root, child account, node position, Horizon endpoint and network passphrase. It returns **no plaintext** and rejects recovery warnings or ambiguous duplicates. This is read-only; it neither unlocks a new session nor broadcasts.
- `HorizonVaultPublicationDiagnostic.InspectBoundRecordAsync` combines network-passphrase check, historical ciphertext commitment, current ciphertext snapshot and connected decrypted record comparison. **The result is NOT proof for mutation confirmation**: current account data can change and the historical verifier doesn't decrypt the exact historical bytes. An authorized queue confirmation provider still needs immutable exact-historical-bytes decryption and independent HD root-lineage/network trust guarantees.
- DI now registers `BrowserStellarLedgerRecords` once and maps `IStellarLedgerRecords` to the SAME scoped instance. Do not use a separate ledger instance for verification, and do not expose these diagnostics as confirmation or broadcasting UI.
- Tests/builds remain deferred at user request. This source-level change needs explicit .NET 10 build, browser, and Testnet validation before production use.
