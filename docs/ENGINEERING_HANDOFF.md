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


## Latest batch: verifier consistency
- Evidence verifier compares hexadecimal record IDs case-insensitively and refuses malformed empty or oversized titles and missing content.
- Still requires trustworthy transaction-to-record linkage; DENY default persists. No builds/tests run.


## 2026-10-28: batch recovery inspection and hash uniqueness
- Added InspectOutstandingRecoveriesAsync for read-only, account/network-scoped enumeration of Submitting and Uncertain queue entries with actionable no-retry guidance.
- Confirmation now refuses a transaction hash already attached to another queue mutation in the same scope.
- Evidence verifier rejects a supplied hash that conflicts with a mutation's previously stored hash.
- This does not establish transaction-to-record proof and is not cross-device protection. No builds/tests were run. The PWA remains deny-by-default for live verification and manual Sync action is not exposed.


## 2026-10-28 recovery inspection in /vault
- Pending queue load now retrieves scoped, read-only outstanding recovery inspections and shows each unresolved mutation ID, state, persisted hash if available and no-retry guidance.
- No network transactions are initiated by this view. No build/tests executed. Live independent transaction evidence and manual Sync UI are still not available.


## 2026-10-28: fail-closed evidence inspection
- EvidenceBasedVaultLedgerMutationVerifier now treats provider failures as unconfirmed, while preserving caller cancellation, and refuses invalid draft tag shape.
- Horizon inspector rejects responses whose effective request host differs from the configured Horizon host; an HTTP client can still redirect within a host, so this is not a complete redirect policy.
- Independent transaction-to-record proof is NOT implemented, verifier is still DENY in PWA DI, and manual Sync UI stays disabled.
- Source changes not compiled or tested; rerun dotnet build when validation is resumed.


## 2026-10-31: Horizon receipt boundary
- Horizon inspector now rejects missing hashes, non-HTTPS or changed-authority/path effective responses, and transaction receipts lacking source account.
- This does NOT prove a record was written in the inspected transaction. The independent evidence provider and UI publication remain disabled.
- No tests or build performed in this batch.


## 2026-10-31: stable Horizon receipt URL
- InspectAsync snapshots the exact expected transaction URL before SendAsync, and validates the effective HTTPS authority/path against that immutable target to detect redirection; rejects unexpected query parameters.
- Rejects declared receipt bodies over 256KiB. Chunked content without Content-Length remains unbounded by this guard.
- Still no transaction-to-record proof or UI Sync enablement. Build/tests not run.


## 2026-10-31 bounded receipt parsing
- Horizon inspector now reads response bodies with a hard 256 KiB cap, including chunked responses without Content-Length, and rejects invalid JSON instead of trusting unbounded ReadFromJsonAsync.
- This remains receipt inspection only. No transaction-to-record provenance provider is wired; do not enable UI publishing based on it.
- No builds or tests performed.


## 2026-10-31: receipt content policy and production gap summary
- Horizon receipt inspection now rejects mismatched effective URL fragments and unexpected declared non-JSON media types. The bounded 256 KiB reader remains in place.
- CURRENT RELEASE BLOCKERS: no authenticated transaction-to-record linkage; deny-by-default verifier; manual publishing UI not enabled; full offline folder/file sync incomplete; multi-client concurrency and conflict handling incomplete; cryptography, PWA, builds/tests and deployment security have not been validated.
- This commit is NOT a production readiness claim. No build or tests run.


## 2026-10-31: read-only publication eligibility check
- Added VaultManualSyncCoordinator.CanPublishApprovedCreateAsync for non-broadcast eligibility checks using current queue readiness, unchanged quote, current remote records, and latest fees/reserve ceiling.
- This method does NOT reserve, sign, or broadcast. The existing submission coordinator still has its own final checks and the UI does not expose publishing.
- No build/test run. Critical blockers remain verified transaction-to-record linkage, explicit production approval, and complete offline file/dependency flows.


## 2026-10-31: fail-closed read-only eligibility
- CanPublishApprovedCreateAsync now returns false for invalid/stale quote, malformed draft, failed ledger query, network changes or insufficient fee ceiling instead of allowing expected failures to bubble into a misleading publish-ready UI state.
- Caller cancellation is still propagated. This method performs only reads and never authorizes or submits a transaction.
- A separate final submission preflight remains mandatory. Live record-linked verification and publishing UI remain disabled. No builds/tests run.


## 2026-10-31: ledger writer rechecks before Create
- BrowserStellarLedgerRecords.PublishWithIdAsync now reads current ledger state before creating, blocks on recovery warnings or occupied IDs, and validates any parent exists and is a folder. This also protects direct callers outside the offline Sync coordinator.
- This remains a best-effort time-of-check, not a ledger-atomic uniqueness guarantee. No live evidence provider, no UI Sync enablement, no build/tests run.


## 2026-10-31: /vault read-only sync eligibility integration
- The sync review UI now calls VaultManualSyncCoordinator.CanPublishApprovedCreateAsync rather than duplicating only a record collision check; it rechecks queue scope, remote records, cost ceiling and fresh quote.
- Still no broadcast button, no production evidence verifier, and no user-facing transaction submission. Builds/tests not run.
- Major next work: transaction-to-record provenance, file chunking/dependencies, end-to-end browser integration. A maturity estimate is not a validation score.


## 2026-10-31: actual HD-v2 transaction provenance discovery
- Inspected src/StellarVault.Pwa/LegacyLedger/StellarVaultStore.cs: AddAsync stages encrypted chunks followed by the final svh:<recordId> manifest ManageData operation; SubmitWritesAsync may broadcast multiple transactions and returns ONLY the last transaction hash. A last-hash receipt alone does not establish the entire record chain.
- Introduced read-only HorizonVaultManifestInspector to query transactions/{hash}/operations (max 200), requiring a ManageData svh:<recordId> operation and matching transaction_hash; bounded reads, HTTPS endpoint pinning. Registered the inspector in PWA DI. This is a PROVENANCE COMPONENT ONLY, not a full independent proof: confirmation also needs transaction success, exact account/network binding, valid manifest/chunks, and independently decoded record data. Do not wire EvidenceBasedVaultLedgerMutationVerifier as enabled until these are complete.
- Code has not been compiled or tested. Manual Sync UI and automatic confirmation remain disabled.


## 2026-10-31: combined read-only Horizon diagnostics
- Added HorizonVaultPublicationDiagnostic to combine successful receipt and matching manifest operation evidence; registered in PWA DI.
- This is NOT a complete cryptographic proof of record contents or transaction-to-record linkage. It cannot confirm a queue mutation. The default verifier stays disabled and the publishing UI stays disabled.
- No build or tests run. Next: verify chunk history, network binding, and record content before enabling confirmation.


## 2026-11-02: AI repository guidance and fail-closed Horizon diagnostic
- Added root AGENTS.md as an AI-readable repository contract linking this handoff, architecture, local persistence opt-in, no-blind-retry, and the unverified HD-v2 transaction provenance limitation.
- Hardened HorizonVaultPublicationDiagnostic: validate inputs before querying, propagate caller cancellation, return an unverified finding for invalid/unavailable/malformed provider responses rather than letting an exception be interpreted as successful evidence.
- This diagnostic STILL does not prove the decrypted record, chunks, account/network or a complete publication; deny-by-default confirmation and UI publication remain unchanged.
- No compilation or tests have been run; production readiness is not established.


## 2026-11-03: HD-v2 manifest payload evidence (read-only)
- HorizonVaultManifestInspector now offers structured InspectManifestAsync evidence: operation source account, HD-v2 generation, encrypted chunk count/length, ciphertext SHA-256 digest and manifest update timestamp.
- Requires one unique svh:<recordId> ManageData write with the requested transaction hash and a valid 51-byte version-2 manifest; rejects malformed base64, invalid length/count, invalid operation source, ambiguous duplicates, and potentially truncated 200-operation pages. Continues to enforce HTTPS, effective URL matching, bounded 256 KiB JSON reads, and caller cancellation.
- HorizonVaultPublicationDiagnostic.InspectForNodeAsync can additionally compare the manifest operation source with a known HD child account. The transaction receipt's source_account is NOT assumed to equal the operation source in sponsored flows.
- These are structural/evidence improvements only: they do NOT retrieve historical svc: chunk writes, prove chunk hash matches, decrypt the record, establish expected root HD lineage, or verify network passphrase. NEVER enable queued confirmation or manual publishing UI from this diagnostic.
- No builds or tests run per user direction. Recheck C# compilation, Horizon JSON response shapes, and real transaction fixtures when testing is authorized. 

