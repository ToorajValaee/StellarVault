# StellarVault engineering handoff — 2026-10-27

Repository: ToorajValaee/StellarVault. Branch: main. Always inspect current HEAD before editing.
This document records implementation progress, not evidence of production readiness.

## Product contract
- Default on-chain-only: BIP-39 access, signer held in memory, no mandatory device storage.
- Explicit opt-in encrypted offline storage: local drafts are device-only until independently confirmed on Stellar.
- Preserve original full /vault ledger browser; offline mode supplements it.
- PWA first, Core shared with MAUI; user has repeatedly requested code changes with no builds/tests yet.

## Implemented, but not compiled or runtime-verified
- HD ledger records, folders, file protocol, on-chain access, Stellar cost estimation.
- Encrypted IndexedDB queue, draft creation/edit/discard, local folders, partial offline file recovery.
- Pending/Submitting/Uncertain/Confirmed/Conflict/Failed queue states.
- Dependency review, fee quotes, approval preview, remote-ID checks, legacy 32-to-16-character ID migration.
- Atomic reservation and no-blind-retry quarantine/reconciliation foundations.
- Read-only Horizon receipt inspector and evidence-based verifier code (NOT wired; app still DENY by default).

## Critical remaining blockers (P0)
1. No end-to-end user-approved offline-to-Stellar broadcast flow.
2. No live verifier proving network, transaction, and specific record-to-transaction linkage.
3. Local folder ID/dependency mapping and multi-stage transaction ordering are incomplete.
4. Legacy ID migrations and parent references need full consistency handling.
5. Fee and ledger collision checks are time-of-check snapshots, not atomic chain guarantees.
6. Crypto/browser storage/session/JS security audit and complete builds/tests have not been performed.

## Other major gaps
- Full chunked offline file upload, resume and verified download.
- Conflict resolution and uncertain submission investigation.
- Native MAUI offline adapters and production deployment/observability/security validation.
- UI correctness and end-to-end flows require verification.

## Important paths
- src/StellarVault.Core/Services/VaultPendingMutationQueue.cs
- src/StellarVault.Core/Services/VaultSyncApprovalGuard.cs
- src/StellarVault.Core/Services/EvidenceBasedVaultLedgerMutationVerifier.cs
- src/StellarVault.Pwa/Pages/Vault.razor
- src/StellarVault.Pwa/Pages/Add.razor
- src/StellarVault.Pwa/Services/BrowserStellarLedgerRecords.cs
- src/StellarVault.Pwa/Services/HorizonVaultTransactionInspector.cs
- src/StellarVault.Pwa/Services/IndexedDbVaultPendingMutationStore.cs
- src/StellarVault.Pwa/Program.cs

## Recommended next steps
1. Audit queue and UI source-level correctness; preserve all on-chain behavior.
2. Implement trustworthy Horizon evidence provider with transaction-to-record linkage.
3. Add explicit user-approved, single-shot sync coordinator, durable submission intent, and fail-closed verification.
4. Complete dependency planning, file chunk recovery, conflict resolution, and native parity.
5. Only when authorized: builds, tests, regression/security evaluation and production hardening.

## New chat continuation
Read this handoff and current main HEAD; continue via GitHub tools. For batch commits: create_blob for each file, create_tree on main's current tree/commit, create_commit parented to current main, update_ref with expected_sha. Do not claim tested. Do not submit live transactions prematurely.

## 2026-10-27 follow-up — shared queued-create validation
- A side-effect-free VaultQueuedCreatePolicy now rejects malformed create drafts, duplicate local entity IDs, invalid tags/titles, unsupported file protocol masquerading as a normal record, and unpublished/self parent folder references.
- Queue readiness, publication preflight, and fee-preview UI consult the same policy. This does not prove remote state and still does not enable broadcast.
- Continue to prioritize the true Horizon transaction-to-record proof and explicit one-shot publication/reconciliation; no builds or tests were run.


## Latest implementation: manual Sync execution service
- PWA VaultManualSyncCoordinator has a single-shot PublishApprovedCreateAsync path for eligible create record/folder mutations. It demands explicit approval, current ledger connection, fresh quote, typed remote preflight, updated fee bounds and durable CAS reservation before calling PublishWithIdAsync.
- After a send attempt it records Uncertain (with transaction hash if available). It never automatically retries or marks confirmed. Current UI does NOT expose the publish action; the live evidence verifier is still deny-by-default. A failed durable uncertain-state write may leave Submitting and requires manual recovery.
- This is untested/uncompiled. Do not enable UI until transaction-to-record evidence, approval UX, single-flight concurrency and error recovery have been audited.


## Latest batch: cancellation-safe coordinator
- PWA manual Sync coordinator now gates concurrent calls within its scope. This does NOT lock other tabs or devices.
- After durable reservation, cancellation must not prevent persisting the Uncertain state; a fresh token is used for best-effort recovery.
- If persistence fails, durable Submitting remains and must be reconciled; never blindly retry.
- No UI broadcast enabled; live verification remains deny-by-default; no builds/tests run.


## 2026-10-28 compiler fix
- Fixed the /vault Razor LOCAL PARENT button that contained an unescaped nested string literal inside @onclick and generated CS1525 / CS1026. It now calls OpenLocalParentFolder, which looks up the active local folder and navigates to its parent.
- Removed unused VaultHome._showMnemonic field and assignment responsible for CS0414 warning.
- Based on user-provided net10.0 browser-wasm build output; changes committed without running a build or tests in this environment. Re-run dotnet build and report any remaining errors.


## 2026-10-28 batch: ledger selection and inventory safety
- Manual coordinator rechecks network selection and connection before reservation and before publication. Cancellation after reservation is treated as uncertain, not retried.
- Core preflight fails closed when remote inventory has missing or duplicate record IDs.
- Remaining: transaction-to-record proof, cross-tab concurrency, full Sync UI, production security validation. Builds/tests not run for this commit.


## 2026-10-28 recovery batch
- MarkUncertainAsync reloads the durable entry, verifies the current state and hash, and atomically transitions the current revision. This prevents overwriting a concurrent reconciliation state.
- Added read-only NeedsManualRecoveryAsync for unresolved Submitting/Uncertain mutations.
- Live record-to-transaction verification and UI publishing remain disabled; builds/tests not run.


## 2026-10-28: unresolved submission visibility
- Core now offers read-only InspectRecoveryAsync with persisted transaction hash (when valid) and no-replay guidance for Submitting/Uncertain entries.
- /vault pending list now displays an explicit unresolved-submission warning, and state-aware labels replace misleading 'Not submitted' text for Submitting/Uncertain/Confirmed entries.
- Publishing remains disabled in UI; evidence verifier is still deny-by-default. No builds/tests were run for these changes.
