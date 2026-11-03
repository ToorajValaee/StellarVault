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