## 2026-11-03: read-only current HD-v2 chunk commitment diagnostic
- Added `HorizonVaultCurrentChunkInspector`: fetches a bounded, HTTPS-pinned current Horizon HD child account response; requires expected account identity, the exact matching current `svh:` manifest envelope, and all generation-indexed `svc:` ciphertext chunks with exact lengths.
- Computes SHA-256 incrementally and compares with the manifest's committed ciphertext digest using fixed-time comparison. Returns only a structured finding, not ciphertext or decrypted data. Limits: 2 MiB JSON response and 1 MiB ciphertext.
- Added `HorizonVaultPublicationDiagnostic.InspectCurrentSnapshotAsync` for an opt-in diagnostic binding the receipt and manifest to an explicit expected HD child and inspecting current account chunks. Registered the service in PWA DI.
- CRITICAL: this checks a **mutable current account snapshot**, not historical writes, transaction ordering, or network/root lineage. It cannot prove record plaintext, publication timing, or independent confirmation. `IVaultLedgerMutationVerifier` remains deny-by-default; UI publishing remains disabled.
- No build or test was run per user preference. Next: historical account operation provenance across pages/transactions and ledger ordering; decode expected record fields with account/network binding before enabling confirmation.

## 2026-11-03: historical HD-v2 encrypted chunk provenance diagnostic
- Added `HorizonVaultHistoricalChunkInspector`: reads descending HD child account operation history using a cursor strictly before the manifest operation, collects the latest successful writes for the exact `svc:<recordId>:<generation>:NNNN` names, and reconstructs the historical ciphertext SHA-256 commitment. Every distinct chunk transaction hash gets a successful Horizon receipt check; its ledger must not exceed the final manifest receipt ledger.
- `HorizonVaultManifestInspector` now requires an operation ID and `transaction_successful=true` for the manifest write; `ManifestEvidence.ManifestOperationId` provides the historical cursor. `HorizonVaultPublicationDiagnostic.InspectHistoricalChunksAsync` exposes the opt-in diagnostic with explicit expected HD child account matching.
- Network requests are read-only with pinned HTTPS effective URL and strict size limits; scanning is bounded to 16 pages × 200 operations and 64 chunks (larger records return incomplete, not confirmed). History pruning, incomplete results or malformed evidence fail closed. No sensitive ciphertext is returned.
- Still blocked: independently prove expected network passphrase and HD root/child lineage, decode/decrypt the record against expected approved mutation, handle more than 64 chunks and historical retention limitations, resolve multi-client conflicts, and provide a securely audited user-approved Sync UI. Deny-by-default queue verifier remains unchanged; no broadcasting enabled.
- No builds/tests run by user request. Validate API response shapes and compile before considering release readiness.

## 2026-11-03: endpoint network binding and chunk receipt ledger checks
- Added bounded `HorizonVaultNetworkInspector` requiring Horizon's reported `network_passphrase` to match an independently selected expected passphrase; computes Stellar network ID as SHA-256(passphrase UTF-8). HTTPS effective origin/path and 64 KiB root size enforced. This attests endpoint configuration but is not independent consensus proof.
- `HorizonVaultPublicationDiagnostic.InspectBoundHistoryAsync` composes endpoint network validation with historical ciphertext inspection, returning a read-only diagnostic. Explicit expected passphrase and HD node account are required.
- Historical chunk operation `ledger` must match its successful transaction receipt ledger and not exceed the manifest ledger; conflicting ledger assignments for the same hash fail closed.
- Pending: verify HD root-child derivation, record decryption and exact approved mutation, production-grade all-size history pagination, queue integration, folder/file Sync, and comprehensive runtime/security tests.
- No builds/tests run by user preference; manually validate Horizon root, operation ledger field and transaction fixture interoperability before trusting results. Confirmation remains DENY and UI broadcast stays disabled.

## 2026-11-03: correction for Horizon ManageData operation schema

- Reviewed the official Stellar Horizon ManageData operation example: its documented JSON lacks an operation-level `ledger` field. Prior implementation required this field and could incorrectly reject genuine chunk operations. Updated `HorizonVaultHistoricalChunkInspector` to rely on a successful, hash-matched transaction receipt for each chunk transaction's ledger, and maintain strict descending operation ID ordering before the manifest.
- Corrected historical diagnostic's success page count (no longer off by one) and reduced redundant final receipt/manifest fetches in `InspectHistoricalChunksAsync`. Endpoints remain read-only and bounded, with fail-closed error handling.
- Note: the verified receipt ledger may equal the manifest ledger if multiple operations occur in the same ledger; operation ID ordering is the temporal constraint used here. Chunk history, endpoint network claims and ciphertext digest still do not prove decrypted record contents or HD root lineage.
- Status: committed source changes, no build/tests performed per user preference. Next priority is derived HD root-to-child lineage and content-level evidence; maintain deny-by-default queue verification and disabled publishing UI.

## 2026-11-03: combined connected HD-v2 decrypted-record diagnostic

- Added read-only `BrowserStellarLedgerRecords.InspectConnectedRecordAsync` using the existing unlocked HD store. It validates the expected root account, HD node account/index, Horizon URL and passphrase against the live connected session. It rejects incomplete/warning-bearing HD chain reads, ambiguous IDs, or any mismatch in decrypted record fields (title, secret/content, parent folder, kind, username, URL, notes, ordered tags). It returns only a boolean/explanation and never exposes plaintext.
- `HorizonVaultPublicationDiagnostic.InspectBoundRecordAsync` now combines endpoint network check, historical `svc:` ciphertext hash, matching current manifest/chunk snapshot and connected read/decrypt comparison to the approved draft. This is read-only and deliberately **does not** implement `IVaultLedgerEvidenceProvider` or enable queue confirmation.
- PWA DI now explicitly shares the single scoped `BrowserStellarLedgerRecords` instance between direct diagnostic consumers and `IStellarLedgerRecords` consumers. A separate injected instance would otherwise be disconnected and cannot be treated as evidence.
- Remaining critical caveat: this checks **current** decrypted record against approved fields, not decryption of the **exact historical ciphertext** preceding the inspected manifest. Mutable account state and untrusted endpoint/HD lineage prevent authoritative transaction-to-record confirmation. Still need immutable historical ciphertext reconstruction exposed via a tightly controlled in-memory decryption boundary, trusted deterministic root-to-child path verification, and full validation.
- No builds or tests run per user request. Do not claim production-ready, do not change deny-by-default verifier, and do not expose manual publishing UI.

## 2026-11-03: historical encrypted record decryption diagnostics

- Added a trusted ephemeral callback to `HorizonVaultHistoricalChunkInspector`. Once successful chunk operations/receipts and the committed SHA-256 ciphertext hash have been checked, the service assembles exactly those historical bytes and invokes the callback. Its temporary buffer and per-chunk buffers are zeroed at return/failure.
- Added `StellarVaultStore.CompareHistoricalHdRecordAsync` for read-only decryption of the exact historical HD-v2 envelope using the already unlocked session, selected node index, record ID and generation; compares all fields of an approved `LedgerRecordDraft`, returning only a boolean. Added `BrowserStellarLedgerRecords.CompareHistoricalEncryptedRecordAsync` to validate connected root account/network/Horizon selection before forwarding.
- Added `HorizonVaultPublicationDiagnostic.InspectHistoricalRecordAsync` to combine Horizon endpoint network passphrase, successful final receipt, matching manifest source, reconstructed historical ciphertext, and connected-session decrypted record comparison.
- **Not production-grade proof:** the selected Horizon endpoint still provides the network and operation evidence; HD root/child membership relies on the live session's chain load and has not been independently audited. Existing queue evidence provider remains unimplemented and intentionally denied, and manual broadcast UI is still disabled. The 64-chunk/16-page proof cap remains.
- Source not compiled, browser-tested or security-reviewed per user instructions; specifically validate JS WebAssembly decryption API and historical op fixture shapes. Next: independent root-child derivation proof, explicit offline queue reconciliation integration under separate review, then full file sync and tests.

## 2026-11-03: explicit deterministic, authenticated HD lineage check

- Added read-only `stellarVault.stellar.inspectHdLineage`: for an existing HD-v2 session, derives root (index 0) and requested child index with SEP-5 from the in-memory HD seed, validates addresses, re-fetches the current root `sv:registry:v2` and checks its HMAC via the seed- and network-bound registry key. Requires current registry highest/active/revision to equal the cached session registry, and child index to be allocated and exist on Horizon. Does not export mnemonic/secret seed, private key or data.
- `StellarVaultStore.CompareHistoricalHdRecordAsync` invokes this stronger lineage gate with cancellation before reading the chain or decrypting exact historically reconstructed ciphertext. Any missing/stale registry, mismatched root/child or unsupported derivation scheme denies the read-only comparison.
- Remaining limitation: re-authenticated current registry is NOT historical ledger-time registry provenance, and Horizon is still the queried endpoint; do not claim independent consensus or enable mutation confirmation. Existing fallback discovery mentions `bip32-compat` while `hd-keys.js` only supports `sep5`: this mismatch requires explicit migration compatibility investigation, not optimistic fallback.
- Source not compiled/tested per user instructions. Priority validation: loadAccount accountId API, registry HMAC interop, JS/C# async cancellation and browser network fixtures. Continue offline file/dependency and approval UX work only behind deny-by-default verifier.

