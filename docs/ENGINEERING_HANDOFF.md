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

## 2026-11-07 — follow-up hardening for encrypted file staging

Reduced transient plaintext copies in `VaultHybridFileWorkspace`. StageAsync now calculates the multipart entry count from the bounded file length, allocates collision-checked IDs directly, and only reconstructs chunk draft strings later when `GetPlanAsync` is explicitly requested. The temporary byte-array copy used to serialize the encrypted queue intent is zeroed immediately; the in-memory mutation payload is cleared after the durable host queue accepts it; queue snapshots used for ID collision checks and plan reconstruction are cleared after use. StageAsync returns only a metadata summary, never the mutation object holding the serialized file bytes. File listing verifies the SHA-256 and exact expected ID count without materializing all part payloads.

No change to the release boundary: hybrid file staging is implemented; per-part approval, signed checkpoints, manifest-last publication, interruption recovery and historical full-file verification remain outstanding. No builds/tests/runtime or Testnet validation run.

## 2026-11-10: encrypted per-part journal and bounded read-only publication preflight

Extended the hybrid offline-file queue mutation model with optional SignedFileEntries (LedgerRecordId + fully signed 64-hex Stellar transaction hash). The new Queue.CheckpointSignedFileEntryAsync guards exact Submitting revision, reads the immutable staged file intent, verifies its byte SHA-256 and stable ordered part/manifest IDs, requires the next entry index and unique hash, and atomically persists the next checkpoint. Host IndexedDB and native filesystem queue CAS comparators now include the journal so a concurrent tab cannot erase or reorder publication history. The journal is encrypted within the existing per-account/per-network queue. No prepared hash is successful receipt evidence; never replay automatically.

VaultHybridFileWorkspace now validates and exposes the ordered journal in FileDraftItem with NextEntryId, marking malformed intents/journals corrupt. PreviewFirstEntryAsync performs read-only preflight for a Pending untouched file: require connected authenticated HD root, no conflicting/unresolved queue entries, dependencies already confirmed, validated file reconstruction, current remote manifest/part IDs unoccupied, remote folder present, and indicative fee/reserve calculation for ONLY the first planned transaction. It rechecks queue payload, account scope, epoch and connected signing root after the remote inspection. Shared PWA/MAUI file editor displays prepared hashes and a preview button with explicit NO SUBMISSION wording and clears stale preview on refresh or session change. This preview is not an approval snapshot; nothing signs, reserves or broadcasts.

**Scope remaining**: Hybrid UploadFile publication is still disabled. Wire per-part explicit approval, actual signed-hash callback in the shared JS HD-v2 adapter before broadcast, verified receipt/record evidence after each part, manifest publication last, aborted/interrupted continuation protocol and SHA-256 reconstruction on a clean connected device. Full PWA/MAUI build and Testnet acceptance remain unrun under user's standing preference.


## 2026-11-10 — Product scope decision: Hybrid files DISABLED

Per product owner direction, **Hybrid mode is records/folders only**. Direct on-chain SVFILE functionality is unchanged. File selection, encrypted staging, multipart upload, paid file-part quoting/broadcast and remote-file deletion staging are unavailable in Hybrid mode, on both PWA and MAUI. Shared UI removes the hybrid file editor and explains that file storage belongs to the direct on-chain path.

Shared Core `VaultHybridFilePolicy.RequireEnabled()` is called at the queue enqueue/reservation/file-hash-checkpoint boundary, upload Stage/Quote/Publish and file-deletion staging. Queue Ready/PrepareReview explicitly blocks UploadFile and DeleteFile, so no generic Sync coordinator can accidentally treat a legacy file mutation as publishable.

**Recovery of old drafts:** Existing encrypted UploadFile/DeleteFile queue entries are retained, never silently discarded/migrated. Historical read-only inspection and already signed upload confirmation remain available in Core for deliberate recovery, but fresh file publishing and automatic replay are blocked. The ordinary on-chain file path is NOT blocked.

**Scope/release:** Existing experimental hybrid file implementation stays in code behind a hard-disabled product policy rather than being removed, to protect old journals and enable an explicit future decision. Do not enable by local setting. This change deliberately was not built or tested per current user instruction. Continue implementing hybrid records/folders and host interop; reserve validation until implementation is finished.


## 2026-11-10 — Records/folders Hybrid implementation freeze candidate

Product scope and deliberate restrictions: see `docs/HYBRID_RECORD_FOLDER_RELEASE_SCOPE.md`. Hybrid files are disabled in both hosts/Core. Mainnet-capable Hybrid Sync covers explicit CREATE record/folder; staged UPDATE/DELETE signing is still Testnet-only due lack of a Stellar-enforced optimistic version CAS; multi-owner DELETE does not have a safe confirmation transition.

This implementation-only pass added: strict unlocked-session epoch/network binding in `VaultHybridDraftWorkspace` across create/revise/list/discard; corrupt local create drafts now appear as noneditable warnings in the shared editor instead of disappearing; `VaultHybridSyncReviewService` requires a matching unlocked session and connected HD root before fee quoting; `VaultQueuedCreatePolicy.ParentCandidates` aligns create/revise dependency resolution and ignores stale confirmed non-create mutations without dropping the original confirmed folder dependency; generic remote update preflight rejects reserved SVFILE payloads; remote conflict review and Sync messaging now describe the real Testnet-only policy rather than stale 'not enabled' text.

No manual builds, tests, CI inspection, device runs, or Testnet writes were performed for this pass, per user direction. Repository push triggers may independently run existing GitHub Actions. Do not call this production-ready; handoff is for focused validation of the scoped feature set. If results identify errors, return to implementation.


## 2026-11-10 — PWA validation and MAUI validation handoff

**PWA phase:** GitHub Actions run 42805633168 passed on the hybrid records/folders scope-freeze commit ec838139: Release builds for Core/shared UI/PWA, PWA publish and asset checks, JavaScript syntax, .NET crypto suite, Node regressions and release artifact packaging. PWA automated pipeline validation is green. **Actual Testnet network publish/reconcile, offline browser restart and multi-tab collision acceptance have not been exercised here**, so this is not production validation.

**Native transition:** The same workflow now compiles the `net10.0-android` MAUI host on main pushes, with host-specific APK artifact packaging and diagnostic logs. Android compilation passed on run 42810369042 and again with the native foreground-loss signer lifecycle changes on run 42810654822. MAUI uses the same Core services/shared Razor hybrid UI as PWA with native filesystem encrypted queue and the host's linked WebView HD signing bridge. Native lifecycle now sends an explicit foreground-loss signal through `VaultNativeForegroundLifecycle`, locks the device vault, hides native signer controls and disconnects the WebView signer; cleanup failure leaves the screen locked. This prevents an on-chain-only native signer from being left available merely because there was no local device session to lock.

**Runner troubleshooting:** Running all MAUI builds on push can require hosted-runner disk reclamation before `dotnet workload install maui-android`; a disk-quota failure was observed in run 42810820212. An experimental `--skip-manifest-update` fix failed to restore a required .NET 10 Mono host runtime package in run 42811029459; the workflow reverted to normal workload installation while retaining removal of unneeded Swift/CodeQL/PowerShell toolchains. Follow the newest workflow result for the current runner configuration rather than using the older failing run.

**Outstanding acceptance:** Native Android APK creation, installation and actual WebView crypto availability on emulator/device; explicit account/root authorization, local encrypted queue reopen/restart, foreground/background key cleanup, native file picker (direct on-chain only), real Testnet record/folder Sync with independently authenticated evidence. iOS, macOS and Windows builds are not certified by the Linux Android CI job. Hybrid file queue operations remain disabled, and Mainnet hybrid update/delete remains Testnet-only.


## 2026-11-10 — Android launch stuck on "Initializing StellarVault…"

A real Android install was reported stuck at the native host's initial `#app` placeholder despite earlier successful Android APK compilation. **APK compilation alone cannot validate Android WebView startup.** Source inspection found an incorrect native entrypoint: `src/StellarVault.Maui/AppRoot.razor` was rendering the browser-oriented `VaultHome Platform="NATIVE APP"` instead of the host-specific `NativeVaultWorkspace`. This is corrected in commit c775683. The native `wwwroot/index.html` now has a fail-visible, credential-free first-render timeout and missing `blazor.webview.js` loading report, commit a0b840a. Node native asset regression explicitly checks the native root component and fallback wiring, commit d1e5787.

The edit is **a likely startup fix, not yet a confirmed physical-device fix**. Validate on a clean installed APK from the newest successful Android CI artifact. If still showing `Initializing StellarVault…`, obtain Android `adb logcat` diagnostics for `chromium`, `blazor`, `WebView`, `dotnet` and `StellarVault` without sharing mnemonic data. Verify `wwwroot/index.html` / `_framework/blazor.webview.js` are served by the packaged BlazorWebView, the root selector `#app` mounts `AppRoot`, and root dependency injection succeeds. Do not downgrade security controls or re-enable Hybrid files to workaround startup.


## 2026-11-10 — Native MAUI UX redesign (Windows / Android / iOS)

After Windows device feedback that the UI looked like a web page and Android/mobile content lacked usable scrolling and accessible actions, the native BlazorWebView presentation was separated from PWA styling. Core ledger, encrypted queue, and signing services were **not forked**.

- `NativeVaultWorkspace.razor` now contains a three-destination native app shell: Stellar storage, local encrypted vault (including offline records/folders), and Hybrid Sync review/staged remote changes. The formerly stacked ledger/local/sync panels are no longer all rendered on the same page. Local consent/unlock is accessible from either local or sync destination; switching destinations does not approve transactions. Direct on-chain files stay under Stellar; Hybrid file operations remain disabled.
- `src/StellarVault.Maui/wwwroot/native-app.css` is loaded **after** the shared PWA stylesheet in the native host only. It supplies app-like surfaces, OS-friendly typography, responsive navigation, an independently scrollable `minmax(0,1fr)` viewport, touch targets, and safe-area bottom bars. Phone navigation and quick actions remain outside the scroll viewport; Windows/macOS/iPad use a navigation rail at tablet/desktop widths, and large Android tablets also use a rail.
- The always-reachable commandbar offers only existing safe actions: Connect/Refresh/Disconnect Stellar, Unlock/Lock device vault and navigate between local and Sync. It must not auto-submit any paid transaction or bypass the approval flow.
- `AppRoot.razor` passes MAUI `DeviceInfo.Platform` to the shared workspace, and Windows creates a resizable ~1180×800 desktop window with a 720×540 minimum.
- Extended native assets regression coverage asserts the startup component, native-only CSS ordering, scroll region, navigation and quick-action strip. An intermediate CI revision failed the old root element matcher; that test was updated to permit `HostPlatform`.

**Validation limitation:** The MAUI renderer remains BlazorWebView, not platform-native XAML/WinUI/UIKit/Compose components. The new presentation is deliberately host-adaptive, but a physical device/emulator must still verify touch scrolling, keyboard resizing, safe areas, orientation, native Windows DPI resizing and VoiceOver/TalkBack interactions. Continue from the newest Android/PWA pipeline result; do not claim visual/device acceptance solely from compilation.


## 2026-11-10 — Native task-focused UX additions and platform build gates

The original mobile/desktop layout redesign is extended with in-screen segmented navigation: My vault shows only **Device records** or **Offline drafts**; Sync shows only **Stage changes** or **Review & sync**. The persistent quick action "Review sync queue" selects the review segment explicitly, not the staging segment. A bottom command strip offers accessible Connect Stellar/Unlock vault (only when their ordinary prerequisites are met), Refresh/Disconnect, Lock and safe navigation; all existing explicit paid approval checkpoints remain in the shared UI.

Android `MainActivity` uses `WindowSoftInputMode = SoftInput.AdjustResize` to give the scrollable BlazorWebView space when the keyboard opens. A native static regression asserts the activity mode. Device testing remains necessary because Android OEM keyboard/window-inset behavior varies.

