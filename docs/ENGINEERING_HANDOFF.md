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