## 2026-11-03: repair unsupported HD fallback and lock Horizon discovery URL policy

- Fixed a recoverability bug in `src/StellarVault.Pwa/wwwroot/js/ledger-compat/hd-discovery.js`: previously after a 404 for SEP-5 index zero, the code attempted `accountSeed(..., 'bip32-compat')` although `src/StellarVault.Pwa/wwwroot/js/hd-keys.js` supports only `sep5`. Now it performs one supported SEP-5 check and explicitly reports that legacy recovery is not implemented, without claiming to have checked a legacy address.
- Strengthened `ledger-compat/app.js` Horizon URL normalization for the account-signing/session path: direct HTTPS root URLs only, except loopback HTTP; reject credentials, query, fragments and arbitrary paths. Root/ledger/fee browser discovery uses `credentials: omit` and `redirect: error` with CORS.
- This is a compatibility/security correction, not complete historical on-chain registry provenance, cross-device approval safety, or production readiness. No signing or broadcasting paths have been enabled by these changes.
- No builds/tests run. Priority next: explicit historical root registry evidence, browser SDK error-path fixtures and a comprehensive production security/build/test pass.

## 2026-11-03: historical root registry provenance and seed-bound authentication

- Added bounded `HorizonVaultHistoricalRegistryInspector` to find the last `sv:registry:v2` operation **preceding the manifest operation ID** on the expected root, requiring matching source, successful operation, transaction hash and exact 50-byte envelope. It is deliberately not a standalone authentication provider.
- Added `stellarVault.stellar.inspectHistoricalHdRegistry` JS bridge: re-derive SEP-5 root and child public addresses from the unlocked seed, verify the historical registry envelope HMAC bound to the selected network and root, and require historical allocation of the expected child index. Its input/copy is zeroed; it returns only boolean.
- Added read-only wrappers in `StellarVaultStore` and `BrowserStellarLedgerRecords`, with existing connected-session/endpoint/passphrase checks. `InspectHistoricalRecordAsync` requires successful historical registry transaction receipt no later than manifest receipt and seed-authenticated registry before proceeding to exact historical ciphertext comparison. Registered new inspector in PWA DI.
- Scope limits: root operation history scans at most 16 pages / 3200 operations and requires available unpruned Horizon history; no independent network-consensus proof, historical account signing-authority check or audited queue integration. No build/test run as requested. Keep deny-by-default verifier and publishing disabled.

## 2026-11-03: read-only queued-mutation to historical evidence bridge

- Added `VaultQueuedCreateHistoricalReview` in PWA Services, registered scoped in `Program.cs`. It accepts the queued mutation and **exact stored** final tx hash, validates Submit/Uncertain state and create-record/create-folder kind, record/mutation identity, expected child node index/account, draft structure and folder-vs-record kind.
- It binds `mutation.NetworkId` to the currently selected network namespace via `VaultNetworkSelection.CredentialKey`, and uses canonical HTTPS Horizon/passphrases for Testnet/Public or explicit configured custom HTTPS root/passphrase. It fails closed on any account/network mismatch and delegates to the authenticated historical-registry/chunk/ciphertext/decryption pipeline. It returns only booleans/explanation, no contents.
- Critical: it does NOT implement `IVaultLedgerEvidenceProvider` or mark anything Confirmed. A pending queue entry without a stored final hash, expired approval, stale queue revision or disconnected HD session requires explicit reconciliation; never infer success from a matching record.
- No build/tests run per user preference. Next: source audit of queue reconciliation stale-state handling and safe user-visible reconciliation, followed by full Testnet/browser validation and file Sync.

## 2026-11-03: durable mutation revision binding for historical review/reconciliation

- `VaultQueuedCreateHistoricalReview` now accepts the existing encrypted `IVaultPendingMutationStore` plus `VaultDeviceRetentionGate`, checks the supplied Submitting/Uncertain mutation against an authenticated durable revision before the first network request, and repeats the read after historical record verification. Both comparisons include all immutable fields, payload bytes, dependencies, state, hash and timestamps. A vanished/replaced mutation, locked store or selected network switch fails closed.
- `VaultPendingMutationQueue` now uses full `SameDurableRevision` equality in `MarkUncertainAsync` and `ConfirmReconciledAsync`, preventing stale caller snapshots from advancing an unrelated durable version. The existing encrypted IndexedDB store CAS remains the last transition boundary.
- This does not enable confirmation, broadcasting or automatic retries; `DenyVaultLedgerMutationVerifier` remains registered. This is a source-level safety improvement requiring compile/browser/concurrency tests later; no builds/tests run by user preference.
- Next: audit queue final-hash persistence and uncertain-without-hash investigation flows; test multi-tab CAS behavior, then move into offline folder/file sync.

## 2026-11-03: unknown-transaction-hash recovery diagnostics (read-only)

- Added `VaultUnrecordedSubmissionDiagnostic` (scoped DI) to inspect a **manual candidate** transaction hash against an unresolved durable mutation that has no saved hash. It requires the unlocked encrypted queue, exact mutation/account/network scope, and Submitting/Uncertain state; delegates to the historical registry + chunks + session decryption and approved draft comparison, returning booleans and explanation only.
- `VaultQueuedCreateHistoricalReview` now exposes `InspectUnrecordedCandidateAsync` alongside the existing stored-hash-only `InspectAsync`. Both enforce authenticated queue revision checks before/after the long-running inspection; the candidate route is allowed only when `TransactionHash == null`, and the stored-hash route still rejects mismatches. Custom network endpoint/passphrase and selected network are rechecked after the scan.
- A candidate match does **not prove** which transaction the earlier local submission sent, nor grant authority to attach the hash, replay, mark Confirmed, or clear an Uncertain state. This remains a diagnostic-only API; default verifier and UI broadcasting still disabled. No builds/tests run by user request. Next priority: recovery UX with explicit uncertainty disclosure, independent transaction provenance, safe queue reconciliation and later complete file synchronization.

## 2026-11-03: read-only /vault unknown-hash recovery panel; repair missing method

- Added a candidate transaction investigation panel in the selected pending mutation modal for Submitting/Uncertain create-record/folder with no durable hash. Requires a connected HD session, 64-hex candidate hash, expected G-address and deterministic child index 1..63; shows matched vs unverified result without affecting queue state or enabling publication. Existing CHECK LEDGER CONFIRMATION remains only for entries with recorded hashes.
- Discovered compile-facing source mismatch from prior commit: `VaultUnrecordedSubmissionDiagnostic` invokes `VaultQueuedCreateHistoricalReview.InspectUnrecordedCandidateAsync` but the prior committed reviewer lacked that method. Added the method and a shared `InspectCoreAsync` with explicit candidate/stored-hash input policy and unchanged durable before/after revision guards.
- No builds/tests were run. Validate Razor compile, DI resolution, browser network identity, historical Horizon fixture shape, race behavior and UX text. Matching an external transaction candidate is NOT proof that the local interrupted submission produced it; do not assign transaction hash or mark Confirmed.

## 2026-11-03 Core-owned Sync and encrypted MAUI queue

The manual Sync coordinator and outcome record have moved from PWA Services to Core Services. Its existing fee approvals, durable submission reservation, no-replay rules and Uncertain quarantine remain intact. The PWA now consumes the Core service without copying it.

Added FileSystemVaultPendingMutationStore to Core, implementing IVaultPendingMutationStore for native hosts. Queue entries are fully encrypted with the unlocked account vault key and the same account/network/id associated data as the browser queue. Process-wide file locks, checked revisions and same-directory atomic replacement safeguard cooperating writers. Serialized JSON size permits base64 expansion of the existing 8 MiB mutation cap. This is not an anti-rollback anchor.

MAUI now registers the local-retention consent gate, native encrypted queue adapter, shared pending queue, shared coordinator and deny-by-default verification. Native ownership authorization remains denied, so these registrations do not unlock a complete hybrid mode or expose paid transactions.

Remaining: move shared review and fee/approval orchestration to Core/UI, complete offline file/folder Sync, native authorization, tests and release gates. Existing direct on-chain writer was deliberately left alone. No builds or tests were run at the user's request.

## 2026-11-03: Core hybrid review + PWA/native shared UI integration

**Delivered source changes**
- New `VaultHybridSyncReviewService` in Core. It provides side-effect-free review of pending creates/folders: checks retention consent, account/network namespace, dependencies and blocked/conflict/uncertain states, reads one ledger inventory, rejects duplicate record IDs and missing parents, reuses `VaultQueuedCreatePolicy` and `VaultPendingMutationQueue.PreflightCreateAsync`, obtains fee/reserve estimates from `IStellarLedgerRecords` and produces short-lived `VaultSyncApprovalSnapshot`s. Update/delete/file kinds are explicitly not estimated.
- Removed the PWA page's duplicate fee and dependency-review implementation. `EstimateReadyDraftsAsync` now maps the Core service report to the existing informational review UI. It does not enable publish or queue confirmation.
- Added reusable `StellarVault.UI/Components/VaultHybridSyncReview.razor`, used by MAUI's NativeVaultWorkspace. The shared panel shows ready/blocked/uncertain/conflict counts and per-draft explanations and estimates, requires unlocked local device vault plus retention consent, and contains no submit or mutate actions.
- Registered `VaultHybridSyncReviewService` scoped in both PWA and MAUI. Native still retains a deny-by-default owner authorizer, so the actual native hybrid session remains inaccessible until independently authorized.