Added `.github/workflows/maui-windows-preview.yml`: build the .NET 10 Windows target on a Windows runner after native/shared changes, installing `maui-windows` and verifying an unpackaged native EXE. An Android APK build **does not** test the Windows navigation rail or WinUI runtime. iOS still requires an Apple runner and interactive device/simulator checks.

See `docs/NATIVE_UX_ACCEPTANCE.md` for Windows/Android/iOS behavior and device acceptance. The native UI is host-adaptive BlazorWebView, not independent XAML/UIKit/Compose implementations; preserve this distinction in release statements.


## 2026-11-10 — Android still hangs on 'Initializing StellarVault…'; native-first startup and terminal identity

Android device feedback confirmed the earlier AppRoot wiring fix did **not** resolve the startup hang. Do not call the incident fixed solely because CI builds APKs. The native host previously had about seventeen synchronous blocking script tags loading the bundled Stellar SDK, BIP39/HD logic and compatibility runtime **before** `_framework/blazor.webview.js`. That makes first render dependent on unrelated signing logic and can strand the placeholder if the WebView's script execution stalls.

Current fix: `src/StellarVault.Maui/wwwroot/index.html` exposes `stellarVaultNativeRuntime.load()` to load the same bundled, ordered JavaScript files on demand after the first Blazor render. The native `NativeVaultWorkspace.CheckRuntimeAsync()` calls this loader, then checks readiness. Ledger connect stays blocked unless readiness succeeds; the first navigation shell has no dependency on HD signing scripts. `MainPage.xaml` now contains an independent **MAUI XAML** startup layer with no JS dependency. `AppRoot.OnAfterRender` signals `NativeStartupStatus` to hide the layer. If nothing renders after 12 seconds, the XAML layer displays an actionable nonsecret native startup diagnostic. That diagnostic is intentionally broad, not a claim of the specific underlying failure. Native asset tests assert the lazy load, XAML signal, and rendered-root lifecycle.

The native-only `native-app.css` now carries the same StellarVault identity as PWA — phosphor green/copper accents, monospace technical microcopy, glowing vault marks, dark secure surfaces and diagnostic-style labels — but keeps independent mobile scroll, bottom navigation, touch-friendly controls and Windows/iPad desktop rail. Do not revert to the PWA stacked long page.

**Outstanding:** The Android hang still needs confirmation on a real device by installing the new successful APK artifact. If XAML timeout appears, inspect Android logcat around WebView asset loading/renderer crashes, MAUI exceptions and JS bridge creation, without printing mnemonic/keys. Build success does not prove render or runtime readiness.


## 2026-11-10 — Android startup second remediation / native visual continuity

User confirmed **Android still hung on "Initializing StellarVault…" after first AppRoot fix**. The earlier guess was not verified; subsequent source work isolates the startup path:

- Native host HTML no longer synchronously loads the Stellar SDK, BIP39/HD and full compatibility scripts before `blazor.webview.js`. `stellarVaultNativeRuntime.load()` loads the same packaged scripts only after the first Razor render; `NativeVaultWorkspace.CheckRuntimeAsync()` awaits load and then inspects readiness, gating signer connection on a complete runtime. This isolates visual shell startup from ledger runtime availability. Keep script ordering and preserve local-only bundled asset policy.
- `MainPage.xaml` overlays the WebView with a MAUI-owned, credential-free XAML loading panel. `NativeStartupStatus` is a singleton shared between `AppRoot.OnAfterRender(firstRender)` and `MainPage`. First render hides the XAML panel; after 12 seconds without it, the host displays a non-secret error. This diagnostic works even when the JS error handler cannot run.
- Native-only CSS now uses StellarVault phosphor green, terminal-style monospace micro-labels and vault marquees while retaining platform-responsive navigation, touch targets, safe areas and independent scrolling. Native shell is not a clone of PWA layout.
- Updated `tests/maui-ledger-assets.test.cjs` to assert lazy script loading, first-render startup signal, terminal style, and MAUI fallback. Constructor signature was changed to `new MainPage(_navigation, _startup)`; test matcher reflects both arguments.

**Important:** a passing Android APK compile is still insufficient. Install the artifact from a successful workflow on/after these changes, and observe whether the XAML panel disappears and the navigation renders. If it times out, collect filtered `adb logcat` messages around MAUI BlazorWebView resource resolution/renderer exception; do not request or print mnemonic or keys.


## 2026-11-10 — Android emulator timeout evidence and startup stage diagnostics

User-supplied Android emulator screenshot confirms the **MAUI XAML startup overlay reaches its 12-second timeout**. This proves `MainPage` was instantiated and the watchdog ran; it does **not** prove whether the native WebView was initialized, static assets resolved, the Blazor bridge attached or the large workspace instantiated. The screenshot itself contains no exception detail.

Changes on `main`: `AppRoot.razor` now performs an intentionally minimal, dependency-free first render. Only after signaling `NativeStartupStatus.NotifyFirstRender` does it mount `NativeVaultWorkspace`. This splits bridge/bootstrap failure from complex Razor workspace/DI failures. `MainPage.xaml` now wires the documented `BlazorWebViewInitializing` and `BlazorWebViewInitialized` events, and `MainPage.xaml.cs` shows a fixed, credential-free startup stage on the XAML overlay and logs it via `StellarVaultStartup` Android logcat tag. The timeout includes the last reached stage, instead of a blanket WebView guess. It does not log mnemonics, user content or exceptions. Native startup regression tests cover first render isolation and event wiring.

The Android CI workflow now inspects the APK ZIP entries to require bundled `wwwroot/index.html`, `wwwroot/native-app.css` and the locally packaged Stellar SDK. This catches missing build assets but **not** bridge execution failures. When testing the next successful artifact, report which stage the emulator displays, and (if still stuck) capture: `adb logcat -d -s StellarVaultStartup BlazorWebView chromium AndroidRuntime` (do not include mnemonic or vault data). Emulator system WebView must be installed and updated.

Do not call this fixed until successful **interactive emulator startup**, followed by Android offline vault and manual Testnet acceptance.


## 2026-11-11 — PWA-first installed-app UX redesign

The user is introducing the browser/PWA as the primary early-access preview while MAUI startup is still unresolved on the Android emulator. To make PWA behave as a proper installed application, the `/vault` route now uses a task-based app frame instead of stacking offline editing, Hybrid Sync review, queue recovery and the on-chain record browser into one page.

- Desktop/tablet: an application navigation rail with **Records**, **Offline drafts**, **Hybrid Sync**, and direct creation shortcuts. Phones: bottom navigation with safe-area padding and 44px+ touch targets. Shared top product chrome stays sticky. The unlocked vault content scrolls independently from the app chrome in browsers supporting `:has`; legacy browsers retain document scrolling. Tab destination `@key` replaces its content scroll so navigation does not inherit an unrelated scroll offset.
- Records remains the original on-chain folder/record browser with search, breadcrumbs, selected-item modals, file actions and explicit account information. No on-chain transaction or local queue logic was ported or reimplemented for the new navigation.
- Offline drafts shows only the existing encrypted local record/folder editor when consent and unlocked retention allow it.
- Sync has separate **Review & Sync** and **Stage remote changes** task choices. It retains original manually approved fee/ledger reviews, uncertain-submission warnings, and read-only recovery; no automatic publish.
- The `/add` and `/edit` routes are now focused editor screens with a return-to-vault link and Record/Folder/File selection for new items. Hybrid local file draft UI was removed in line with the existing Core prohibition; direct on-chain files remain available with individual fee approvals.
- Stale access-page language claiming offline drafts and manual Sync are unavailable was corrected; disconnected signing sessions remain clearly labeled `NOT SIGNED IN`, never incorrectly reported as internet offline.
- `tests/pwa-release-smoke.test.cjs` covers task destinations, on-chain browser retention, no accidental duplicate local editor, Sync components and focused editor route. `docs/PWA_APP_UX_ACCEPTANCE.md` records manual browser/mobile and security acceptance before distributing beyond a limited test cohort.

These commits are **presentation changes, not a completed security audit or a live Testnet E2E certification**. The PWA cannot be claimed production-ready from CI alone; verify actual mobile keyboard, privacy lock, installed service-worker upgrade, on-chain fee approval and offline/Sync interruption behavior before introducing it to users with real assets or secrets. MAUI core/host and Hybrid file policy remain unchanged.


## 2026-11-11 — Android emulator WebView bridge compatibility hypothesis

Latest emulator screenshot reports **"WebView initialized; waiting for Blazor bridge"**. This proves MAUI reached `BlazorWebViewInitialized` and the 12-second native watchdog; it does not prove that `_framework/blazor.webview.js` was successfully loaded or parsed. Minimal Razor root rendering was already isolated on a preceding commit, so do not resume UI redesign as a startup workaround.

Source inspection identified a material version mismatch: `Microsoft.Maui.Controls` was 10.0.30 but `Microsoft.AspNetCore.Components.WebView.Maui` was **10.0.0**. Both are now pinned to matched **10.0.120** stable .NET 10 packages (published 2026-10-21). This should remove a plausible bridge compatibility risk, but the user's emulator has not retested it. The native initialization callback now captures OS API level and `Android.Webkit.WebView.CurrentWebViewPackage.VersionName`, logs those public metadata through the existing credential-free `StellarVaultStartup` logcat tag, and shows them alongside the last reached stage on timeout. Test coverage checks package alignment and safe diagnostics.

There are public upstream reports of WebView bridge `Uncaught SyntaxError` / `Blazor is not defined` on older Android 11/12 emulators and System WebView builds (dotnet/maui#32860, dotnet/aspnetcore#53699). If the matched-patch APK still stalls, compare the emulator API level and installed Android System WebView/Chrome version and request narrow `adb logcat -d -s StellarVaultStartup chromium AndroidRuntime` output; neither the screenshot nor successful CI proves the user's emulator version. Prefer updating the emulator WebView or using a recent API 35+ emulator as an A/B test, not arbitrarily weakening Android or Blazor browser security.

**Unresolved:** Android interactive first-render confirmation and on-device Hybrid offline/transaction acceptance. Preserve PWA-first priority and signed-transaction safety boundaries.


## 2026-11-11 — Android Blazor bridge startup root cause and fix

New emulator logcat provided two decisive JavaScript errors from `https://0.0.0.1/_framework/blazor.webview.js`: **`window.external.receiveMessage is not a function`**, followed by **`Blazor has already started`**. Earlier startup screenshots were only reporting the absence of a first render; these logs provide a concrete bootstrap race explanation. The native host's `wwwroot/index.html` erroneously used `<script src="_framework/blazor.webview.js" ...>` **without** `autostart="false"`. With auto-start enabled, JS begins before native WebView bridge registration, so `window.external.receiveMessage` is missing; MAUI's subsequent controlled start then sees an already-started Blazor runtime. .NET MAUI's documented BlazorWebView host pattern is `<script src="_framework/blazor.webview.js" autostart="false"></script>`; MAUI controls the single start after native messaging is ready (see dotnet/maui#3644, Microsoft BlazorWebView docs).

**Corrective commit:** 328ec42c8. The host script now explicitly sets `autostart="false"` while retaining the existing credential-free diagnostic fallback and deferred Stellar SDK load. A native asset regression assertion checks that bridge autostart is false and no explicit `Blazor.start()` or WebAssembly/server bootstrap is included in the native host. No wallet cryptography, approval workflow, or local storage semantics changed.

**Required acceptance:** Wait for a passing Android CI APK built on or after this commit, install and launch on the Android API 36 emulator, verify first Razor render and native tabs, and then run local vault workflow and Testnet checks. The cause is now specifically identified but device resolution is **not yet verified**. Do not claim release readiness until interactive retest.


## 2026-11-11 — Customer-first MAUI + PWA UX cleanup after Android first-launch success

Android emulator screenshot now confirms the MAUI BlazorWebView and native shell **render successfully**, including the Stellar, local vault and Sync tab bar. This resolves the earlier blocking first-render symptom at the user-observed level; do not infer that wallet signing, transaction approvals or on-device offline workflows have passed E2E. The user identified a follow-up UX defect: implementation jargon ("ANDROID · PRIVATE STORAGE", "Check native runtime", SDK/mnemonic descriptions, marketing/status banners) was being shown as though the installed app were a developer console.

**MAUI `NativeVaultWorkspace.razor`:** primary screens now use task-oriented headings ("Welcome back", "Connect your vault", "My records", "My vault", "Sync"). Platform name, access-stage numbers, routine runtime diagnostics and successful ledger-operation logs are removed from the default path. The automatic credential-free runtime readiness check and fail-closed signer prerequisite remain in Core/UI: it runs on first render without a manual "Check native runtime" button and shows a short error with "Try again" only if initialization fails. The mnemonic entry is still required but labeled "Recovery phrase." A compact collapsed network choice replaces inaccurate instructions about a nonexistent native header selector; Testnet is default, Mainnet requires explicit acknowledgement of real XLM risk before native connection, and switching is disallowed while a signer or local vault session is active. Direct on-chain file flows, explicit fee estimate and approval remain unchanged. Local storage/security details are tucked behind accessible disclosure controls, with explicit storage consent still required; the native storage notice's stale "not enabled" statement was corrected. `native-app.css` preserves the terminal/phosphor identity but uses compact device-first spacing.

**PWA `VaultHome.razor`, `Vault.razor`, `AdaptiveShell.razor`, `Add.razor`, and CSS:** remove persistent WEB PWA platform badge, developer-looking header subtitles, excessive marketing copy, and the two-column unlock landing experience. Open into one centered secure unlock form (recovery phrase, choice of Stellar-only or explicitly opted-in encrypted device storage, optional passphrase). Technical explanations are optional; the early-access safety notice, selected public Mainnet acknowledgement, fee review and secret protections remain. Post-unlock Records, Drafts and Sync use concise action labels, with detailed warnings retained for unresolved submissions and transaction approvals. New-item screens use task-focused header labels.

**Validation:** Native static regression protects absence of the manual runtime button, automatic bridge readiness check and Mainnet explicit consent; PWA smoke tests were updated to assert semantic on-chain-only default rather than obsolete marketing text. GitHub Actions workflows trigger on main pushes, but visual acceptance on Android emulator/browser and exact commit CI outcome must still be checked before inviting preview users.


## 2026-11-11 — Minimal recovery-first UX and network icon (PWA + MAUI)

Customer requirement: installed app should open directly at recovery words, without welcome/connect-intro panels, while the network selector is a **small header icon**. Keep terminal/phosphor visual identity and use short task-oriented headings on the other screens.

- MAUI `NativeVaultWorkspace.razor`: brand header has an icon-only `◎` network menu with Testnet, Mainnet and Custom. Native custom network uses the same packaged `stellarVaultV2Network.probeCustom` JS discovery as the PWA; the detected passphrase and exact HTTPS Horizon URL are stored in the shared `VaultNetworkSelection` only after success. Changing the URL invalidates readiness, and local Create/Unlock and Stellar Connect are blocked while a custom network is undetected. Both hosts pin discovery to the exact requested URL and unlocked session epoch, discarding stale asynchronous responses. Testnet is default and Mainnet still requires an explicit real-XLM acknowledgement. The menu is visible but editing is disabled during a connected or unlocked session. The first Stellar page omits redundant heading, welcome/intro, network selector and technical diagnostics; the content starts with **Recovery phrase** and mnemonic inputs, followed by the existing persistent Connect action. Other native views use plain headings while retaining the actual fee approval and file recovery warnings.
- PWA `VaultHome.razor`: the existing fully functional network popover is now a compact icon-only header trigger, preserving Testnet/Mainnet/Custom and existing custom passphrase autodetection. Recovery phrase is the first form content, and optional local encrypted storage options and saved-account details live in collapsible disclosures. The recovery-word and fee/real-funds safeguards remain unchanged. The common `AdaptiveShell` header shows only the StellarVault brand, without an extra subtitle. On-chain browsing, record/folder creation, Sync and registry setup have shorter labels, and explanatory technical detail is subordinate to primary controls.
- CSS in both hosts scopes the compact network menu and keeps touch-friendly 40–44 px targets, accessible labels, safe-area awareness and keyboard navigation. Native and PWA regression tests now guard header discovery and the minimal entry form.
- This is a source implementation pass. Verify current GitHub Actions for *this* commit; older green builds are not proof of this revision. Run manual Testnet/Custom network discovery (including editing Horizon mid-request), Mainnet acknowledgement and both host device/browser navigation before offering access to real assets. Android startup was previously verified by the user's successful screen capture, but this redesign still needs an emulator/device visual pass.


## 2026-11-11 — Unified terminal visuals, responsive explorer and native mobile task pages

Following user screenshots, the next cross-host UI pass addresses four concrete defects:

1. **Mixed PWA card geometry / surface texture:** `src/StellarVault.Pwa/wwwroot/css/app.css` now applies one squared 2px CRT geometry and faint dot-matrix texture across terminal panels, records, folder tiles, app chrome, popovers and other card-like surfaces, respecting green/amber theme variables. Prior rounded card overrides are superseded. This is scoped to the PWA's adaptive shell; transaction warnings must stay visually distinct.
2. **Folders should be browsable like file storage:** `src/StellarVault.Pwa/Pages/Vault.razor` now renders a folder explorer tree in wide layouts and a familiar folder/object card grid in constrained layouts. Folder nesting is derived from the existing on-chain parent IDs with a visited-set/maximum depth guard. Selection, breadcrumbs, existing record read, local pending folders, edit/delete actions and fee approvals still use the existing workflow. CSS media and workspace container queries switch the presentation based on real pane width, not user-agent detection.
3. **Native on-chain record parity:** `NativeVaultWorkspace.razor`'s dedicated Add record screen now collects title, nested parent folder, username, URL, secret/content (masked), notes and tags, using the same `LedgerRecordDraft` data model as PWA. The reviewed transaction fingerprint covers *all* editable fields plus network and item kind, so changing metadata between cost preview and submission cancels the authorization. Native on-chain folder creation also has its own Add folder destination and uses the original fee/registry approval controls.
4. **Actual task destinations on small native screens:** MAUI ledger browse now has a directory/file grid on narrow layouts and a desktop folder tree on wide ones, with record detail on a separate screen and Back navigation. The on-chain Record, Folder and File forms are distinct screens, not tabs which accordion-expand inside a long list. Shared `VaultRecordWorkspaceView`, `VaultHybridDraftEditor` and `VaultHybridRemoteChanges` now separate listing from creation/editing/staging; the shared components benefit both hosts. In Hybrid mode file staging/upload/deletion remains forbidden; direct on-chain files are unchanged.

New Node regressions in `tests/maui-ledger-assets.test.cjs` and `tests/pwa-release-smoke.test.cjs` verify structural separation, metadata parity, fee-review fingerprint wiring, responsive tree/cards and terminal tokens.

**Verification caveat:** Static assertions and intermediate successful PWA/Android CI builds do not validate the latest revision until its workflow completes; manual Windows explorer width/keyboard and Android mobile interaction checks are still required. Do not claim the app can safely hold live customer assets before approval-path and emulator acceptance. Keep these requirements in `docs/PWA_APP_UX_ACCEPTANCE.md` and `docs/NATIVE_UX_ACCEPTANCE.md`.


## 2026-11-11 — Approved modern product UI implementation (PWA first, then MAUI)

The user explicitly chose the newer high-fidelity desktop and mobile StellarVault mockups over the previous terminal/CRT style. **Do not reintroduce dot-matrix, scanlines, sharp pixel borders or neon terminal visual treatments** as the default app identity. The current visual direction is modern, calm, clean, premium green/teal with rounded surfaces and accessible typography.

Implementation now on main:

- `src/StellarVault.Pwa/wwwroot/css/modern-app.css`, loaded last in the browser `index.html`, overrides the 70KB legacy CSS without changing shared Core operations. It contains modern design tokens, a light application frame and sidebar, responsive folder/file cards, accessible controls, sheet-like mobile record details, a desktop right-hand record inspector using existing modal semantics, and reduced-motion handling.
- `src/StellarVault.Maui/wwwroot/modern-native.css`, loaded last after native host and Razor scoped styles, makes Windows/Android/iOS mobile and desktop compositions visually consistent with PWA. Existing native scroll, foreground-loss lock, BlazorWebView bridge autostart=false, and safe-area navigation behavior remain.
- `src/StellarVault.Pwa/Pages/Vault.razor`: upgraded search and object/folder card content, added **Settings** with real network status, account info and local retention/Lock actions; mobile bottom navigation offers destinations with a floating New action (not a fake interactive button). Do not expose secret contents in cards; the record inspector continues masking content until explicit reveal.
- `src/StellarVault.UI/Components/NativeVaultWorkspace.razor`: fourth Settings destination with active network, disconnect and local-vault lock, using existing services. Separate Create Record/Folder/File screens, folder tree and record detail remain implemented.
- PWA and native Node smoke/regression tests assert asset ordering, consistent product theme, editor/navigation structure, and no regression to Android BlazorWebView autostart safety.
- `docs/MODERN_APP_DESIGN.md` describes the product design system and device acceptance.

**Verification caution:** Green builds from earlier stylesheet-only revisions do not certify the latest functional Settings/navigation commits; check current workflow outcomes. No emulator screenshots or real browser visual end-to-end tests were run by the connector. Modern visual refresh does not change production crypto/audit readiness. PWA is the initial preview product; do not turn on mainnet launch claims or re-enable Hybrid file sync merely because the UI looks production-quality.


## 2026-11-11 — Offline save versus failed Horizon refresh

Reported symptom: creating a record while disconnected displayed `Cannot load record: Failed to fetch`. Saving the encrypted local draft did persist the item, but the parent Vault immediately attempted `Ledger.ReadAsync()` and displayed `Ledger synchronization failed: Failed to fetch`, conflating successful local persistence with a failed remote refresh.

Changes:
- `VaultItemEditor.OnParametersSetAsync` does **not** load the Stellar ledger for new items. It takes the last known folder snapshot from `CachedFolders` and retains the chosen parent. Editing an already-published record still requires a trustworthy loaded remote record.
- `SaveOfflineDraftAsync` only enqueues the encrypted local mutation; after a confirmed enqueue it signals `OnDraftSaved` separately from the on-chain `OnSaved` callback. Post-save navigation/queue-refresh problems cannot rewrite a successful local commit into a failed-save status.
- `Vault.OnLocalDraftSavedAsync` opens Drafts and refreshes **only** the local pending queue. It deliberately does not call `Ledger.ReadAsync`, `loadChain`, Horizon, fee estimation, or submit.
- A failed user-triggered ledger refresh preserves the last verified in-memory records and shows a brief connection error rather than the vendor SDK stack. A registry lookup which fails because Horizon is unreachable is **unknown**, not `registryReady == false`; locally unlocked workspaces remain accessible.
- Add source regression checks for no implicit network calls on local create/save and no false registry redirect.

Security distinctions: encrypted local save is not proof of Stellar submission or sync. Unknown/uncertain signed submissions must still be reconciled before retry; this change does not relax approval or recovery gates.

Verification still required: .NET 10 build, live offline PWA save, refresh browser while offline, cached folder selection, queue persistence on device, reconnect/review/approval sequence, and platform-specific offline handling. Source assertions alone are not end-to-end verification.


## 2026-11-14 — Inspector/theme defects and proposed secret-key chain mode

UI fixes committed: the Settings Help action is now a proper themed navigation row; the explorer maintains its explicit list/grid state with `data-explorer-layout`; desktop inspector and record workspace scroll independently; desktop CSS selectors for `sv-view-grid` override legacy inspectors' forced flex lists; light/dark theme CSS normalizes PWA Sync/Drafts review panels and MAUI shared hybrid panels. The PWA stylesheet asset was cache-busted. A source regression check guards these requirements. Browser/device runtime and .NET compile validation are still outstanding.

Product request: offer two explicit vault unlock modes — existing BIP-39 SEP-5 HD vs imported Stellar secret key with a linked-list of independent accounts. The existing production-path IStellarLedgerRecords expects a mnemonic. Legacy connect/encryption code exists in `docs/legacy-ledger-reference/app.js` and `src/StellarVault.Pwa/wwwroot/js/ledger-compat/app.js`, but does not constitute a production-safe second onboarding path.

Detailed proposal: `docs/SECRET_KEY_CHAIN_OPTION.md`. Important: ManageData addresses are pointers, not signing grants. A single secret-key controller must be registered as a signer on each child or recover child keys from separately encrypted seeds. Prefer explicit root-signer authorization and reconciled atomic account/signer/link provisioning. At current 0.5 XLM base reserve, illustrative new child reserve 1 XLM account + 0.5 XLM pointer + 0.5 XLM signer = 2 XLM per child, exclusive of encrypted data subentries and dynamic transaction fees. Root sponsorship changes payer, not total reserves. Never conflate this with the existing HD registry which derives child keys and sponsors child account reserves.

Security gates: account-chain integrity/authentication, privilege thresholds/master-weight policy, lost child-key recovery, transaction receipt confirmation, concurrent tail extension, clean-device recovery, domain-separated encryption and audit. No second credential mode has been wired in this pass.


## 2026-11-14 — Dual vault credentials: first executable implementation milestone

**Product decision:** both BIP-39 mnemonic HD and imported Stellar S-key linked-chain vaults are independent first-class *planned* account-management architectures, using common encrypted records/folders, on-chain and hybrid service contracts where practical. Do not reclassify Secret Key mode as HD recovery or as a legacy-only migration.

**Code completed this pass:** Core `VaultCredentialMode`, `VaultAccessCredential`, `VaultSecretChainInspection` and `IStellarSecretKeyVaultDiscovery`; pure reserve/fee plan `VaultSecretChainProvisioningPlanner`; read-only JS `stellarVaultSecretChain.inspect`; strongly validated browser adapter; DI/assets for PWA and MAUI; credential selector + masked S-key entry on both hosts, with a **read-only** inspect action; native secret runtime readiness decoupled from HD. The new path never signs, writes, silently unlocks records or stores the imported key. S-key input is cleared after inspecting. HD's existing `IStellarLedgerRecords.ConnectAsync(mnemonic,...)` and signer path are untouched.

**Security gates implemented:** full Stellar StrKey checksum in JS; Horizon passphrase binding, bounded 64 nodes, loop detection, valid next-pointer G-address, root signer thresholds, rejection of zero thresholds and of HD-v2 registry roots; typed C# result validation; test fixtures for positive and failure cases. Accepts only literal `sv:next:v1` or `sv:next` pointer containing base64 ASCII G-address. Historical linked-pointer formats and encryption compatibility remain unverified.

**Not yet complete:** live S-key transaction signing, account creation + root signer authorization, atomic tail-pointer update, encrypted record protocol read/write, synchronization, independent receipt verification and clean-device recovery. The Secret Key mode must not be advertised as a fully unlocked vault yet. The live on-chain provider remains HD-only. Do not take new account funds from the user without the separate fee-approval and recovery milestone.

**Next milestone:** implement a versioned, authenticated secret-key chain registry and historical recovery proof, test funded child setup (with temporary child key and root signer, explicit master-weight rules) on Testnet, independently verify ledger inclusion, then implement the separate S-key ledger adapter behind typed mode dispatch. Preserve offline queue and native/PWA feature parity only after all mutation pathways support it.

**Verification:** JS source parsed and mock inspection cases exercised in a V8 isolate; Razor tags and DI/source wiring manually inspected. No dotnet build, native runtime, live Testnet or end-to-end recovery test could be run from the available execution environment. A regression test file was committed but has not been executed through the repository test runner.


## 2026-11-14 — Secret Key linked-account vault Phase 2 implementation batch

User explicitly selected **two independent first-class account-management architectures**: mnemonic/SEP-5 deterministic HD and imported Stellar S-key / independently generated linked accounts with root public key as an authorized signer. The second path is a distinct source protocol; no S-key is interpreted as a mnemonic and no old HD vault is rewritten.

**Implemented, committed:**
- `IStellarSecretKeyLedger` in Core, `BrowserSecretKeyLedger` host adapter and `vault-secret-ledger.js` versioned independent on-chain engine. Same adapter/runtime packaged and DI-registered in PWA and native MAUI. Existing HD `IStellarLedgerRecords` writer is unchanged.
- New S-key record format: domain-separated HKDF/AES-GCM encryption, root/network/record/generation AAD, 47-byte binary `skh:` commit envelope containing ciphertext length/count/SHA-256 digest, and 64-byte `skc:` chunks. A record mutation is one atomic transaction at most 90 operations; its fee/reserves and changes are explicitly approved.
- Quote/submit split with exact one-shot quote token, 120-second expiry, rerun current network identity/fee/base-reserve checks, stale-record/chain preflight, and fail-closed uncertain-outcome handling. Current snapshot and decrypted record are checked after response. **This is not independent historical receipt verification or a Stellar-native CAS.**
- Account-chain extension via one reviewed transaction: new random child, root-funded creation, child SetOptions root authorized signer + masterWeight=0, positive signer thresholds, tail pointer, and optional tail reserve top-up. Child master key is needed only for bootstrap signing and best-effort cleared afterward. A new record on a child receives any required reserve funding in the *same* transaction as its data writes.
- Shared `VaultSecretKeyWorkspace.razor` offers authenticated on-chain record/folder create, read, update, delete, nested folders, list/tile view, reveal-on-demand, account inventory, explicit chain extension and fee/reserve approval. PWA `/secret-vault` route and native embedded workspace share UI and Core types. Mobile details are a bottom sheet and editor is full-screen. Native Hybrid Sync does not route S-key mutations through the HD signer.
- S-key runtime in both hosts revoked when backgrounded/closed; pending quote ciphertext and temporary child signing keys cleared on cancellation/disconnect. Invalid/corrupt records and historical `svh:/svc:` storage fail closed; other pre-HD vault formats have **not** been reconstructed or migrated.
- `tests/secret-key-ledger.test.cjs` holds mocked SDK/Horizon scenarios for encrypted CRUD, approved child provisioning/reserve topup, clean-session recovery, invalid/corrupt records, mainnet gate and one-time quotes. Extra standalone in-memory V8 mock exercised the open/create/update/delete/expand/create-on-child sequence: five simulated submissions and recovered contents verified. Source JS syntax and relevant Razor tag structures checked.

**Unsupported / blockers:** no full binary file upload/download or high-capacity staged multi-transaction writer for this mode; no offline encrypted Secret Key drafts or hybrid sync; no verified old pre-HD data migration; no authenticated independent historical receipt proof; concurrent writer conflict strategy is inadequate for Mainnet; no real external security audit or clean-device Testnet trial. Mainnet mutations explicitly blocked at JS quote/submit. Node regression test files are committed but were not executed by their actual test runner here; `dotnet` is unavailable locally, and no MAUI/PWA device rendering was executed. **Do not call Secret Key mode production-ready, fully feature-parity, or use it with real funds.**

Follow-up priorities: (1) run .NET 10 builds and both Node suites, (2) perform controlled Testnet funding and receipt/historical verification, (3) implement signed-chain registry & recovery and conflict control, (4) integrate Secret Key encrypted local/offline Sync into shared Core safely, (5) file streaming/multi-transaction authenticated commits, (6) browser/Android/iOS UI acceptance and security review. Keep in-project docs, release gates and readme consistent.


## 2026-11-14: reusable account and record-type contracts (source-only)
- New `VaultAccountStrategyFactory` and `IVaultAccountProvisioningStrategy` explicitly route account provisioning and discovery by blockchain + strategy. They do not hold secret material and are not yet registered as live HD or linked-key providers.
- New `VaultRecordTypeFactory` introduces stable logical types (text/file/folder/credential/unknown), safe unknown-type behavior, and historical MIME discrimination; shared text and binary workspaces now use the legacy classifier. Existing stored data formats remain unchanged.
- Added `docs/architecture/pluggable-vault.md` defining Stellar/EVM boundaries and versioned future share/import/export contracts. No EVM adapter, sharing, import/export, or live record migration is implemented.
- Work is on branch `architecture/pluggable-vault-records-and-chains`. Builds, tests and device validation were not executed; do not claim production readiness. Preserve existing deny-by-default sync verification and S-key Mainnet restrictions.


## 2026-11-14 continuation — typed local write integration and safe account read model
- Both hosts now register `IVaultRecordTypeFactory`; shared text and binary workspaces use it before indexed encrypted writes. Existing ciphertext/index layout unchanged. Unknown/unregistered types and absent binary MIME are rejected on creation; legacy MIME catalog classification retained for reads/deletes.
- New shared `VaultConnectedStellarAccountInventory` maps explicit credential mode to the already-connected HD or linked S-key ledger inventory; no mnemonic/S-key extraction, new transaction submission, or signer-chain proof. This is intentionally separate from the still-unwired `VaultAccountStrategyFactory`.
- EVM is explicitly last priority; do not implement an EVM adapter before Stellar production blockers, record/file parity, sharing and import/export foundations.
- All changes are source-only on `architecture/pluggable-vault-records-and-chains`; no build/tests or device validation run. Review for compile/runtime behavior and preserve existing Mainnet and sync safety gates.


## 2026-11-14: Stellar strategy and typed ledger integration
- Registered concrete HD and linked Stellar account strategies in PWA and MAUI, supporting explicitly selected, connected, read-only inventory inspection. Account creation and per-node discovery still fail closed until approved transaction and chain proof integrations are complete.
- Linked-key workspace refresh and post-submit checks inspect account-count/tail through the selected strategy factory. The HD and secret-key ledger transports remain separate.
- HD ledger draft estimate/publish/update and linked-key quoted drafts now validate logical credential/folder types using the same record factory; on-chain wire formats are unchanged.
- Local binary files with MIME text/plain are rejected because the historical encrypted catalog lacks an explicit file-vs-text discriminator.
- No builds, tests, EVM implementations, sharing or import/export performed. Source-only draft PR; preserve Mainnet and Sync safety restrictions.


## 2026-11-14 continuation: explicit reviewed linked-account expansion in shared Core
- Introduced scoped `VaultLinkedAccountExpansion` and registered it in PWA + MAUI. Linked-key UI now uses shared `PrepareAsync`, `SubmitApprovedAsync`, and `CancelAsync` for **chain expansion only**. Record create/update/delete retain the existing dedicated quote/submit transport.
- Expansion keeps exactly one outstanding quote per coordinator, binds the entire reviewed quote including fees/reserves, current root, selected network and credential scope (custom passphrase digest namespace), and checks connected chain count/tail before submission. An explicit user checkbox is still required; no automatic network calls are triggered by a factory CreateNext request.
- Consumes pending expansion intent before transmission; if submission or subsequent connected-state verification fails, treat status as uncertain and reconcile before retrying. On success checks that connected account count increased by exactly one with a changed tail; this **does not** independently prove signer authorization, full historical registry or consensus.
- Linked workspace revalidates selected factory inventory after a mutation and avoids claiming independent historical verification from a transaction hash.
- Current limitations: only in-memory per-scoped-session gating, no cross-tab/process coordination, durable in-flight journal or authenticated historical child discovery; connected session verification still relies on transport. Mainnet restrictions are unchanged. No .NET build/tests, real Testnet trial or device verification executed. EVM remains last priority.


## 2026-11-14: storage-driven linked-account capacity rollover
- Corrected product behavior: a user requests to save a record/folder, not an extra account. Existing linked ledger JS `plan(create)` now emits `SV_ACCOUNT_CAPACITY_REQUIRED` when its target tail would exceed its subentry limit. `BrowserSecretKeyLedger` converts that exact create-only marker into typed `VaultAccountCapacityRequiredException`.
- New shared `VaultLinkedRecordWritePlanner` validates logical record type, requests the existing create/update quote, and on create-capacity exhaustion routes into `VaultLinkedAccountExpansion.PrepareAsync`. Both hosts register the planner. The linked workspace no longer presents manual Expand account chain button.
- The first approval creates and verifies additional linked-chain capacity; only after connected chain inspection is the retained in-memory record draft re-quoted for a **second explicit approval**. No silent save, second submit, retry or secret persistence. Cancelling the first quote leaves the original editor draft available. If re-quote fails, record remains unsaved, editor draft remains in memory.
- Limitations: this capacity signal only covers the current *create* path's 1000 subentry limit; updates stay pinned to their original account. Insufficient reserves, oversized atomic record operations, full chain limits, concurrent sessions, and off-network capacity failures remain explicit errors. This is a reviewed two-transaction workflow, not atomic account-create-and-record-save. Durable cross-tab/process state and verified historical signer provenance are still missing.
- All source-only changes on the existing draft PR. No dotnet build, Node test runner, real Testnet, PWA, MAUI or security review executed. Mainnet write guard unchanged. EVM last priority.

## 2026-11-14: linked rollover failure safety and tail-link headroom
- Linked JS planner now reserves one subentry on the active tail for the future sv:next:v1 link during new-record planning. At 999 occupied entries, another new record requiring an entry triggers a reviewed account rollover; an already full 1000-entry tail cannot be extended using the existing pointer protocol and fails closed with manual recovery guidance.
- The planner checks minimum transaction operations and impossibly large fresh-account entry requirements before deciding to provision more capacity. Updates never silently relocate their historical owning account. Temporary child signing keys are generated only after funding/capacity preflight.
- Failed/ambiguous transaction submission sets an in-memory needsReconciliation latch in the JS ledger session; no further plan/submit on that connected session. Linked UI similarly blocks mutation actions after ambiguous outcomes while allowing read-only Refresh. Refresh does not clear the latch. Closing/reconnecting is not proof of reconciliation, and there is still no durable cross-session journal or independent on-chain proof.
- Updated Node regression scenarios: create-capacity signal, update pinned to original account, reserved-tail pointer rollover, saturated-tail fail-closed behavior, and network timeout quarantine. These are committed test sources; they were **not executed** here. No .NET build or device Testnet validation was performed.
- Open P0: durable transaction intent/recovery across reload, cross-tab and device coordination, authenticated signer/provenance verification, explicit read-only reconciliation workflow, comprehensive file and hybrid path parity.


## 2026-11-14: read-only uncertain linked submission diagnostics
- Secret Key ledger JS retains a volatile public-only uncertain submission snapshot at network submission: operation kind, root and target public accounts, optional record ID, exact network passphrase and optional transaction hash. No plaintext, seed, keys or ciphertext; returns a defensive copy. Cleared on confirmed connected-state success or session close, not on read-only refresh.
- IStellarSecretKeyLedger now exposes InspectUncertainSubmissionAsync with a validated C# browser implementation, and the linked UI presents a read-only diagnostic panel when a submission is uncertain. Writes remain blocked; no retry or automatic clearance path is supplied.
- Added regression test source cases for public-only snapshot, defensive copy, absent snapshot before submission, uncertain timeout write quarantine and successful clearing. Node/.NET/MAUI/PWA builds and Testnet flows were not run.
- This is NOT durable recovery: all volatile details disappear on session closure. Historical authorization/content proof, secure opt-in durable intent and cross-device coordination remain unimplemented. A transaction hash is not independent proof of record commit.


## 2026-11-14 continuation: read-only linked recovery assessment
- Inspected existing encrypted pending mutation queue; it requires `VaultSessionManager` unlocked local-vault key and `VaultDeviceRetentionGate` explicit encrypted local storage consent. The independent S-key session does NOT currently share that key lifecycle. Never reuse the queue for secret-key recovery without a dedicated authenticated key binding, scoped encryption, and opt-in durability design.
- Added `VaultLinkedRecoveryInspector`, returning a non-authorizing `VaultLinkedRecoveryFinding` from the current connected S-key ledger inventory: for expansion, whether expected child is current tail; for create/update/delete, whether current record ID visibility matches the operation. This service is strictly read-only and never clears an uncertain-submission latch or authorizes retry. It does not prove exact revision, transaction inclusion, signer chain, or source of observed state.
- Registered recovery inspector in both PWA/MAUI and surfaced its observation through the existing read-only uncertain-submission UI. No EVM, broadcast or storage-format changes. Existing device persistence policies remain unchanged.
- Durable cross-restart recovery journal is still NOT implemented; it requires explicit consent, key lifecycle integration, independent historical operation proof, and an authenticated conflict-safe transition to clear quarantine. Builds and tests have NOT run; do not merge as production-ready.


## 2026-11-14 continuation: intra-runtime quarantine and independent advisory receipt read
- JavaScript linked-ledger transport now retains a PUBLIC-ONLY unresolved-submission tombstone keyed by root G address and exact network passphrase across session lock/reopen in the *same JS runtime*. It blocks new plans and submits on reopening until that runtime ends. No secret, quote, encrypted payload, seed or record body is retained by the tombstone. Re-opening UI reads diagnostic and fail-closes writes. This does NOT survive a page reload, WebView restart, another tab or another device.
- Expanded volatile VaultSecretSubmissionDiagnostic with HorizonUrl from the verified signing session. Browser C# validates HTTPS origin, root, kind, IDs and network passphrase. Added IVaultLinkedReceiptEvidence and HorizonVaultLinkedReceiptEvidence; the latter performs read-only Horizon network-passphrase verification, checks a successful historical receipt matching the recorded hash, then requires expected root as source. The result is *advisory only*: no operation-body, signer-policy, encrypted-record revision, network consensus or durable intent proof.
- VaultLinkedRecoveryInspector exposes this advisory receipt next to current linked-account/record observations. UI shows distinct historical receipt and live-state sections and NEVER clears the write quarantine on the basis of either. Shared adapter registered in PWA and MAUI; native csproj explicitly links its file.
- Added Node test source for lock/reopen tombstone, updated public-only diagnostic fields and static read-only-adapter registration checks. Tests and .NET builds were NOT run in this session; run them and inspect host parity before merging.
- P0 still missing: encrypted opt-in durable intent journal with independent S-key key lifecycle, cross-tab/device arbitration, authenticated transaction operation verification, complete signer authorization and exact record revision proof, and manual reconciliation authorization to unblock. No EVM work.


## 2026-11-14: cross-session linked ledger safety pass
- Verified the existing Secret Key JS transport and Node test harness sources. `vault-secret-ledger.js` now checks unresolvedByVault in BOTH plan and submit for any already-open session sharing the exact `(root, network passphrase)` scope. A final check immediately before `submitTransaction` prevents a concurrent session that passed the early check from transmitting after another session began a potentially uncertain submission.
- The new-record planner now fails closed on an already saturated 1000-entry tail rather than emitting a generic account-capacity signal that could lead the coordinator to attempt impossible expansion. Rollover is offered only while a signed `sv:next:v1` chain pointer can still be created.
- Updated `tests/secret-key-ledger.test.cjs`: saturated-tail expected failure, second previously-open session blocked after uncertain submission, and two simultaneous approved submissions where at most one reaches the transport. Tests are source cases, NOT executed; GitHub reports no CI statuses or workflow runs for latest commit. Local Node v22 is installed but network access to the repository was unavailable.
- The cross-session guard is per JS runtime, NOT durable across full page reload or separate devices. No transaction replay, new signing bypass, EVM work, or Mainnet enablement. Continue toward authenticated durable journal, independently verified historical operation bodies, native parity, and Testnet validation.


## 2026-11-14: capability reuse audit — correct previous 'remaining' reports
- The user correctly identified that several items described as missing already exist. Read `docs/architecture/capability-reuse-audit.md` before implementing another queue, sync engine, file chunker, folder planner, receipt reader, or shared host component. Explicitly distinguish mechanism already implemented from independent S-key wiring, policy enablement and production validation.
- Confirmed existing HD/hybrid primitives: encrypted indexedDB/native CAS `IVaultPendingMutationStore`, `VaultPendingMutationQueue`, local consent and session gate, `VaultManualSyncCoordinator` plus fee approval guard, `VaultHybridSyncReviewService`, folder dependency planning, binary upload staging and chunked on-chain file protocol, historical Horizon evidence and shared PWA/MAUI UI. Many remain bound to HD sessions; hybrid file publication has a policy disable and MUST NOT be silently activated.
- Actual source refactor: new `VaultStellarDraftClassification` wraps existing `IVaultRecordTypeFactory` and is used by BOTH HD `VaultLedgerRecordWorkspace` and S-key `VaultLinkedRecordWritePlanner`. Both hosts DI-register it. Removed unused duplicate record-factory dependency from S-key UI. No wire/protocol change.
- Avoid reinventing durable journal/approved sync for S-key: **reuse existing CAS/approval/folder/file mechanics** after introducing a separate authenticated S-key storage key and protocol adapter, honoring explicit local-consent and independent account semantics. Historical HD registry verification cannot simply be treated as S-key proof.
- All changes remain unbuilt, untested and unmerged draft branch. EVM remains final priority.


## 2026-11-14 continuation — refactor existing approval freshness for both Stellar strategies
- Introduced account-neutral `VaultApprovalScopeGuard` using exact root address + network scope + review age. Rewired existing `VaultSyncApprovalGuard.RequireFresh` to delegate those rules while preserving HD queue draft digest and pending-state checks. Linked `VaultLinkedAccountExpansion` now uses that same scope+freshness rule and retains exact quote equality and independent S-key transport. No new queue, network write or EVM integration.
- Existing S-key JS transport quote expiry is 120 seconds; C# expansion approval now independently refuses a quote older than two minutes. Existing S-key create/update/delete quote approval remains in its transport (not forcibly migrated into the HD queue).
- Added static Node regression coverage in `tests/secret-key-ledger.test.cjs` for both approval consumers, strict freshness, unchanged linked exact quote comparison and lack of signing APIs in shared guard. Tests and .NET build were NOT executed.
- Read `docs/architecture/capability-reuse-audit.md` before making new subsystems. **Real gaps**: S-key-specific encrypted local key lifecycle, queue protocol adapter and historical S-key operation proof, not absence of encrypted CAS, folder planner, chunker, approval guard or receipt reader.


## 2026-11-14 continuation — shared encrypted queue review and S-key read-only adapter
- Refactored HD hybrid read-only review onto new `VaultScopedQueueReview`, itself using EXISTING encrypted `VaultPendingMutationQueue` and `VaultHybridFolderSyncPlan` rather than creating another storage engine. Captures and rechecks local unlocked account, session epoch, exact credential-network scope, custom network fields and explicit device retention consent. The HD mutation preflight, quote and submission paths remain as before.
- Added `VaultLinkedQueueInspection` for the independent S-key ledger: checks connected S-key root, observes chain before/after reading, requires matching separately unlocked local encrypted vault+consent, and returns only aggregate queue/folder information with `CanPublish=false`. **Not a S-key hybrid Sync implementation**, and not surfaced in linked UI. A coincident G address is not proof an HD-format draft belongs to the S-key protocol.
- Both hosts DI-register both Core services. Added a static Node regression source case for guards, no submission and host registration. Not compiled or executed. Preserve policy-disabled file publishing. Next: strategy-tagged authenticated encrypted intents and a genuine linked-key quote/verification adapter atop existing queue CAS, no silent cross-strategy draft migration.


## 2026-11-15: shared queue strategy origin — compatibility and isolation
- Added `VaultMutationStrategyPolicy` with explicit `stellar-hd-v2` and `stellar-secret-key-chain` origin IDs. `VaultPendingMutation.OriginStrategyId` is optional at the **end** of its record signature: old encrypted JSON entries without it continue to mean legacy HD. Invalid values fail closed, never silently interpreted as S-key.
- Both encrypted queue store adapters now validate the decoded origin, bind it to CAS comparison and refuse in-place origin modification. Existing HD queue Enqueue stays HD-only; HD approval guard includes origin in its digest and explicitly refuses linked entries; HD sync review rejects mixed-origin entries. This deliberately does NOT enable S-key local persistence or publication.
- Linked read-only queue inspection now reports separate legacy HD and tagged linked counts, with only tagged linked data in its operation counters and `CanPublish=false`. This is a discriminator, not an S-key ownership proof. Imported S-key signing session, encrypted local-vault session, and signer authorization remain independent.
- Regression test source assertions committed but not executed. Needed next: authenticated independent S-key local encryption key lifecycle with explicit device-storage consent, then reuse existing CAS queue and strategy-specific approved submit/verification adapter. Mainnet mutation still blocked. EVM last.


## 2026-11-15 — queue-key refactor and missing origin revision safety
- Corrected `VaultPendingMutationQueue.SameDurableRevision` to compare `OriginStrategyId` (previously only both encrypted stores' CAS comparers included it). HD-bound queue transition paths now refuse linked-origin entries while mixed-origin entries remain inspectable read-only. `ReviewReadyAsync` omits linked entries; `PrepareReviewAsync` marks them explicitly blocked. HD queue enqueue continues to reject linked-origin writes.
- Extracted `IVaultQueueKeySession.ExecuteAsync(accountId, networkId, operation, ct)` plus its default `VaultLocalQueueKeySession` adapter wrapping the existing unlocked local `VaultSessionManager`. Both encrypted PWA and native queue stores now delegate temporary encryption-key access to the interface and continue to enforce `VaultDeviceRetentionGate`. Host DI provides only this HD/local implementation for now, so the current encrypted queue file formats, AES-GCM AAD, CAS and key lifecycle remain intact.
- S-key-local encrypted queue access is **not implemented**; a strategy-specific provider must have its own credential authorization, key lifecycle, consent and storage namespace before activation. New static Node regression source checks origin isolation and store/key DI; unexecuted. No .NET compile or device Testnet tests run. Do not label this independent S-key hybrid Sync complete.


## 2026-11-15: single-snapshot shared queue review
- Corrected a consistency gap in `VaultScopedQueueReview`: it previously called `VaultPendingMutationQueue.PrepareReviewAsync` and `ReviewAllAsync` separately, reading the encrypted queue twice. A concurrent CAS transition could produce a readiness report inconsistent with the folder graph. Extracted the existing HD queue readiness logic into pure Core `VaultPendingMutationReviewPolicy.Build(all)`. `VaultPendingMutationQueue.PrepareReviewAsync` now delegates to that same policy, and the shared scoped review calls `ReviewAllAsync` exactly once then builds both readiness and `VaultHybridFolderSyncPlan` from the same authenticated list.
- HD readiness rules (blocked file publication, unresolved/conflict counts, dependencies, linked-origin fail-closed) remain identical. The S-key linked read-only adapter continues to report `CanPublish=false`. No new queue, no unconsented local storage, no imported S-key signing and no EVM changes.
- Updated existing Node static regression assertion that referenced the removed double-read and added a source test for single-snapshot reuse. Tests were **not executed**: this environment has Node v22 but no local repository checkout and cannot reach github.com for clone; no dotnet SDK was found. GitHub CI status should be checked separately before merging.
- Next substantive milestone: derive and authorize a distinct imported-S-key local encryption-key session under explicit retention consent, isolate its authenticated storage namespace, then reuse existing CAS queue and folder planning for reviewed linked writes. This remains a separate, independent vault mode—not another key for an HD vault.


## 2026-11-15: linked expansion preflight and lock recovery fixes
- The previous linked expansion path could leave a stale `VaultLinkedAccountExpansion._pending` review after account/network/quote preflight failed. The shared Core now discards that unsubmitted intent and cancels the JS quote on preflight failure, distinguishing `VaultLinkedExpansionPreflightException` from a genuinely uncertain transport call. The no-retry boundary is still immediately BEFORE `ledger.SubmitApprovedAsync`; nothing after that boundary is reclassified as safe or automatically retried. Cancellation during preflight similarly drops the quote and propagates cancellation.
- The shared linked UI handles the typed preflight failure without falsely claiming that its particular expansion was broadcast; it refresh-gates further writes and checks the ledger's public-only unresolved diagnostic. If a sibling session has an unresolved attempt, writes remain quarantined. The JS `inspectUncertain` now reports a vault-wide same-runtime public-only tombstone, not just its own session field, and returns a defensive copy.
- `VaultLinkedAccountExpansion.AbandonUnsubmittedAsync` is an idempotent lock/disconnect cleanup for never-broadcast Core expansion reviews; the linked UI invokes it before disconnecting even when an earlier exception discarded the visible quote. It cannot clear post-submit uncertainty because Core already consumes `_pending` at the transport boundary.
- S-key JS record planner now reserves the last `sv:next:v1` ManageData entry on **tail updates** as well as new creates. Updates that would consume that slot fail closed and require a separately reviewed migration protocol; no automatic rollover of existing records.
- Node test source includes same-runtime sibling inspection, headroom on tail updates, preflight-vs-transport separation, and abandoned quote cleanup. These tests were **not executed**; no .NET or Testnet verification. No new queue, account strategy, plaintext local storage or EVM work.
- Still outstanding: encrypted consent-based durable S-key recovery/queue key, historical operation/authorization verification, complete linked file and offline sync adapter and production QA. Existing HD/hybrid queue/file/folder primitives should be reused; see `docs/architecture/capability-reuse-audit.md`.


## 2026-11-15 — S-key separate local key/session and encrypted store adapter

- Added `IStellarSecretKeyLedger.ConnectedNetworkPassphrase` and C# browser bridge lifetime management so independent local-key unlock can reject mismatched root or network. The unavailable ledger returns null.
- `VaultLinkedQueueKeySession` is a dedicated S-key in-memory encryption-key source. It checks explicit device-retention consent, the connected independent signer/chain, exact selected network passphrase, Argon2id-wrapped credential lifecycle, local passphrase and key zeroization. It reuses `VaultPassphraseLifecycle` but **not** the HD `VaultSessionManager`; no S-key seed or HD mnemonic is persisted in credentials. Key use is bounded to `secret-chain:queue:v1:` namespace and in-flight context revalidation.
- Browser and native credential adapters reuse existing atomic wrapped-key storage under separate S-key account/network namespaces. Browser and native read-only queue snapshot adapters reuse existing AES-GCM encrypted queue stores but with the dedicated S-key key source and prefixed queue network scope; native uses `vault-secret-chain-pending-queue`. The linked inspector no longer reads the HD queue and does not authorize any publication or migration. Both hosts DI-register all pieces.
- Lifecycle zeroization implemented in PWA privacy/lock/background/dispose, MAUI foreground loss/device lock/disconnect/dispose and linked UI lock. `CanPublish=false`; no S-key encrypted mutation enqueue, manual Sync, file publication or automatic replay is enabled.
- Added `.github/workflows/linked-vault-validation.yml` with Node linked ledger tests and .NET 10 Core/UI/PWA build+crypto test steps, plus failure-log artifacts. Workflow metadata currently reports failures; connector doesn't expose useful job output. Source-only regression checks were expanded. **Builds/tests are NOT verified passing; do not merge as production ready**.
- Next: diagnose failing CI jobs; add consent-driven local key enrollment UI only when the S-key encrypted draft staging/review workflow is usable; reuse the existing queue CAS/state machine and folder graph with protocol-aware linked ledger quote/receipt verification. No new parallel queue engine. EVM last.


### 2026-11-15 final linked-key access hardening
- `VaultLinkedQueueKeySession.ExecuteAsync` now requires a live `IStellarSecretKeyLedger.InspectAccountInfoAsync` before AND after a local encrypted queue key callback. A cached C# `IsConnected` flag is insufficient after JS `revokeAll`, browser tab suspension or native foreground loss; the live JS signer and stable linked account count/tail must both remain verifiable. If Horizon is offline, this currently fails closed; **true offline S-key use requires a separately authenticated local signing-proof lifecycle and is not yet enabled**.
- PWA preview release workflow now runs the linked S-key Node regression suite and checks the bundled `vault-secret-ledger.js` asset. Branch validation workflow collects stdout into artifacts on completion. The GitHub Actions connector currently reports failures and exposes neither job steps nor downloadable logs; no successful build can be claimed.
- Do not mistake independent S-key queue key lifecycle and read-only encrypted snapshot integration for working offline sync: no user-facing local draft enrollment or approved S-key durable mutation publisher is enabled, and no encrypted local payload is migrated from HD.


## 2026-11-15: GitHub CI removal and independent S-key local consent UI

**User explicitly requested removal of GitHub Actions CI to avoid workflow usage.** Deleted the following workflow files on BOTH `main` and `architecture/pluggable-vault-records-and-chains`: `fresh-v2-build.yml`, `pwa-preview-release.yml`, `maui-windows-preview.yml`; removed `linked-vault-validation.yml` on the feature branch (it was absent on main). Read-only fetch verified all four paths absent on both branches. Do not recreate GitHub Actions or automatically run CI unless the user explicitly requests it.

- Added `VaultLinkedLocalQueuePanel.razor` to the shared `VaultSecretKeyWorkspace`, which renders in both PWA and native. It allows a user to explicitly consent to encrypted device storage, discover/create/unlock a separate S-key Argon2id-wrapped local queue key with a separate >=12-character local passphrase, inspect the isolated encrypted queue, and lock local drafts. This is a read-only queue inspector: no queued S-key publisher, automatic sync or on-chain transaction is enabled. Turning consent off does not erase previously encrypted stored credentials/data, and UI explicitly warns users.
- Added distinct session-only `VaultLinkedLocalRetentionGate` instead of treating HD `VaultDeviceRetentionGate` as sufficient S-key consent. Every independent key session, browser/native credential adapter, browser/native encrypted queue adapter, and linked queue inspector checks BOTH gates. The shared panel grants S-key consent only on an explicit checkbox selection and resets it on withdrawal/dispose. It tracks whether it enabled the general device gate so it does not undo unrelated existing HD consent.
- Extended `IStellarSecretKeyLedger.ConnectionEpoch`: Browser transport advances it for every connect and disconnect, and `VaultLinkedQueueKeySession` captures the exact epoch on key activation and checks it for every operation. Reconnecting the same root and network cannot reuse a previous local key unlock. `IsUnlocked` also reflects current consent and session epoch.
- Static Node regression sources now check the UI opt-in, consent isolation, session epoch and absent workflow files. A local parser check of the S-key JS and Node test file passed; **Node test execution and .NET compilation are NOT verified** (the available container has Node but no .NET SDK or network access to clone this repo). Do not imply production readiness.
- Manual validation from local checkout (no GitHub CI): `node --test tests/secret-key-ledger.test.cjs`, `dotnet build src/StellarVault.Core/StellarVault.Core.csproj`, `dotnet build src/StellarVault.UI/StellarVault.UI.csproj`, `dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj`, `dotnet run --project tests/StellarVault.Core.CryptoTests/StellarVault.Core.CryptoTests.csproj`. For MAUI use installed local .NET MAUI workloads on an appropriate host.
- Still missing: independent S-key queued mutation staging + approved publication with historic operation proof and durable uncertainty recovery, file parity, and user-controlled encrypted-data deletion UI. Reuse existing HD hybrid/queue/CAS and file Core services, not duplicate. EVM last.


## 2026-11-15: independent S-key encrypted draft creation and explicit manual publication

This section supersedes earlier statements that **all S-key queue publication is disabled**. The feature branch now has a distinct **create-only, user-reviewed** linked queue pipeline; it has NOT been compiled or tested to completion in this environment.

### Reused components and actual code changes
- `IVaultLinkedDraftStore` extends the existing isolated read-only linked queue store with **atomic Add/TryDelete/TryReplace**, delegating to the ORIGINAL PWA `IndexedDbVaultPendingMutationStore` and native `FileSystemVaultPendingMutationStore`, with existing AES-GCM associated data, CAS and native file locks. Both host adapters enforce `VaultLinkedLocalRetentionGate`, `VaultDeviceRetentionGate`, independent S-key key session, exact account/network-prefixed namespace and linked strategy tag. CAS transitions are restricted to Pending→Submitting→Confirmed/Uncertain, with immutable draft metadata and 64-hex confirmed hashes. No alternate queue/storage implementation.
- `VaultLinkedDraftWorkspace` stages encrypted record/folder **creates**, lists titles/identities/states and discards only never-submitted drafts without active dependents. Local parent dependencies reuse `VaultQueuedCreatePolicy.ParentCandidates`. Parent IDs are selected in UI from published/local folder choices, rather than hand-entered. Serialized drafts are encrypted by the existing host store and transient payload byte arrays are zeroed. Staging currently requires a **live S-key signer and Horizon connectivity** because the independent local key session enforces live account inspection. Do NOT call this offline-ready.
- `VaultLinkedDraftPublication` is a separate reviewed create-only publisher using the ORIGINAL `IStellarSecretKeyLedger.QuoteCreateAsync` and `SubmitApprovedAsync`. A review is bound to the SHA-256 digest of the complete queued mutation, exact root/network connection epoch and 2-minute quote lifetime. The user approves the quote on a separate UI screen. Core verifies latest encrypted queue revision, local parent dependency graph and remote ledger record inventory. It atomically CAS-reserves Pending→Submitting **before** network submission, then verifies the connected ledger record (ID, title, content, parent, folder flag, username, URL, notes, tags) and CAS-marks Confirmed with the returned 64-hex hash. Any transport, read or persistence failure after reservation leaves an Uncertain/Submitting journal; never auto-retry. **This connected-state verification is NOT independently audited historical operation/signer proof.**
- Shared `VaultLinkedLocalQueuePanel.razor` (PWA and MAUI) now exposes local title/credential/folder draft entry, local list/discard, published or pending parent folder picker, separate manual `Review publish` fee/reserve quote, explicit approval and submit, and a state report. The direct HD, imported S-key and Mainnet restrictions remain distinct; queued deletes/updates are not enabled. Both hosts register the new Core services.
- Added `tests/StellarVault.Core.CryptoTests/LinkedDraftSmoke.cs` and called it from the existing test console. It exercises actual native encrypted queue storage, separate S-key key lifecycle, folder parent dependencies, approved create publication via a fake ledger and fail-closed uncertain outcome. Added Node source regression assertions for hosts, UI, immutable CAS and no GitHub CI workflows.

### Manual verification / remaining important blockers
- **No GitHub Actions workflows:** removed on BOTH main and feature branches per user request; do not restore or trigger CI. Local commands: `node --test tests/secret-key-ledger.test.cjs`; `dotnet build src/StellarVault.UI/StellarVault.UI.csproj`; `dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj`; `dotnet run --project tests/StellarVault.Core.CryptoTests/StellarVault.Core.CryptoTests.csproj`. Full MAUI compile/device testing needs corresponding SDK workloads.
- **No .NET SDK, public GitHub clone network or full code checkout in this execution environment.** Only source inspection, connector fetches and JS syntax parsing are possible; no tests or builds have been run here. Do not claim production-ready or green builds.
- Still to complete: **independent historical transaction operations and signer authorization proof**, durable cross-tab/device uncertain reconciliation including restart-safe S-key diagnostics, truly offline S-key local unlock (currently requires live connected ledger), full queued update/delete with conflict/version recovery, automatic linked capacity expansion journal for *queued* creates, encrypted file/chunk upload parity, native device E2E UX/security review and release audit. Existing HD/hybrid queue, folder/file, approval and history services already exist and should be adapted rather than rebuilt. EVM last.


## 2026-11-15 security correction — no shared HD/S-key consent side effects

A follow-up review caught a real cross-strategy authorization issue: the first version of `VaultLinkedLocalQueuePanel` selected the **HD/global** `VaultDeviceRetentionGate` when the user consented to imported S-key local storage. That was incorrect. The correction is now committed:

- `VaultLinkedLocalRetentionGate` independently **owns** a fresh `VaultDeviceRetentionGate DeviceRetention` and selects/resets only that gate on explicit S-key consent. It never modifies the HD/local vault retention gate.
- `VaultLinkedQueueKeySession`, S-key credential stores, PWA/native encrypted queue adapters, `VaultLinkedQueueInspection`, `VaultLinkedDraftWorkspace` and `VaultLinkedDraftPublication` no longer inject/consume the global HD `VaultDeviceRetentionGate`; they require `linkedRetention.RequireLocalPersistence()` and its owned `linkedRetention.DeviceRetention`. Original HD queue services keep their existing host-gate DI.
- Both PWA and MAUI use the same strategy-specific gate instance for linked encryption and consent; native registrations and the local fake-ledger smoke test were updated. The smoke test verifies that granting S-key consent leaves a separate HD gate in `OnChainOnly`.
- Shared UI no longer turns on/off general HD retention when S-key consent is changed or the S-key panel is disposed. Existing encrypted S-key credential/queue namespaces and formats were not changed, so previously encrypted linked drafts remain readable under their original local passphrase after fresh explicit S-key consent.
- Node regression source expectations updated; JS syntax parsing passed. No .NET SDK/compiler/test execution or real Testnet verification available in this environment; do NOT claim the code is compiled. GitHub Actions remains removed on both feature and main branches per user request.

Remaining blockers from the previous section remain unchanged: independently verified historical operation proof, durable uncertain recovery across devices/reloads, offline authorization without live Horizon, queued update/delete, automatic queued capacity expansion and file upload parity. EVM last.


## 2026-11-15 continuation — true offline local-only S-key draft access and advisory recovery

- Added `VaultLinkedQueueKeySession.UnlockOfflineAsync(publicRoot, localPassphrase)` and `HasLocalCredentialsOfflineAsync(publicRoot)`. After **prior online enrollment** of an independently wrapped S-key queue key, a user can explicitly consent and unlock the same authenticated encrypted queue with their public G-address, selected network and **separate local passphrase** without importing the S-key or contacting Horizon. The existing Argon2id wrapping and authenticated PWA IndexedDB/MAUI filesystem encrypted queue format are reused, and the key is zeroized on lock/dispose. Offline access cannot create a new wrapped credential without prior online enrollment.
- The key session distinguishes `IsOfflineOnly`, `ActiveRootAccountId`, `IsUnlocked` and `CanPublish`. Offline-only local queue operations NEVER invoke JS signer or Horizon, check account/network scope before and after each operation and fail closed on selected-network changes. The Core `VaultLinkedDraftPublication.RequireScope` requires `keys.CanPublish`: reconnecting the same S-key signer **cannot promote** an offline-only unlock to publication authority. The user must separately unlock the queue under the freshly connected signer and network, request a fresh quote and approve it. Signer-connected queue unlock retains live Horizon chain checks.
- `VaultLinkedDraftWorkspace` can now create/list/discard encrypted local record/folder drafts without Horizon. Locally staged parent dependency edges are still validated. Unknown remote parent-folder IDs may be staged as *unverified* offline references; before any eventual publication the existing remote preflight must confirm the actual folder and all dependencies. Local IDs are random but remote collision checks only happen at publication when offline.
- `VaultLinkedQueueInspection` now supports an explicitly offline read-only local-queue status without claiming remote verification. The shared `VaultLinkedLocalQueuePanel` can be reached from disconnected PWA `/secret-vault` and the native S-key unlock screen, requiring public root G-address, selected network, separate local passphrase and explicit separate S-key storage consent. Offline UI disables publish/recovery network actions and labels published-folder visibility as unavailable. No on-chain transaction can be prepared from this offline mode.
- Added `VaultLinkedDraftRecoveryReview`: an advisory-only read of a durable Submitting/Uncertain S-key queue entry, comparing the current fully decrypted record fields and optionally using the *existing* Horizon transaction receipt observation **only if** matching public diagnostic/root/record/hash is available. Neither matching current state nor receipt is independent proof of complete historical record operations. The adapter has **no durable state transition methods**; no automatic retries, releases or confirmation. Shared PWA/MAUI panel exposes this on unresolved queued drafts while connected.
- Fixed `VaultLinkedDraftPublication` to discard/wipe the pending approval and cancel the SDK quote even for mismatched fee approvals or failed quote preflight. Network scope authorization now fails closed without throwing from the `IsUnlocked` UI getter when custom-network selection becomes unverified.
- Extended `LinkedDraftSmoke.cs` to simulate offline Horizon failure, separate encrypted local unlock/staging, no publication, same-signer reconnect refusing offline-key promotion, fresh online unlock, changed network scope and read-only uncertain recovery. Added static Node source assertions; **no .NET compile/test execution is available here**.
- **STILL NOT FINISHED FOR PRODUCTION:** exact historical operation and signer proof; safely clearing quarantined submissions on verified independent evidence; complete queued update/delete and file chunk publication; imported S-key linked capacity rollover for queued creates; native real-device/restart/end-to-end security tests; default release audit. Even with local-only offline drafts, full offline *on-chain synchronization* is not possible by definition until reconnect. GitHub Actions remains deleted per user request; run local Node/.NET builds and Testnet tests manually.


## 2026-11-15: isolated S-key local draft editing and shared CAS policy

- Added `VaultLinkedMutationTransitionPolicy` as a SINGLE shared Core transition validator used by the EXISTING PWA IndexedDB and native filesystem linked draft adapters. It authorizes only pending create draft revisions `Pending→Pending` (same account, network, record ID, entity kind, origin, creation timestamp, state and transaction hash), `Pending→Submitting`, and `Submitting→Confirmed/Uncertain`. Draft revision may change only encrypted payload and parent-dependency edge atomically. Submitted/uncertain state can NEVER be restored to pending by editing. Original encrypted stores still provide authenticated AES-GCM storage and atomic CAS. This replaces duplicated adapter transition logic, not the storage engine.
- `VaultLinkedDraftWorkspace.OpenDraftAsync` explicitly decrypts one unpublished local record/folder when the user chooses Edit. `ReviseAsync(mutationId,draft,expectedPayloadSha256)` validates the previously opened payload fingerprint in constant time against the exact current encrypted queue revision, retains the original stable record ID, recomputes local parent dependency, detects ancestor cycles and performs a single store CAS revision. Published/unresolved entries cannot be edited. Offline parent references remain unverified until later online publication preflight.
- Shared PWA+MAUI `VaultLinkedLocalQueuePanel` now offers `Edit local draft`, masked secret/notes fields and Save encrypted changes / Cancel editing. When offline editing a draft whose previously published parent cannot be fetched, the UI preserves its original parent record ID as an explicitly *unverified* option rather than losing it. Existing create, local-only unlock, fee review, publish and uncertain recovery remain separate.
- Native encrypted queue smoke test now covers successful revision, rejection of stale editor digest, offline revision while fake Horizon throws, folder-cycle refusal. Node source test checks CAS transition policy and the shared edit UI. No .NET SDK or full local checkout: these tests **remain unexecuted**, and no build success can be claimed. GitHub Actions remains disabled per user request.
- Major blockers still unresolved: operation-by-operation historical receipt / signer proof, trustworthy clearance of durable Uncertain/Submitting journal across device restarts, queued remote update/delete and binary upload parity, automatic reviewed account capacity rollover for staged creates, real Testnet + PWA/MAUI device tests and security audit.


## 2026-11-15 — ordered S-key manual-sync review (no background publication)

- Added pure Core `VaultLinkedManualSyncPlan.Build` to reuse `VaultQueuedCreatePolicy.Problems` for pending encrypted S-key records and folders. It derives blockers and one `NextReadyMutationId` from the SAME authenticated snapshot, orders deterministically by creation date and mutation ID, and fails closed if ANY durable Submitting/Uncertain/Conflict entry remains. It also blocks manual review when `VaultLinkedQueueKeySession.CanPublish` is false (offline-only local unlock, disconnected signer or network mismatch).
- `VaultLinkedDraftWorkspace.ReviewManualSyncAsync` reads the existing encrypted CAS queue once, derives the pure plan, zeroes temporary decrypted payload buffers, and does not touch network or durable queue state. Shared `VaultLinkedLocalQueuePanel` now has `Review manual sync order` and `Review next ready draft`, displaying each draft's specific dependency/strategy/blocker reason. The next-ready button passes into the original per-item `VaultLinkedDraftPublication.PrepareAsync`; user still must approve a fresh exact Stellar fee/reserve quote for EACH record/folder. The queue is never auto-submitted, and a successful item requires a fresh snapshot before the next review.
- Expanded local fake-ledger Core smoke to verify parent is ready first, child becomes ready only once parent is confirmed, and all suggestions are blocked after an uncertain transmission. Node static checks were updated.
- This is NOT full automated sync or production readiness: independent operation/signer history and durable quarantine clearance, queued updates/deletes and binary files, reviewed account-capacity rollover on full linked tail, PWA/MAUI real device/transaction tests and release security audit remain. New .NET tests were NOT run in this environment (no dotnet SDK). GitHub Actions stays removed on both main and feature branch per explicit user request.


## 2026-11-15 — durable linked capacity expansion for manual encrypted queue

This milestone extends the pre-existing `VaultLinkedAccountExpansion` approval flow, **not** a replacement account creation/signing protocol. It reuses the separately consented S-key encrypted queue to persist an account-expansion transaction intent across app restarts.

- Appended `VaultMutationKind.ExtendLinkedChain` (preserving prior enum numeric values) as a LINKED-ONLY public-intent journal mutation. HD enqueue rejects this kind outright. Existing PWA IndexedDB and MAUI filesystem encrypted stores accept the linked intent using the existing account/network-scoped AES-GCM and CAS, and the shared `VaultLinkedMutationTransitionPolicy` accepts only immutable Pending→Submitting→Confirmed/Uncertain transitions for expansion entries.
- Added `VaultLinkedExpansionJournal` as a wrapper over the **existing** `VaultLinkedAccountExpansion` signer/strategy approval service. `PrepareAsync` and `SubmitApprovedAsync` both refuse if the encrypted queue contains any unresolved Submitting/Uncertain/Conflict entry or an expansion journal not yet Confirmed. Before broadcasting, it computes a **deterministic 32-hex journal ID from authenticated root, network and current tail account** so simultaneous browser tabs planning the same tail race for the SAME original store atomic Add slot. It then CAS-reserves Pending→Submitting BEFORE calling the existing expansion transport. On observed success, it CAS-confirms with a valid hash; if transport/read-back/persistence fails, it leaves Uncertain or Submitting, never silently retries. A crash after Add but before reservation leaves a Pending expansion journal which also blocks future publishing.
- Added `DiscardNeverSubmittedIntentAsync` to permit **explicit, atomic cleanup only for a still-Pending expansion intent**. A sibling tab's CAS transition to Submitting makes the discard fail; Submitting/Uncertain/Confirmed journals are never removable by this method. This cleanup never broadcasts. The shared UI renders account expansion as a distinct non-editable journal item and offers the pending-only cleanup action with a warning.
- `VaultLinkedManualSyncPlan` and `VaultLinkedDraftPublication` now block ordinary manual record/folder publication if a linked expansion intent remains Pending/Uncertain/Submitting/Conflict. `VaultLinkedDraftRecoveryReview` reports advisory linked-tail visibility for unresolved expansion without misclassifying it as a missing record; neither current state nor receipts clear quarantine.
- Both PWA/MAUI register the new service and the shared S-key local queue panel now provides a **separate reviewed account-capacity quote and fee/reserve approval**, routed through the durable journal. Completing expansion does NOT automatically submit a queued record; the user must re-review the original draft separately.
- Extended native fake-ledger smoke with successful durable expansion, simulated unknown outcome, reopened encrypted store observation, refusal to retry after fresh local unlock, and safe cleanup of a never-reserved Pending intent. Node static source assertions cover the new journal, deterministic tail reservation, CAS and UI.
- **Tests and .NET builds were not executed in this environment.** GitHub Actions remains removed on main and feature branch per user request. Real Testnet transaction and MAUI device verification is still required.
- **Limits:** This journal covers **expansions initiated through the linked encrypted queue UI only**, with explicit local storage consent. The older direct on-chain workspace's expansion path can still rely on volatile same-runtime quarantine and is NOT now durable without local consent. Exact historical operation/signer proof and safe clearing of Uncertain/Submitting after restarts remain an unmet production-security requirement, as do queued updates/deletes, binary file parity, and release audit. Do not label entire project production-ready or claim all capacity flows durable.

## 2026-11-15 linked queue publication confirmation safety correction
- Active branch: `architecture/pluggable-vault-records-and-chains`. User reported local Node regression and PWA build passing after previous syntax/test fixes.
- Linked draft create submission previously advanced directly from durable Submitting to Confirmed after `ledger.ReadAsync` found matching CURRENT record fields. This is insufficient provenance: another writer could have produced that state, and historical signer/operation/chunk evidence remains incomplete.
- `VaultLinkedDraftPublication.SubmitAsync` now records durable `Uncertain` after successful transaction plus current-state comparison; no queue mutation is Confirmed from a current snapshot. Matching current state remains only a read-only diagnostic. No blind retry or child publication is permitted while unresolved.
- The shared linked queue panel now explicitly describes the unresolved state even after a successful submit, and the native smoke scenario enforces that a matching current record does not release quarantine. Additional source-level regression assertion updated.
- Native network passphrase is null-gated before linked discovery. HD hybrid draft creation uses explicit non-null argument validation.
- This batch was committed using GitHub file edits; .NET/Node execution in this environment was NOT performed. Run local `node --test tests/secret-key-chain.test.cjs tests/secret-key-ledger.test.cjs`, `dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Release`, and `dotnet run --project tests/StellarVault.Core.CryptoTests/StellarVault.Core.CryptoTests.csproj -c Release`.
- Still missing: independent historical linked operation/signing proof, tested reconciling uncertain queue state, linked binary file parity, and production security/audit gates. Do not claim production complete.

## 2026-11-15 credential-independent device-storage entry layout
- PWA entry `VaultHome` now renders the optional encrypted device storage preference outside and above the credential card for both HD/mnemonic and independent imported S-key tabs. The Unlock/Lock action is outside/below the same card, still bound to the form via `form="sv-access-form"`.
- The position of storage controls and access button no longer changes when tabs switch; only credential fields do. The separate security scopes are preserved: HD uses `_enableLocalStorage` and HD session setup, while S-key grants explicit `VaultLinkedLocalRetentionGate` consent and uses its own keywrap/passphrase/queue after connection. Turning off S-key consent locks linked queue keys and resets only linked retention, not HD retention. No S-key secret is stored by merely selecting the preference.
- Existing S-key workspace consent remains available for in-workspace control. Device consent does not authorize S-key publishing; fresh signer-backed connection, quote and explicit approval are still necessary. Updated PWA CSS and a source regression assertion.
- Commit source changes only; local Node/.NET build and visual testing on desktop/mobile have not been performed for this batch.

## 2026-11-15 linked account expansion historical-proof quarantine
- Independent linked account expansion through `VaultLinkedExpansionJournal` previously marked an encrypted journal `Confirmed` immediately following `VaultLinkedAccountExpansion.SubmitApprovedAsync` success. A successful transport/read-back is not full historical CreateAccount, SetOptions signer, and parent-pointer operation proof.
- Changed the successful path to persist `Uncertain` with the returned hash via durable CAS, mirroring linked record publication. This intentionally blocks further queued writes until a separate historically proven reconciliation flow exists. The direct non-journaled signer workflow remains separate; do not promise durable reconciliation there.
- Split native fake-ledger smoke tests into record-publication quarantine, successful expansion quarantine with close/reopen verification, and uncertain expansion failure. Regression source assertion prohibits optimistic expansion `Confirmed` transitions. These changes were applied through GitHub; native smoke and complete Node suites have **not** been executed in this environment.
- Still critical: independently verify historical operation provenance and signer policies; after verified evidence, add explicit CAS reconciliation/release under correct account/network scope, plus cross-tab/restart integration tests. S-key file parity, platform testing, security assessment and release operations remain incomplete.

## 2026-11-15 local verification follow-up: stale test assertions and compiler warnings
- User's local PWA Release build succeeded with Core CS9113 for unused `guardedLedger` constructor parameter in `VaultManualSyncCoordinator` and PWA CS0414 for an assigned but unread `Vault._syncEstimateNote`. Removed only the unused constructor injection; did not relax or alter transaction authorization. PWA now renders the existing sync status note for user-visible read-only estimate/queue-change feedback.
- Node regression test asserted obsolete `uncertainPlan` variable removed when the linked draft smoke was split into successful-submission quarantine tests; updated it to assert the existing `afterParentPlan` unresolved-state check. Device-storage placement assertion searched for the old form markup without its new `id="sv-access-form"`; updated to match the actual external-button-associated form. Removed a duplicated assertion.
- These are targeted test/build fixes, not new independent historical verification, reconciliation, or S-key binary support. GitHub commits done without running .NET/Node in this environment. Require local `node --test tests/secret-key-chain.test.cjs tests/secret-key-ledger.test.cjs`, `dotnet build src/StellarVault.Pwa/StellarVault.Pwa.csproj -c Release`, and Core smoke run.

## 2026-11-15 linked recovery smoke assertion correction
- User confirmed the PWA .NET Release build now passes, but the local Node source-regression suite reported an obsolete expectation `finding.CurrentRecordMatches is false` after the success-path smoke test was changed to inspect an already-submitted folder.
- The fake ledger publishes that folder's current state, so the correct diagnostic is `CurrentRecordMatches is true`. This **must still leave the durable queue Uncertain** because the existing deny-only historical receipt evidence cannot independently establish the exact transaction/operations.
- Updated `LinkedDraftSmoke.cs` to explicitly require a matching current snapshot AND no historical proof while retaining the Uncertain/Submitting state. Aligned the Node source assertion. No live network use; executable Node/Core tests have not been run from the GitHub connector.

## 2026-11-15 signed-hash persistence and restart-safe advisory receipt lookup
- The independent S-key JS signer now computes the **exact signed transaction hash** with `tx.hash()` before broadcasting and stores it in its existing public-only uncertain diagnostic. An absent hash stops the broadcast; a Horizon response reporting a different hash fails closed into quarantine. This public hash does NOT prove inclusion or a specific record operation, and never authorizes replay.
- New shared Core `VaultLinkedSignedSubmissionIdentity.TryReadAsync` binds diagnostic kind, root, target account, record ID, selected network passphrase and connection epoch before accepting its 64-hex hash. Both linked draft and linked expansion catch paths best-effort checkpoint that hash into the existing encrypted durable Uncertain CAS revision after a Horizon timeout. If an unknown result occurs before a hash is available, the journal remains Submitting/Uncertain without an identifier and must not be retried.
- `VaultLinkedDraftRecoveryReview` can reconstruct a **read-only, advisory receipt query** from a durable hash, the selected trusted Testnet/Public preset (or explicitly selected custom passphrase + HTTPS Horizon origin), and the reopened connected S-key account. It does not depend on a transient JS inspection surviving process restart. It still cannot prove historical encrypted writes, true child signer authorization at operation time or matching decrypted content from a historical ciphertext, and cannot change the durable queue state.
- Node mocked signer now supplies signed hash semantics and tests timeout/hash mismatch, with source assertions for scope binding. Native fake-ledger smoke supplies signed public identity after unknown expansion outcomes, verifies durable storage and refuses network, operation and epoch mismatches.
- All code committed through the connected GitHub repository. **No Node/.NET binaries or Stellar Testnet integration tests were run here.** User previously confirmed earlier tests passing; this new batch needs fresh local execution.
- Remaining release blockers: complete independent historical operation/signer/ciphertext verification and safe durable CAS release, linked queued update/delete, linked file publication, multi-device and MAUI real-runtime recovery, independent security audit. A controlled Testnet pilot must not use real credentials or valuable funds.