**Remaining vertical slice**: native local-vault authorization and explicit storage-mode consent, shared offline-create/edit workflow, reviewed queue submission UX, durable transaction attribution/confirmation, cross-platform file chunk queue and conflict resolution. Direct on-chain writer untouched. Test/compile not run at the user's request.

## 2026-11-03: shared Core/UI offline records and folders, local-only native unlock

A new `StellarVault.Core.Services.VaultHybridDraftWorkspace` provides cross-host encrypted local record/folder draft creation, revision, listing, and discard. It applies the shared consent gate and unlocked account/network scope, validates record/folder draft structure, allocates 16-character record IDs, and encodes pending local folder parents as queue dependencies rather than pretending they are on-chain. The existing `VaultQueuedCreatePolicy` and queue preflight block children until parents are independently confirmed.

New shared `VaultHybridDraftEditor` Razor component adds a usable record/folder editor, pending local folder parent selector, encrypted draft directory, edit and discard actions. It is displayed in PWA Vault when local retention is enabled and in MAUI's native workspace after unlocking local encrypted storage. The existing direct ledger record and file UI is unchanged.

MAUI now presents an explicit device-only retention opt-in and separate vault encryption passphrase, and registers the already-existing `LocalOnlyVaultNamespaceAuthorizer` used in PWA. This **does not verify Stellar ownership**. Native local content is isolated by a G-address namespace and protected by the local wrapped key; ledger transaction authorization is still a separate mnemonic-based HD session. Honest UI warnings disclose the difference. Wallet-signature challenge authorization and secure native key custody remain separate acceptance criteria.

Unfinished: fully approved hybrid Sync publication and independent confirmation remain disabled; local edit/delete of already-published remote entities, offline file sync, native verified wallet ownership, cross-device recovery test, build and browser/device tests still pending. User explicitly requested no builds/tests; none were run.

## 2026-11-03: shared paid-transaction approval integrated for hybrid create

The Core `VaultManualSyncCoordinator` now captures a stable local-device session epoch and selected network, validates the unlocked session matches the queued account, and compares the **actual connected HD signing root** via `IStellarLedgerRecords.InspectAccountInfoAsync` before preflight, immediately before reservation, and after reservation before signing. This prevents publishing a device-only namespace entry through a different connected mnemonic account. It remains a single-shot, CAS-reserved no-retry coordinator.

`StellarVault.UI/Components/VaultHybridSyncReview.razor` now handles the full two-step transaction approval for create-record/create-folder queue entries: quote selection invokes fresh shared Core `CanPublishApprovedCreateAsync`, displays the exact account, mutation, fee/reserve and fingerprint, obtains an explicit paid-transaction consent checkbox (plus separate Public Mainnet acknowledgment), and on confirmation invokes Core `PublishApprovedCreateAsync`. The selected quote is consumed before the call, and any result—including returned hash—is displayed as **Uncertain**, never Confirmed; failures prompt durable-queue/ledger investigation without retry.

PWA Vault now mounts this shared approval component in its local pending section instead of its former preview-only markup. MAUI already mounted the same shared component and uses the same Core coordinator, native encrypted queue and ledger transport; its local-only account access must never be mistaken for signer ownership. Existing direct on-chain operations were not rewritten.

**Milestone remains open:** independent record-to-transaction confirmation provider is deny-by-default; this means parent folder submission does not immediately release child drafts. Hybrid update/delete, file synchronization, device-level and clean-device Testnet verification, builds and security review remain. The source is not release validated; no builds/tests run per user preference.

## 2026-11-03: hybrid recorded-hash reconciliation and folder dependency unblock

Added a host-scoped `HorizonVaultQueuedMutationEvidenceProvider : IVaultLedgerEvidenceProvider`, compiled in both PWA and MAUI. It accepts only existing recorded 64-hex hashes on Submitting/Uncertain creates, checks local vault session/account/network and connected mnemonic-derived HD root, finds the exact transaction's historical manifest, resolves the expected child index from the connected authenticated HD chain (via new `BrowserStellarLedgerRecords.ResolveAuthenticatedHdChildAsync`), then runs the established historical network, registry HMAC, ciphertext chunk and decrypted draft comparison. Only a fully matching historical proof yields `RecordLinkedToTransaction=true`. The Core `EvidenceBasedVaultLedgerMutationVerifier` rechecks contents, and Core queue CAS finally promotes the exact original revision to Confirmed.

The queue now rejects `ConfirmReconciledAsync` if the original mutation has no durably recorded hash, closing the candidate-hash attribution loophole. Unknown-hash investigations remain read-only; no automatically inferred transaction hashes.

Both PWA and MAUI now use this Core verifier and host evidence adapter. MAUI links the existing Horizon inspector source files and provider into its own build and registers the full graph; it maps `IStellarLedgerRecords` onto its scoped `BrowserStellarLedgerRecords` concrete service so historic proof and signing share exactly one session. The shared `VaultHybridSyncReview.razor` displays unresolved queue entries with hash and offers a manual `VERIFY RECORDED TRANSACTION — NO RETRY` action. A positive proof results in Confirmed + reloaded queue; dependencies can then advance, but next publish still requires a fresh explicit fee approval.

**Security/release blockers**: no builds/tests run by user request. Validate .NET 10 MAUI/PWA compilation, JS/HD registry proof, history truncation/rollback cases, native runtime, account/network switching, idempotency under retries, Testnet real receipt/manifest/chunk sequence, and independent audit. This is source-level functionality, not an assertion of production readiness. Existing direct on-chain flows unchanged. Hybrid update/delete and file sync remain incomplete.

## 2026-11-03 — offline update/delete staged workflow across Core, UI, PWA and MAUI

**Delivered:** New `VaultHybridRemoteMutationWorkspace` in Core for UpdateRecord/UpdateFolder/DeleteRecord/DeleteFolder local staging. Requires opted-in local retention, unlocked matching vault account/session epoch, connected mnemonic HD wallet with the **same root account** and scoped Stellar network. Reads a complete remote inventory and validates distinct record IDs. Excludes chunk/file-protocol records; validates update fields and parent folders, detects multi-level folder ancestry cycles and refuses deleting folders with published children. Captures deterministic SHA-256 fingerprint over the entire original on-chain record in `ExpectedRemoteVersion`, rechecks both ledger state and scoped queue before saving an encrypted pending mutation. Local update revisions preserve this baseline; local discard works only while Pending. Historical fingerprints are version expectations, not proofs of blockchain transaction origin.

**Shared UI:** New `StellarVault.UI/Components/VaultHybridRemoteChanges.razor` exposes authenticated published-record selection, editable offline update drafts, local deletion intent, offline revision/discard and a read-only remote drift/conflict check. The same component is mounted inside PWA Vault and MAUI's native workspace with scoped registration of the Core service. No existing direct on-chain write behavior was modified.

**Critical unfinished work:** The Core fee/approval coordinator and transaction verifier currently support *create* mutations only. Update/delete mutation records are staged but deliberately not broadcastable or confirmable from the hybrid queue. A later batch must add an independent compare-and-swap **per remote entity** across devices/tabs (not merely repeated queue reads), fresh version-based preflight before reservation, actual signed update/delete path with explicit fees, and authenticated historic receipt comparison and tombstone proof. Multi-device conflict tests and PWA/MAUI builds remain undone under the user's no-build preference. Do not label this feature production ready.

## 2026-11-03: version-pinned update/delete Sync preflight, preview, and Testnet-only submission

A single shared Core approval pipeline now handles staged UpdateRecord/UpdateFolder/DeleteRecord/DeleteFolder. `VaultSyncApprovalGuard` supports these only with a valid 64-character lower-hex `ExpectedRemoteVersion`, binding the complete durable mutation to a short-lived quote. `VaultPendingMutationQueue.PreflightRemoteMutationAsync` checks fresh remote record inventory, unique remote ID, pinned complete-record digest, record/folder kind, text/file protocol exclusion, parent-folder ancestry and nonempty-folder delete prevention, and absence of conflicting or unresolved queue operations. `ReservePreflightedRemoteMutationAsync` durably reserves the approved mutation with CAS.

`VaultHybridSyncReviewService` now returns fresh update and delete cost estimates through the correct `EstimateAsync(draft, existingId)` or `EstimateDeleteAsync(id)` path; separately **fixed create estimates** to use `EstimateAsync(draft)` (no ID), since the PWA adapter treats any ID as UPDATE. `VaultManualSyncCoordinator.CanPublishApprovedRemoteMutationAsync` and `PublishApprovedRemoteMutationAsync` enforce exact account, current local unlock epoch, selected network, current connected HD root and approved quote/cost bounds, then invoke one underlying direct writer operation after durable reservation. Every attempt becomes `Uncertain`, including a returned hash. The independent historical verifier still permits CREATE only; update/delete confirmation is not enabled.

**Safety gating:** There is no atomic ledger-enforced expected-version condition on the existing Stellar update/delete writer, so a concurrent external write may happen after remote preflight. Hybrid queued update/delete **publishing is restricted to Testnet**, enforced both in Core and the shared UI. Public Mainnet and custom-network update/delete queued submission remain disabled. These are experimental source changes, not production-safe public-network features. Existing direct on-chain UI/writer behavior is untouched.

**Still outstanding:** design an atomic/ledger-aware compare-and-swap for remote versions, implement transaction-specific update/delete historic proof and tombstone confirmation, establish cross-tab per-entity mutual exclusion, secure unknown-hash recovery, run two-host compilation and Testnet concurrency/device tests. Builds/tests were not run by previous user preference.

## 2026-11-03: recorded primary-manifest transaction hashes and Testnet UPDATE confirmation

Fixed the primary historical-proof blocker: legacy `StellarVaultStore.UpdateAsync` returns the *last* transaction hash, often the follow-on chunk cleanup, rather than the manifest-write hash. `DeleteAsync` may similarly return an orphan-chunk cleanup hash rather than the manifest deletion. Added optional `CommitTransactionHash` to `StellarVaultPwa.Core.VaultMutationResult` and Core `LedgerMutationResult`; legacy `TransactionHash` remains backward-compatible for the already-working direct on-chain UI. The browser transport forwards both hashes, while `VaultManualSyncCoordinator` saves **only primary commit-manifest hash** in the encrypted hybrid queue. If the primary hash is absent, the mutation is quarantined without a guessed hash.

Extended `EvidenceBasedVaultLedgerMutationVerifier` and the shared host `HorizonVaultQueuedMutationEvidenceProvider` for UPDATE record/folder kinds: requires an exact recorded durable hash, 64-hex pinned old-record fingerprint, authenticated connected root and deterministic HD child, historical seed-authenticated registry, success receipt, manifest, historical ciphertext and decrypted field-by-field match against the approved queued update. Scoped to Testnet only, with Core/host checks and manual button in the shared Sync UI. Queue CAS promotes a verified update to Confirmed; no retry occurs on failure.

**Limitations:** This verifies the *new content was committed by the recorded transaction*, not that the old version was atomically checked on-chain. Concurrent device writes may still clobber each other. Historical delete confirmation remains disabled until exact historical manifest tombstone and prior-state provenance can be verified. Public/custom hybrid remote mutations are still gated; direct on-chain CRUD unchanged. Neither platform has been built/tested (per user preference), so these are source-level changes, not production acceptance.

**Additional lineage hardening in this commit:** `StellarVaultStore.VerifyCurrentHdLineageAsync` calls the existing seed-derived JS `stellarVault.stellar.inspectHdLineage` verifier. `BrowserStellarLedgerRecords.ResolveAuthenticatedHdChildAsync` now returns an index only after both an unambiguous chain snapshot match and that seed-authenticated verification. This is shared by PWA and MAUI's linked ledger implementation.

## 2026-11-03: full staged manifest-owner set for queued hybrid delete

Implemented a substantive cross-platform deletion proof prerequisite. Added Core `VaultDeletionIntent(LedgerRecordDraft Original, string[] ManifestSourceAccounts)` and `IVaultLedgerManifestCopyInventory`. The connected browser/MAUI ledger adapter now inventories every visible `svh:<recordId>` HD child manifest from one complete warning-free chain snapshot and verifies each child through the mnemonic-derived `VerifyCurrentHdLineageAsync` check. Delete staging now encrypts the original approved record fields **and** those authenticated owner accounts into the queue. It re-reads both record fingerprint and owner list before final enqueue, refusing drift.

Core remote-mutation preflight requires the deletion envelope to match the immutable `ExpectedRemoteVersion` full-record fingerprint, have nonempty unique valid source addresses and match record/folder kind. The shared manual Sync coordinator also rechecks owner coverage at eligibility, approval and immediately before durable CAS reservation. PWA and MAUI register the same `IVaultLedgerManifestCopyInventory` backed by their existing connected HD adapter.

Manual deletion recovery now checks the encrypted original record fingerprint against its pinned baseline, passes the expected manifest owners to the Horizon deletion inspector and refuses the `StagedSourcesMatched` flag if the recorded transaction's unique source account set is different. The shared Razor UI displays this result alongside HD lineage, operation count and current absence.

**Security remains fail-closed:** A staged manifest-owner inventory is not a cryptographically authenticated historical predecessor transaction, and Stellar still lacks atomic expected-version enforcement between the last read and transaction inclusion. Consequently this does **not** authorize Confirmed on deletes; Mainnet/custom hybrid remote writes remain blocked. Legacy queue deletes with no source-set envelope cannot be fully reviewed by this new path. Next acceptance step: historic predecessor manifest/ciphertext provenance, safe last-copy coverage and validated Testnet two-device races. Builds/tests deferred.

## 2026-11-03: authenticated historical predecessor coverage for queued deletion

Extended deletion reconciliation with meaningful predecessor evidence. A new `HorizonVaultDeletionPredecessorInspector` scans each source account's bounded descending `manage_data` history **before its exact manifest tombstone operation ID**, returning only the closest preceding successful `svh:<recordId>` write, never skipping a nearer deletion or malformed manifest. It enforces HTTPS same-host response URLs, nontruncated/size-bounded pages, strictly descending operation IDs and valid transaction hashes.

The existing `HorizonVaultDeletionInspector` now exposes `TombstoneSource(AccountId, OperationId)` on successfully checked full recorded transactions. The shared `HorizonVaultDeletionTombstoneReader` verifies the complete staged source set matches those operations and for *each* deleted child: obtains its nearest predecessor hash; independently validates the historical manifest operation identity/source; resolves the mnemonic-derived authenticated HD child index; and calls the pre-existing `HorizonVaultPublicationDiagnostic.InspectHistoricalRecordAsync` to authenticate Horizon network, root registry, complete chunk transaction history, decrypted ciphertext and exact fields against the **encrypted original delete intent**. A separate `HistoricalPredecessorMatched` diagnostic result becomes true only when every source matched; shared UI exposes that result in PWA and MAUI, but does not mutate queue status.

This closes the previous *diagnostic historical-predecessor* gap for deletes staged with a complete original/owner envelope, but DOES NOT establish atomic no-concurrent-write semantics or independent multi-Horizon trust. DELETE remains permanently `Uncertain` until a carefully designed ledger-level predicate (or alternative provably safe protocol) plus full network/device validation. The Core evidence verifier is deliberately not expanded to deletes. Direct on-chain path unchanged. No builds/tests run at user request.

## 2026-11-03: guarded ledger-writer boundary for hybrid remote mutations

Implemented a distinct Core `IVaultGuardedLedgerMutations` contract, consumed by the manual Sync coordinator for version-pinned queued UPDATE/DELETE. The existing `BrowserStellarLedgerRecords` implementation (shared in PWA and MAUI) now routes queued operations through `UpdateIfUnchangedAsync`/`DeleteIfUnchangedAsync` and retains its original public `IStellarLedgerRecords.UpdateAsync`/`DeleteAsync` API for direct on-chain operations. Both hosts resolve the guarded interface to the **same connected ledger adapter scope**, avoiding a divergent signing session.

The underlying `StellarVaultStore` now accepts optional guarded version and owner inputs. Immediately before a guarded write it repeats a complete warning-free ledger read, verifies exactly one decrypted target record and checks the full canonical `VaultHybridRemoteMutationWorkspace.Fingerprint` matches the approved pinned `ExpectedRemoteVersion`. The guarded delete additionally checks the complete observed `svh:<id>` manifest-owner account set against the staged immutable owner list from the encrypted `VaultDeletionIntent`, after reloading the HD chain and before creating manifest-delete operations. Drift results in an exception and no guarded writer submission. The coordinator still makes the durable mutation Uncertain once the CAS reservation was acquired, even if a guard rejects: it never performs a second attempt.

**Remaining critical safety gap:** These are optimistic transport-level checks only, NOT ledger-enforced atomic record CAS. HD-v2 chunk writes and the manifest are submitted via separate root-funded transactions. A second writer can still race after the last read or during multipart writes; the native/PWA adapters must not claim Mainnet production safety. Mainnet/custom queued UPDATE/DELETE remains disabled. The next architectural step needs a genuinely conditional ledger commit protocol (potentially a Soroban-managed version authority, with explicit design and migration), not another read-only Horizon comparison. The project has not been built or Testnet-validated, per earlier user instruction.

## 2026-11-03: deletion quote bug fix and conflict-safe offline record copy

During review of the hybrid fee approval pipeline, discovered that `VaultHybridSyncReviewService` still deserialized deletion queue payloads as `LedgerRecordDraft`. Since new deletion entries are encrypted `VaultDeletionIntent` envelopes with the full original record and HD manifest-owner set, every legitimate staged delete lacked a usable fee preview. Fixed by deserializing `VaultDeletionIntent.Original` for the display draft, leaving the strict original fingerprint/owner validation in `VaultPendingMutationQueue.PreflightRemoteMutationAsync`. Remote mutation quotes are now omitted at the Core service boundary on Public/custom networks, matching the existing coordinator Testnet-only guard.

Added an intentionally non-destructive escape hatch for a conflicting pending offline record UPDATE. `VaultHybridRemoteMutationWorkspace.PreserveUpdateAsNewRecordAsync` validates the original immutable baseline, queued record-kind and pending state, reads the existing encrypted proposed update, labels a new draft with an `(offline copy)` suffix, and delegates to the existing `VaultHybridDraftWorkspace.CreateAsync` for a new 16-hex record ID and durable encrypted CREATE mutation. **No submission happens, no original on-chain record is modified, and the source update is not automatically deleted or retried.** The user can separately discard the abandoned update after reviewing the new queued CREATE. The shared `VaultHybridRemoteChanges.razor` now offers this operation on both hosts. Source must be pending UpdateRecord; folder and delete mutations are excluded.

These changes preserve data but do not provide ledger-enforced CAS or automatic conflict merge. Queued create still needs fresh account/network fee review and explicit payment approval and independent confirmation. Testnet remote update/delete remains experimental; Mainnet/custom remote mutation publishing remains blocked. Build and Testnet/device validation remain unperformed by prior user direction. Next architectural step: an actually atomic version authority or a consciously append-only logical-record protocol, rather than more claims about preflight safety.

## 2026-11-03 — complete offline folder reparenting for pending drafts

Shared Core now offers `VaultPendingMutationQueue.RevisePendingCreateAsync(expected, replacementPayload, replacementDependencies)`. The operation checks an entire previously observed durable queue mutation revision, verifies it is an unsubmitted CreateRecord/CreateFolder, validates record/folder draft structure, resolves and verifies any referenced pending local parent, requires exactly one dependency to a pending parent (or none to a confirmed/on-chain parent), walks the dependency ancestry to prevent cycles, and commits both encrypted draft payload and dependency edge through one store compare-and-swap. A stale local editor fails closed. This addresses a product gap where the UI could create a nested offline record/folder but could not move or reparent it without discarding/recreating the draft.

`VaultHybridDraftWorkspace.ReviseAsync` now delegates to that single-CAS revision instead of rejecting changed dependencies. Shared `VaultHybridDraftEditor` gains move-to-root, current-parent selection, child-record/new-subfolder shortcuts and parent display. It supports pending and already-confirmed local folder parents, with children of pending parents still blocked until independent confirmation. PWA and MAUI already mount the shared editor and Core queue, so no duplicate host logic is introduced. Direct on-chain operations are unchanged.

**Safety:** This is only an atomic replacement of one locally encrypted queue entry, not a ledger transaction, cross-tab tree transaction, or network-level version authority. A concurrent edit to another entry can still invalidate a parent dependency; subsequent queue review/preflight must fail closed. Full hybrid validation remains undone under the user's request not to run builds or tests.

## 2026-11-03 — stale offline editor / session-cache hardening

Fixed a concrete data-loss risk in the hybrid editor workflow: `VaultHybridDraftWorkspace.ReviseAsync` previously read the latest durable queue revision just before saving, so a tab holding an older opened draft could silently overwrite a newer draft from another tab despite the queue's compare-and-swap operation. The workspace now exposes `DraftItem.PayloadSha256`, accepts the opened SHA-256 fingerprint on save, and Core `VaultPendingMutationQueue.RevisePendingCreateAsync` checks it against the current durable payload before replacing both payload and parent dependency in one CAS.

Extended the same invariant to pending remote updates: `VaultHybridRemoteMutationWorkspace.StagedItem.PayloadSha256`, `ReviseUpdateAsync(..., expectedPayloadSha256)` and the new `VaultPendingMutationQueue.RevisePendingUpdateIfUnchangedAsync` prevent stale update editors from replacing a newer payload. This *editor-revision* digest is separate from the immutable remote-content baseline stored as `ExpectedRemoteVersion` and does not authorize publishing.

Both `VaultHybridDraftEditor.razor` and `VaultHybridRemoteChanges.razor` now subscribe to the unlocked vault session's StateChanged notifications, clear cached drafts/fields/opened revision on lock or account replacement, and load the encrypted local queue only while still on the original epoch. Both release event subscriptions in Dispose. The UI prompts reopening changed entries instead of replaying a stale save. This is shared across PWA and MAUI; existing direct on-chain CRUD unaffected.

**Unvalidated:** No builds/tests run per prior user preference. Validate C# and Razor signatures, two-tab edit races, lock-during-refresh, initial unlocked render, encrypted queue CAS under rapid revisions, and account switch. No atomic cross-device ledger version protection was added.

## 2026-11-03: prevent orphaned and mismatched folder dependencies from reaching Sync

Discovered that queue readiness evaluated `DependsOn` separately from the serialized CreateRecord/CreateFolder draft `FolderId`. This allowed an inconsistent draft to appear Ready when the recorded dependency pointed to a confirmed but unrelated folder, even though the *actual* intended parent was still a pending folder. Implemented shared `VaultQueuedCreatePolicy` validation of this relationship: the local parent entity ID must resolve to a unique `CreateFolder` mutation, and if that parent is pending the child must depend on **exactly that mutation**. Stale/missing dependencies, non-folder parents, failed/conflicted parents, duplicate local parent IDs, self-parenting, malformed IDs and cycles are reported. Confirmed parents tolerate their original matching dependency for old queued entries; remote parents require no local dependency.

Both `ReviewReadyAsync` and `PrepareReviewAsync` now invoke the policy before reporting creates ready. `PreflightCreateAsync` already uses it, preserving the single source of policy truth through fee approval and reservation. `VaultHybridSyncReview` exposes actionable blocked reasons in PWA/MAUI; no automatic repair, resend or publication is performed. This is a queue-safety fix, not multi-device ledger CAS.

**Unvalidated:** Build and device tests deferred by user's previous instruction. Validate pending-to-confirmed parent transitions, obsolete dependency after draft reparenting, failed/missing parent, duplicate IDs, child edit cycles and the exact C# pattern/Razor compilation. Cross-device remote update/delete remains Testnet-only; direct on-chain flow unchanged.

## 2026-11-03: read-only end-to-end folder/child Sync plan across shared hosts

Implemented the missing guided manual workflow for nested offline folder and record creation. Core `VaultHybridFolderSyncPlan.Build(all, review)` walks account/network-scoped encrypted queue creates, resolves parent folder mutation references and bounded ancestry depth, and classifies each item as ready for review, waiting for unpublished parent, requiring repair, awaiting verification, confirmed, or no longer publishable. The plan is derived from the exact same queue readiness snapshot as Sync review, never from UI guesses about publication. Parent-to-child step ordering is displayed in the shared Razor `VaultHybridSyncReview` on both PWA and MAUI, including whether the next action is a fresh fee review or independent verification of an earlier submission.

`VaultHybridSyncReviewService.Report` now returns `FolderPlan`. Users can manually request fresh cost estimates for currently ready steps. After an approved submission attempt, the shared component reloads the durable queue (including Uncertain or Submitting entries and blocked children) instead of going blank. After recorded-hash confirmation, it reloads the same workflow so the child can become ready, but never submits children automatically.

Also subscribed the shared Sync review to `VaultSessionManager.StateChanged` and cleared selected quote, prior report, historical tombstone findings and unresolved state on account switch/lock. Async review checks the originating session epoch and current network scope before presenting results. This prevents retaining an old approval pane across a vault session transition.

**Not production accepted:** No PWA/MAUI builds or end-to-end Testnet exercise were run (previous user instruction). The plan is read-only; it does not prove current remote parent existence. Existing Core preflight still requires the parent on the authenticated Stellar inventory before any child submission. Cross-device remote UPDATE/DELETE still has no atomic ledger version condition; Mainnet/custom queued remote writes remain disabled. Next acceptance: actual two-host build + manual Testnet folder/child sequence, interruption recovery and cross-tab race scenarios.

## 2026-11-03 — manual crash quarantine + truthful parent/child readiness

Closed a real hybrid recovery gap: durable `Submitting` entries after interruption could be quarantined only through the Core API, while the shared Sync view offered no user action. Added the shared `QUARANTINE INTERRUPTED SUBMISSION — NO RETRY` action in `VaultHybridSyncReview`. It checks unlocked-account/network/session epoch, confirms the mutation still matches the screen, and calls the new queue operation `QuarantineInterruptedIfUnchangedAsync(expected)`. This method independently re-reads the full encrypted durable mutation, compares every revision field using `SameDurableRevision`, then CAS-transitions Submitting→Uncertain. No Stellar call, automatic resubmission or optimistic confirmation occurs. The shared UI reloads unresolved entries and guided folder workflow afterward.

The guided `VaultHybridFolderSyncPlan` previously labeled otherwise-independent pending folder/child creates ReadyForReview while another mutation was unresolved or a conflict existed. The coordinator correctly blocked fee review globally, but the UI guidance was misleading. The plan now demotes those items to AwaitingVerification or NeedsRepair until the global queue gate clears. Confirmed parents still unlock children only after history verification and a fresh on-chain parent inventory.

**Still unfinished:** unknown-hash recovery must be investigated against authenticated ledger history; delete confirmation, atomic multi-device update/delete version control, and cross-host compile/real Testnet end-to-end runs remain outstanding. Builds/tests deferred under earlier user direction. The new manual quarantine does not remove the unresolved block or authorize any retry.

## 2026-11-04 — hashless interrupted submission candidate history

Added the next useful crash recovery step without guessing hashes or weakening queue confirmation. Core `VaultUnrecordedSubmissionReview` accepts only account/network-matched durable Submitting/Uncertain CreateRecord/CreateFolder or Testnet UpdateRecord/UpdateFolder **with no stored transaction hash**, and snapshots the mutation bytes/state, account, session epoch, Horizon endpoint and passphrase before the read-only host search. It rejects all results after any durable revision or session/network change.

The linked PWA/MAUI `HorizonVaultUnrecordedSubmissionReader` scans a bounded recent window of root-funded transactions, validates direct HTTPS Horizon response/ordering/size and filters to successful root-sourced receipts with a matching HD-v2 manifest. For each manifest it resolves the child account through the authenticated unlocked HD seed and invokes the existing full historical verification of network, registry, ciphertext chunks and decrypted queued record. Matching transaction hashes are returned only as **candidates**, never as a new queue hash or evidence of unique attribution. Search is explicitly incomplete: at most 32 transactions examined from one latest page, and old/pruned/parallel histories may not appear.

The shared Sync review now exposes `INVESTIGATE MISSING TRANSACTION HASH — READ ONLY`, shows inspected count and candidate hash(es), and emphasizes `NOT QUEUE CONFIRMATION`. Confirmed/Uncertain transition behavior remains unchanged; no replay, queued-state mutation or fee approval. Hashless DELETE and files cannot use this positive-manifest proof. This improves operator diagnosis but does NOT complete safe unknown-hash reconciliation. Mainnet/custom queued UPDATE/DELETE remain blocked, direct on-chain CRUD untouched.

**Validation gap:** No build, PWA/MAUI device run or live Testnet historical search (previous user instruction). Verify Stellar SDK transaction paging tokens and Horizon response schema with fixtures before release. Next focus: safely persisting original transaction identity before/at broadcast, not treating candidate discovery as unique proof.

## 2026-11-04 — durable pre-broadcast signed-hash checkpoint for hybrid CREATE

Addressed the concrete unknown-hash recovery gap on the **final record creation transaction**, rather than merely broadening post-crash historical search. Introduced `IVaultPreparedCreateLedger` in shared Core and implemented it in the existing connected PWA/MAUI `BrowserStellarLedgerRecords` adapter. Direct on-chain calls retain `PublishWithIdAsync`; the manual hybrid coordinator uses only `PublishWithPreparedHashAsync`.

In the HD-v2 signer, `stellarVault.stellar.submitHdManageData` accepts an optional manifest checkpoint and validates exactly one `svh:<recordId>` manifest write. For its final signed ManageData transaction, `stellarVaultSponsorship.writeSponsoredData` computes the signed transaction hash and **awaits** the .NET `SignedManifestCheckpoint` JSInvokable receiver before calling `server.submitTransaction`. The receiver is single-use, validates the signed SHA-256 hash, and invokes the coordinator's callback. The new Core `VaultPendingMutationQueue.CheckpointSignedCreateManifestAsync` verifies the full exact encrypted durable `Submitting` revision and CAS-persists the signed manifest hash. If this persistence fails, the signed final transaction is NOT broadcast. The coordinator tracks the CAS-returned revised submission object and uses it in `MarkUncertainAsync`, preserving the hash on Horizon errors, lost response, or app interruption. A recorded signed hash still requires independent verified historical publication; a prepared but never submitted transaction can remain Uncertain.

This applies **only to queued CREATE**. Earlier chunk transactions and new-child provisioning occur before the manifest is prepared; an interruption before it is signed can still leave a hashless unresolved queue entry. Update/Delete commit hash checkpoints require their own sequencing and transaction protocol, not reuse of this create-only hook without review. Mainnet/custom queued updates/deletes remain disabled and deletion confirmation still fails closed.

**Acceptance outstanding:** .NET10/Razor and MAUI builds, JS SDK `transaction.hash()` verification, blazor JSInvokable reentrancy/durable IndexedDB CAS behavior, simulated failure at every checkpoint/broadcast boundary, and Testnet independent recovery. No build/test/live broadcast was run at the user's previous request.

## 2026-11-04 — pre-broadcast transaction identity for queued Testnet UPDATE

Extended the hybrid create prepared-manifest checkpoint protocol to **primary Testnet UPDATE manifest** commits. New Core `IVaultPreparedUpdateLedger.UpdateIfUnchangedWithPreparedHashAsync` is implemented by the shared connected browser/MAUI ledger adapter and registered in both hosts. The guarded version-pinned update still performs its final decrypted-version check before writing. `StellarVaultStore.UpdateAsync` now optionally passes a signed-manifest checkpoint to its existing HD-v2 per-write JS signer only for the primary `svh:<recordId>` manifest write, not encrypted chunks or later cleanup. The JS sponsor computes the fully signed primary transaction's hash and awaits a `DotNetObjectReference` durable .NET queue callback **before broadcasting it**.

Core `VaultPendingMutationQueue.CheckpointSignedPrimaryManifestAsync` extends exact-queue-revision CAS persistence to queued CREATE/UPDATE, retaining the prior `CheckpointSignedCreateManifestAsync` alias. The manual Sync coordinator retains the new exact `Submitting` revision after callback and uses it in `MarkUncertainAsync`; lost Horizon responses and post-commit cleanup transaction hashes do not erase/replace the original primary signed identity. The direct on-chain update API remains untouched. Queued DELETE has not been modified: multiple HD manifest owners may require multiple tombstones and a single transaction hash is insufficient complete-deletion proof. Historical confirmation remains independent and exact-hash-based, and still only allows supported Testnet updates.

**Remaining architectural risks:** This is not a Stellar on-chain record-version precondition. Concurrent writes may interleave after last-read guard or while encrypted chunks are being published, and crashes before the manifest is prepared still leave hashless entries and possibly orphan chunks. The production blocker remains ledger-level conditional coordination plus tested parent/child, Testnet concurrency and failure-injection flows. Per user direction no build, tests or live transactions were run.

## 2026-11-04: pre-broadcast manifest owner journal for queued Testnet DELETE

Added VaultSignedDeletionManifest to the encrypted durable VaultPendingMutation queue, with source G-address and fully signed transaction hash. The new VaultPendingMutationQueue.CheckpointSignedDeletionManifestAsync restricts the append to a Submitting DeleteRecord/DeleteFolder whose original encrypted VaultDeletionIntent contains that HD owner. It ensures owner/hash uniqueness, validates the original full durable mutation revision, and saves a new journal entry with store CAS. The native file and PWA IndexedDB mutation comparators now include journal equality; Core deletion and missing-hash diagnostic revision checks do too.

Introduced IVaultPreparedDeletionLedger, implemented in the already-connected shared BrowserStellarLedgerRecords adapter and registered for both hosts. The optional journal callback moves through StellarVaultStore.DeleteAsync, SubmitWritesAsync and submitHdManageData into the signed HD-v2 sponsorship transaction. The signer invokes the .NET SignedDeletionCheckpoint receiver with the deterministic HD source address and signed hash after signing and before Horizon submission. Callback/persistence failure prevents broadcast of that owner tombstone. Earlier successful owner tombstones may already have landed, however, so unknown or interrupted outcomes remain Uncertain and cannot be replayed. The coordinator uses the latest exact durable queue revision and avoids assigning a single legacy TransactionHash to a journaled multi-owner delete.

Shared VaultHybridSyncReview displays the signed per-owner journal. Signed hashes are NOT successful receipts or a queue confirmation protocol; no queue deletion is marked Confirmed. Direct on-chain deletion and manifest/chunk cleanup are unchanged. Mainnet/custom queued update/delete stays disabled. Follow-up milestones: per-owner historical journal receipt verification, Testnet partial deletion failure injection, and atomic cross-device version control. No builds or live tests run per user preference.

## 2026-11-04 — shared deletion journal historical investigation

Added a full read-only per-owner investigation path for the encrypted signed-deletion journal introduced in the previous commit. Core `VaultDeletionJournalReview` takes a queued Uncertain/Submitting DeleteRecord/DeleteFolder, revalidates the pinned approved original draft fingerprint and the entire staged unique HD manifest-owner inventory, and rejects duplicate/foreign/malformed signed hash entries. Each expected manifest owner receives a result: NotCheckpointed (no durable signed hash), UnresolvedTransaction (missing or mismatched Horizon proof), or VerifiedHistoricalTombstone (the **existing** `IVaultDeletionTombstoneReader` verified a singleton owner/hash pair). This reuses the existing strict historical tombstone inspector, HD derived-child authentication, nearest-before-tombstone predecessor manifest, authenticated historical registry/chunks and decrypted approved original record. The whole result is rejected if the account, encrypted queue revision, session epoch, selected passphrase or Horizon endpoint changes during inspection.

The service reports counts, whether each expected owner has authenticated past deletion evidence and whether current ledger read reports the record absent. `IsQueueConfirmationProof` is hardcoded false: proving all staged historical tombstones still cannot guarantee atomic deletion or the absence of concurrent writers/newer versions. The shared Sync review now offers `VERIFY PER-OWNER DELETION HISTORY — READ ONLY` and shows a per-owner breakdown plus prominent no-retry/no-confirmation language. Both PWA and MAUI register the same Core service; no new Horizon parser or signing path was added. Missing hashes remain unresolved and no entry state is changed.

Remaining: ledger-enforced conditional version authority, cross-device race validation, full Testnet end-to-end deletion interruption scenarios, PWA/MAUI compilation, automated forensic fixtures, and optional stronger receipt bundling. Builds and tests were not run under user's previous preference.

## 2026-11-04 — harden per-owner delete journal evidence scope and current-state diagnosis

Refined the recent multi-owner deletion journal verifier. HorizonVaultDeletionInspector now requires the successful tombstone transaction receipt's *fee-paying root source account* to match the connected queued root; an HD child ManageData operation's source and a valid transaction hash are insufficient to bind the receipt to this vault.

VaultDeletionJournalReview now separates independent **historical** per-owner proof from an explicitly **non-atomic** current-state observation. Previously the journal aggregated CurrentRecordAbsent from a sequence of per-owner reads, so the result could mix snapshots captured at different ledger heights or across another device's concurrent writes. The Core review now makes a single final diagnostic inventory read via the connected IStellarLedgerRecords and IVaultLedgerManifestCopyInventory, requiring the same root before/after and checking for malformed/duplicate owner sets. It returns CurrentSnapshotAvailable, CurrentManifestOwners, and UnexpectedCurrentOwners (new owners absent from the staged immutable deletion intent), and reports absence only when both visible record and all manifest owners are absent. A failed current-state read is *unknown*, not absent. The queue revision, selected network, and unlocked session are still rechecked before exposing the result.

The shared VaultHybridSyncReview displays current snapshot availability, current manifest owner count and explicit unexpected-owner warnings; each historical proof stays separately available even if current inventory is unavailable. Nothing changes queued state, approves retries, or turns matching historical tombstones into atomic deletion proof. This remains a Testnet-only queued remote deletion diagnostic; Mainnet/custom update/delete still disabled.

No build/test/live Testnet was run per standing user preference. Validate compilation, receipt source binding, missing live inventory, owner-set changes and concurrent writers in the next acceptance cycle. Cross-device version-enforced mutation protocol is still the major production blocker.

## 2026-11-07 — guard the exact remote HD-v2 manifests at signing time

Implemented another fail-closed defense against cross-device stale writes in the shared PWA/MAUI HD-v2 signer. Previously Core compared the decrypted record version before staging network writes, but the final signed manifest operation did not recheck the bytes of the old HD manifest(s). A second device could replace or delete a manifest while the local client was uploading its encrypted chunks. For hybrid prepared UPDATE the C# writer now captures exact base64 manifest ManageData values from each old owner in the latest authenticated LoadChainAsync snapshot, forwards this bounded owner/name/value guard set through JsManageDataWrite and JS interop, and checks each owner again with server.loadAccount immediately before signing the primary manifest write. A moved record also requires the destination manifest key to be absent when the earlier snapshot had no copy there.

For a queued DELETE, the C# writer forwards the precise expected manifest value from each staged owning HD child with its tombstone. The signer re-reads the child immediately before each signed deletion and rejects a mismatch, preventing an older delete intent from deleting a manifest that changed since the transport's loaded snapshot. This byte guard complements, but does NOT replace, prior decrypted-record fingerprint checks, the durable signed deletion journal, the recorded signed hash, and explicit approval. It is confined to guarded hybrid UPDATE/DELETE via optional arguments; direct on-chain CRUD continues unchanged. No automatic retry is enabled.

**Security limitation:** The final read is a best-effort Horizon observation, not an on-chain conditional precondition. Ledger writes are not atomic across the read and the transaction, and multi-owner deletions remain multi-transaction, so the cross-device race is still a release blocker. Build and live tests were not run per prior user instruction. Next acceptance should exercise stale manifest overwrite, absent or occupied relocated target, interrupted journal progression, and native/Blazor JS interop value shape.

## 2026-11-07 — serialize HD-v2 sponsored writers using root account sequence ordering

The signer previously loaded the root and target HD child concurrently in `writeSponsoredData`. Since `TransactionBuilder(root)` uses the root account's sequence as the transaction sequence, parallel reads could observe an old child manifest and a newer root sequence. That allowed a stale manifest snapshot to be signed with a sequence that was fresh *after* a concurrent transaction, defeating the intended optimistic fence.

The signer now loads and validates the root account first, then reads the target child and all manifest guard owners. Every normal StellarVault HD-v2 sponsored write uses this root as the transaction source. A concurrent root-sourced transaction accepted after the root snapshot advances its sequence, so the in-flight signed transaction is rejected as stale; a transaction accepted before the root snapshot should be visible to the subsequent child reads and the manifest byte guard. This composes with the previous exact byte guards on hybrid UPDATE and per-owner DELETE. Guard owner G-addresses are also syntax-checked in JS before account lookups.

This is a **root-sequence serialization fence for compliant root-sourced StellarVault writers**, not a native Stellar ManageData CAS. A separate client can bypass it by signing directly with a child account or by using another transaction source; Horizon consistency and the ordered read assumptions must be validated. No automatic retries were introduced; existing uncertain-submission quarantine stays fail-closed. Mainnet queued update/delete remain blocked. No build, browser test or Testnet concurrency experiment was run.

## 2026-11-07 — hybrid file workflow: encrypted offline staging now shared by PWA and MAUI

A concrete gap was found: `StellarLedgerFileProtocol` and the legacy `StellarLedgerUploadCoordinator` already supported multipart SVFILE2 records and manifest-last publication, but this path was not integrated into the encrypted hybrid mutation queue. Generic record/folder Sync explicitly excluded `UploadFile`.

Added shared Core `VaultHybridFileWorkspace`. It reads at most 1 MiB from a caller-owned stream, creates a deterministic manifest-last upload plan with stable random record IDs, fingerprints the original bytes with SHA-256, and persists filename/content type/optional parent folder/original file bytes/fingerprint/all part+manifest IDs as one `UploadFile` mutation. The entire serialized intent is stored via the existing account/network-scoped encrypted queue adapter; no plaintext checkpoint is placed in localStorage or the filesystem checkpoint folder. On recovery the workspace verifies the fingerprint, ID bounds/uniqueness, reconstructs the exact immutable plan, and checks the manifest ID against the queue entity ID. Existing file part IDs are checked against every queued entity before new IDs are allocated. A pending local parent folder becomes a queue dependency; confirmed parent folders need no local edge.

Added shared `VaultHybridFileDraftEditor`. MAUI uses its native `IVaultNativeFileAccess`; PWA now registers a browser file-picker/export adapter and transient JS picker in `vault-v2-download.js`. Browser-picked bytes are transferred as a bounded base64 interop result into a zeroing stream; no browser localStorage/cache persistence is used. PWA VaultHome now exposes the same Hybrid Sync workspace components as MAUI's NativeVaultWorkspace, including offline record drafts, offline file staging, remote changes and Sync review.

Important scope boundary: this is **encrypted offline staging**, not end-to-end file Sync. The review explicitly marks the UploadFile mutation as not yet publishable by the manual coordinator. Remaining: per-part fee estimate and explicit approval, durable signed hash checkpoint for each part before broadcast, manifest-last publication checkpoint, restart/partial-upload reconciliation, authenticated complete-file verification, safe cleanup, and file deletion. Existing old upload checkpoint path remains separate until migrated. 1 MiB cap remains.

No build, tests, browser/MAUI runtime or Testnet transactions were run per standing user instruction. Do not call the file flow production-ready until those are exercised.
