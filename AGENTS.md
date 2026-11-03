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

## 2026-11-03 exact historical ciphertext comparison (read-only)

- `HorizonVaultHistoricalChunkInspector.InspectAndCompareAsync` reconstructs the historical `svc:` ciphertext, validates successful chunk receipts and SHA-256 manifest commitment, then invokes a trusted in-process comparison callback on a temporary encrypted buffer. The temporary ciphertext is wiped after the callback. This is NOT a general-purpose ciphertext extraction API; avoid logging, persistence or capturing the array.
- `StellarVaultStore.CompareHistoricalHdRecordAsync` uses the connected unlocked HD session to decrypt that exact historical envelope with node index, record ID and manifest generation; it compares all approved record fields without returning plaintext. `BrowserStellarLedgerRecords.CompareHistoricalEncryptedRecordAsync` checks the existing session's root/network/Horizon endpoint first.
- `HorizonVaultPublicationDiagnostic.InspectHistoricalRecordAsync` composes endpoint-reported network, receipt, manifest, historical chunk integrity and exact ciphertext-to-approved-record comparison. It is read-only and **must NOT** be wired to queue confirmation. Session-chain discovery is not yet an independently audited deterministic root-child proof, and Horizon is still a trusted endpoint.
- The current-record diagnostic remains available but should not be used as evidence for a historical mutation. Keep `DenyVaultLedgerMutationVerifier` and disabled broadcast UI.
- No builds/tests performed. Verify decryption contract and historical Horizon fixtures before any production use.

## 2026-11-03 authenticated HD root-to-child lineage gate

- `stellarVault.stellar.inspectHdLineage` in `src/StellarVault.Pwa/wwwroot/js/ledger-compat/app.js` is an explicit read-only JS session gate. It re-derives root index 0 and the expected HD child index from the in-memory BIP-39 seed using SEP-5, compares their public addresses, reloads the root account from Horizon and verifies the current `sv:registry:v2` HMAC envelope with a session-derived MAC key.
- Require the authenticated current registry to match cached revision/highest/active indices and include the requested index, and the derived child account to exist, before permitting exact historical ciphertext decryption. Key/seed material remains in the browser session and temporary Ed25519 seed bytes are zeroed.
- `StellarVaultStore.CompareHistoricalHdRecordAsync` now requires this lineage gate before `LoadChainAsync`/decryption. It fails closed on unsupported `bip32-compat` scheme. Legacy fallback appears in discovery but the current `hd-keys.js` accountSeed supports only `sep5`; do not silently assert compatibility.
- This verifies deterministic **current unlocked-session lineage**, not independently proven historic root registry state at publication ledger nor network consensus or time-of-check atomicity. Do NOT register a live `IVaultLedgerEvidenceProvider` or expose publishing without audit and tests.

## 2026-11-03: HD recovery fail-closed and browser Horizon trust boundary

- `ledger-compat/hd-discovery.js` now attempts only SEP-5 index-0 recovery, the sole scheme actually implemented in `hd-keys.js`. Previous code misleadingly tried `bip32-compat` after a 404, which always threw from `accountSeed`. Legacy scheme accounts are **not** recovered or migrated; keep unsupported paths explicit rather than auto-guessing.
- `ledger-compat/app.js` now validates direct Horizon root URLs (HTTPS, or HTTP only on localhost/loopback), rejecting embedded credentials, paths, query and fragment; browser-side Horizon discovery fetches omit credentials and reject redirects. These are trust-boundary safeguards, not proof of independently verified consensus.
- A seed with an absent SEP-5 root should fail with actionable recovery guidance instead of attempting unsupported keys. No changes to queue confirmation or broadcast UX; the mutation verifier remains deny-by-default.
- No builds/tests run per project instruction. Browser SDK CORS, local loopback scenarios and regression fixtures need testing before release.

## 2026-11-03 historical root registry diagnostic

- `HorizonVaultHistoricalRegistryInspector` performs a bounded (16 × 200 operations) descending root-account history lookup anchored before the manifest operation ID. It returns the latest `sv:registry:v2` ManageData envelope as an **untrusted candidate**; malformed/deleted/unsuccessful matching operations fail closed.
- `stellarVault.stellar.inspectHistoricalHdRegistry` checks the 50-byte envelope using the session's seed-derived registry HMAC key, expected network passphrase and SEP-5 root/child address derivation, requiring the expected child index to be allocated. Callers must additionally validate the candidate transaction receipt and ordering. Only the already-unlocked HD session is used; secret keys never leave JS.
- `HorizonVaultPublicationDiagnostic.InspectHistoricalRecordAsync` now requires historical root registry evidence and authentication before historical chunk decryption. This remains a **read-only diagnostic**, not a queue evidence provider. Horizon history can be pruned, and an endpoint-reported receipt is not independent consensus proof. Keep DENY verifier, disable broadcast UI.
- Registry HMAC verification does not establish all historical root governance/account creation/signers. Never interpret a matching record as final confirmation before protocol and browser tests/security review.

## 2026-11-03 read-only queued-create historical review

- `VaultQueuedCreateHistoricalReview` is a **diagnostic-only bridge** from an already-reserved `VaultPendingMutation` to `HorizonVaultPublicationDiagnostic.InspectHistoricalRecordAsync`. It never calls `IVaultLedgerMutationVerifier`, changes queue state, broadcasts, retries or returns approved plaintext.
- Requires `Submitting` or `Uncertain` create-record/folder kind, a stored 64-digit transaction hash exactly matching the inspected hash, a valid 16-character record ID and 32-character mutation GUID, an existing account, and an explicit expected HD child account/index. Unknown tx hashes fail closed; do not substitute an arbitrary matching transaction.
- Binds mutation network namespace to `VaultNetworkSelection.CredentialKey(rootAccount)`. Known Testnet/Public networks use explicitly selected canonical Horizon roots and passphrases; custom networks use explicitly selected HTTPS root and passphrase. Reject malformed or cross-network scope.
- Deserialize the already-unsealed queue payload as `LedgerRecordDraft` and ensure folder kind/title/tags/self-parent constraints before historical comparison. Do not treat the misleading field name `EncryptedPayload` as proof that bytes are ciphertext **in memory**: durable IndexedDB store is responsible for encrypting the serialized queue at rest.
- This diagnostic alone cannot safely mark mutations Confirmed. The user-approved review quote can expire, local pending mutation state can change, and historical Horizon evidence remains endpoint-attested. Keep the app's deny-by-default verifier and disabled publication UI.

## 2026-11-03 durable queue revision gates for historical diagnostics

- `VaultQueuedCreateHistoricalReview` now reads the authenticated durable `IVaultPendingMutationStore` entry before contacting Horizon and re-reads afterward. It requires full mutation revision equivalence: ID, root/network, kind, entity, payload bytes, expected remote version, dependencies, state, hash and created time; selected network scope must also remain unchanged. It returns no positive result if the mutation is edited, removed, reconciled or switched during inspection.
- `VaultPendingMutationQueue.MarkUncertainAsync` now compares its Submitting argument to the actual durable revision before transitioning with CAS. `ConfirmReconciledAsync` likewise requires the requested snapshot to equal the durable revision before invoking even a future verifier. Prevent accidental cross-tab use of stale caller snapshots.
- This is a read-only diagnostic plus queue transition hardening, **not an authorization to confirm**. The post-read consistency check is not an atomic snapshot across Horizon and IndexedDB; the existing queue verifier stays deny-by-default and publishing UI disabled. Concurrent writes following the final read can still occur; any future confirmation must re-check via CAS.
- Builds and tests are deliberately deferred until the user authorizes them. Verify browser, .NET, concurrent tab and lock-session cancellation behavior before production use.

## 2026-11-03 manual unknown-hash candidate investigation

- `VaultUnrecordedSubmissionDiagnostic.InspectCandidateAsync` now lets future recovery UI **read-only inspect** a user-found Stellar transaction hash for an existing, durable `Submitting`/`Uncertain` queued create whose transaction hash was never recorded. It retrieves the encrypted-at-rest scoped queue entry with the current unlocked key; no hash is assigned and no mutation is confirmed, failed, retried, or discarded.
- `VaultQueuedCreateHistoricalReview.InspectUnrecordedCandidateAsync` shares the full historical evidence pipeline but requires the durable queue hash be **null**, checks strict account/network/ID/draft/HD child identity, and re-reads durable state after verification. It distinguishes a matching candidate from proof that the original local submission generated that transaction; a duplicate identical record is possible.
- The existing `InspectAsync` path still requires an exact stored hash. During history inspection, recheck the selected network and, for custom networks, passphrase and HTTPS endpoint stability to fail closed on mid-inspection changes.
- Recovery diagnostics remain backend-only (not exposed as user approval, confirmation, or broadcast UI); `IVaultLedgerMutationVerifier` stays deny-by-default. Never automatically assign a candidate hash to an unknown outcome. Build/browser/Testnet/concurrency tests have not run.

## 2026-11-03 unknown-hash investigation UI and API consistency repair

- /vault unresolved-submission modal now allows read-only inspection of a manually discovered 64-hex transaction candidate when the durable queued create is Submitting/Uncertain with no stored hash. User must enter expected HD child G-address and index 1-63; a connected session is required. Never displays matching candidate as a successful submission confirmation, never saves the hash, retries, submits, or resolves the mutation.
- Fixed the missing `VaultQueuedCreateHistoricalReview.InspectUnrecordedCandidateAsync` API found in committed source even though `VaultUnrecordedSubmissionDiagnostic` called it. The shared inspection core separates strict saved-hash evidence (hash must match) from unknown-hash candidate evidence (durable hash must be null). Both retain durable revision and network rechecks.
- The modal only offers normal CHECK LEDGER CONFIRMATION when a stored hash exists. Unresolved state without hash remains quarantined; UI explanations emphasize non-attribution of candidate tx to the original browser attempt. This is read-only recovery visibility, not an alternative to independently audited confirmation.
- No builds/tests run per previous instruction. Razor rendering and browser/Testnet full path must be validated.

## 2026-11-03: hybrid Sync consolidation

- Manual Sync coordinator and outcome types moved to Core. PWA keeps the same DI registration; its old coordinator source must be removed from the commit.
- New Core FileSystemVaultPendingMutationStore implements encrypted native queue persistence with the session key, associated account/network/id data, exclusive file locking, and atomic CAS writes.
- MAUI registers the shared queue, consent gate, deny confirmation verifier and shared coordinator. Native ownership verification and hybrid UI remain gated.
- Do not confuse this Core refactor with completed hybrid UI or native release readiness. Direct on-chain behavior is left unchanged.
- Builds/tests remain deferred by user request. Verify both hosts and queue concurrency before release.

## 2026-11-03: shared hybrid review and fee-preview vertical slice

- `VaultHybridSyncReviewService` in Core now owns read-only queued-create review/fee estimation: consent and selected account/network checks, full queue/dependency review, remote record-ID inventory, shared create policy, full `PreflightCreateAsync`, fresh per-mutation fee estimate, and quote snapshot creation. Unsupported update/delete/file mutations receive explicit non-publishable explanations.
- The PWA's old `EstimateReadyDraftsAsync` per-record JSON/dependency/estimate loop was removed; its existing page now calls the Core service and maps the result into the existing review UI. Existing direct on-chain workflows remain unchanged; no queued publication is enabled.
- New `StellarVault.UI/Components/VaultHybridSyncReview.razor` provides a reusable read-only native queue review and fee-preview panel. Native workspace renders it; it refuses queue access without an unlocked local session and explicit retention consent. MAUI and PWA both register the same Core review service.
- Native `DenyVaultAccountAuthorizer` remains, so its panel correctly reports local-unlock unavailable; this is a shared UI preparation step, NOT completed native hybrid consent/unlock or publishing. Keep `DenyVaultLedgerMutationVerifier` and disabled queued broadcasting.
- No builds/tests run by prior user preference; check Razor compilation, Core service provider resolution, queue/mock ledger review fixtures, concurrent state changes, and both hosts before release.

## 2026-11-03: shared offline record/folder editor and explicit native local consent

- `VaultHybridDraftWorkspace` in Core creates, revises, lists and discards queued record/folder drafts using the same encrypted `IVaultPendingMutationStore` on PWA and MAUI. Requires the unlocked account and explicit local-retention consent. New 16-hex record IDs and dependency IDs are generated in Core; ciphertext is generated by host queue adapter at rest. Unpublished local folder parents are represented by durable mutation dependencies and block child publication until parent confirmation.
- `VaultHybridDraftEditor.razor` in shared UI now provides offline record/folder create/edit/discard, pending folder selection, and local directory across PWA Vault and MAUI NativeVaultWorkspace. It is explicitly device-only and contains no paid-transaction submit controls.
- MAUI `NativeVaultWorkspace` now offers separate local-only encrypted namespace create/unlock behind explicit opt-in checkbox and separate passphrase, plus existing lock control. `MauiProgram` uses `LocalOnlyVaultNamespaceAuthorizer` (same as PWA) **ONLY for local namespace access**; this is NOT Stellar signer authorization and NEVER grants transaction signing or queue confirmation. Direct on-chain signing continues to require the separate mnemonic-based ledger session. Native public-account namespace alone is not proof of ownership; an authenticated wallet challenge remains unimplemented.
- `VaultHybridDraftWorkspace` is scoped in both hosts. Uncertain-submission reconciliation, approval and actual queue publishing remain blocked by the existing `DenyVaultLedgerMutationVerifier` and disabled UI. This batch does **NOT** deliver the fully synchronized milestone; do not overstate readiness.
- No build/test run per user request. Verify native passphrase lifecycle, storage policy reset, PWA editor integration, filesystem queue, dependency order, and Razor compilation before release.

## 2026-11-03: first explicitly approved shared hybrid create publication

- **Core:** `VaultManualSyncCoordinator` now re-verifies that the connected mnemonic-derived HD root equals the unlocked local queue account through `IStellarLedgerRecords.InspectAccountInfoAsync`. It binds the selected account/network/custom Horizon settings and `VaultSessionManager.Epoch` before quote preflight and again immediately before signing. A local-only namespace authorizer does **not** grant ledger signing authority. No secret material moves between local and ledger sessions.
- **Shared UI:** `VaultHybridSyncReview.razor` provides a deliberate two-step approval: first a fresh server/queue/root/cost eligibility check on a specific short-lived quote, then an explicit checkbox covering fees, reserve and uncertain-no-retry behavior. Mainnet additionally requires a distinct public-network acknowledgment. The approval checkbox is consumed at submit. The same component is mounted in native workspace and PWA vault.
- **PWA:** removed the old preview-only review markup in favor of the shared component. The PWA local pending list and recovery diagnostics remain intact. This is the first UI wiring to the existing Core `PublishApprovedCreateAsync` for supported CreateRecord/CreateFolder.
- **Safety:** following durable CAS reservation, any attempted publication becomes `Uncertain` even when a tx hash returns. No automatic retry or confirmation; `DenyVaultLedgerMutationVerifier` remains registered in both hosts. The UI must not call a result Confirmed. Pending local children remain blocked until a parent is independently confirmed.
- No builds/tests run per prior request. Audit quote expiration, native and browser signing-root/session binds, real Mainnet consent, ledger error injection and queue state persistence before use with real funds.

## 2026-11-03: recorded-hash hybrid confirmation and dependency release

- Core `VaultPendingMutationQueue.ConfirmReconciledAsync` now requires an already-durable exact 64-hex transaction hash; unknown-hash candidate investigations can never attach a guessed hash or promote a mutation.
- `HorizonVaultQueuedMutationEvidenceProvider` is the PWA/MAUI host adapter for `IVaultLedgerEvidenceProvider`. For a recorded-hash CreateRecord/CreateFolder it binds the unlocked local vault, network/endpoint/passphrase and connected HD signing root; finds the tx-specific historical manifest source; resolves the child **from the authenticated connected HD chain**, never a caller-entered child index; verifies historical registry, chunks, encrypted ciphertext and decrypted approved draft before emitting `RecordLinkedToTransaction=true`. Unsupported mutation types never confirm.
- Both hosts register `EvidenceBasedVaultLedgerMutationVerifier` with this strict evidence provider. MAUI compiles the existing Horizon inspectors/provider as linked source, registers their DI graph and reuses the **same concrete BrowserStellarLedgerRecords** instance for `IStellarLedgerRecords` to avoid mismatched session state.
- Shared `VaultHybridSyncReview` UI now lists unresolved recorded-hash submissions and has a manual **VERIFY RECORDED TRANSACTION — NO RETRY** control. It calls Core `ReconcileAsync`, which only transitions Confirmed after verification plus durable CAS; queue review is refreshed and previously blocked children can become eligible once parent is confirmed. Missing-hash submissions remain quarantined and require separate investigation.
- This is a substantial security-sensitive change and is NOT production validated. User requested no builds/tests; perform PWA + MAUI builds, mocked Horizon historical fixtures, Testnet end-to-end create/confirm/dependent-child flow, lock/concurrency and malicious-response tests before production/mainnet use. Do not assume Horizon endpoints are independently decentralized sources of truth.

## 2026-11-03 — shared hybrid staging of remote updates/deletes

- Added `VaultHybridRemoteMutationWorkspace` to Core: an account/network/session-bound staging service for modifying or deleting **already published** Stellar record/folder entries. It reads the current connected mnemonic HD root and remote inventory, verifies the root equals the local vault account, excludes file-protocol records, and pins every staged change to a SHA-256 fingerprint of the original complete record. Re-reads Horizon and queue before enqueue; never broadcasts.
- `StageUpdateAsync` and `StageDeleteAsync` create encrypted-at-rest `VaultPendingMutation` records (UpdateRecord/UpdateFolder/DeleteRecord/DeleteFolder) with `ExpectedRemoteVersion` populated. `ReviseUpdateAsync` works offline and preserves the initial immutable fingerprint. Discard only unsubmitted Pending mutations. Folder deletion refuses nonempty folders; moving a folder validates all ancestors to avoid a cycle.
- New shared `VaultHybridRemoteChanges.razor` supports browsing authenticated remote records, staging an update/deletion, reopening an offline update, discarding unsubmitted changes and inspecting remote-baseline drift. Integrated into both PWA Vault and MAUI NativeVaultWorkspace. Both hosts register same Core service; direct on-chain editor unchanged.
- **Update/delete queue publishing and confirmation remain disabled.** Existing create-only approval guard, create-only coordinator, and historical verifier intentionally refuse these kinds. Drift checks provide read-only conflict notices; no silent overwrite, delete or auto retry. A stale remote fingerprint is not a safe version-control token until verified again immediately before transaction reservation.
- Important: cross-tab conflicting staging has a residual race without entity-indexed atomic uniqueness; queue reads reduce risk but are NOT an atomic per-entity lock. Never enable update/delete broadcasts until durable uniqueness, preflight and receipt linkage are added. No builds/tests run per user preference.

## 2026-11-03 — Testnet-only queued update/delete approval path

- Fixed a real create fee-review bug: `VaultHybridSyncReviewService` and `VaultManualSyncCoordinator` now call `ledger.EstimateAsync(draft)` for *new* record IDs; passing a new record ID was incorrectly interpreted by the browser ledger adapter as an UPDATE estimate.
- `VaultSyncApprovalGuard.Capture` now supports UpdateRecord/UpdateFolder/DeleteRecord/DeleteFolder only when a 64-lowercase-hex `ExpectedRemoteVersion` content fingerprint is present; snapshots bind this fingerprint, payload and mutation identity.
- Added Core `VaultPendingMutationQueue.PreflightRemoteMutationAsync` and `ReservePreflightedRemoteMutationAsync`: fresh draft quote, complete scoped queue check, conflict/unresolved block, no duplicate active target, full remote inventory check, exact complete-record SHA-256 baseline match, correct record-vs-folder kind, existing destination folder, full ancestry cycle check, and refusal of nonempty folder deletes/file-protocol items. Reservation uses durable mutation CAS and never broadcasts.
- Core `VaultHybridSyncReviewService` now offers fee/reserve quotes for version-pinned update/delete as well as create. Coordinator adds `CanPublishApprovedRemoteMutationAsync` and `PublishApprovedRemoteMutationAsync`, which re-validate connected HD root, session epoch, queue quote, exact baseline and fee caps before one attempted network update/delete after durable reservation. On any attempted publication, the queue enters Uncertain, with no automatic retry. Existing independent verifier remains **create-only**, so update/delete never auto-confirm.
- **CRITICAL LIMIT:** Direct Stellar writer has no atomic on-chain expected-version precondition. Therefore queued hybrid update/delete broadcasts are **Testnet-only**, blocked in Core and shared UI for Public and custom networks. Even on Testnet, external device writes may race the final remote inventory; this requires ledger-level conditional-write design and Testnet adversarial validation before production. Direct on-chain operations unchanged.
- Both PWA and MAUI already mount `VaultHybridSyncReview`; it now routes each kind to its correct Core coordinator, displays explicit experimental-warning text, and disables remote mutation approval outside Testnet. The unknown-hash/receipt verifier never assumes update/delete confirmation.
- No builds or tests run per user's prior preference. Inspect C# compilation, Razor compilation, cross-tab CAS race, full history proof and Testnet behavior before release.

## 2026-11-03 — primary transaction hash and Testnet historical UPDATE reconciliation

- **Critical identity fix:** `StellarVaultStore.UpdateAsync` may publish a manifest-write transaction and then a separate cleanup transaction. `DeleteAsync` likewise deletes manifests and may later clean orphan chunks. Historically their `TransactionHash` return value was the *last cleanup* receipt, which cannot be used to prove the primary mutation. Added optional `CommitTransactionHash` to legacy `VaultMutationResult` and shared `LedgerMutationResult`, threaded through `BrowserStellarLedgerRecords`. Existing direct on-chain callers can keep using `TransactionHash` for backward compatibility; **the hybrid queue stores only `CommitTransactionHash`**, or null if unavailable, never an unrelated cleanup receipt.
- `EvidenceBasedVaultLedgerMutationVerifier` now supports **UpdateRecord/UpdateFolder** in addition to CREATE. It requires an already-durable exact hash, valid pinned baseline fingerprint for UPDATE, and strict content equality to the approved draft. `HorizonVaultQueuedMutationEvidenceProvider` now handles UPDATE only on **Testnet**, requiring the primary transaction's manifest, authenticated historical HD child/root registry and chunks, and comparison of decrypted history to the queued update draft. Positive result can promote Uncertain→Confirmed through existing queue CAS; it does not prove an atomic check of the predecessor's version.
- The shared `VaultHybridSyncReview` offers recorded-hash verification for CREATE and Testnet UPDATE kinds. Delete remains unconfirmable: a manifest deletion needs tombstone/absence history and provenance evidence, not the create/update manifest-value proof. Do not enable DELETE confirmation with current historical inspector or infer success from a cleanup hash. Both hosts use the same Core + linked proof code.
- Public Mainnet/custom queued UPDATE/DELETE submissions remain disabled: the writer lacks ledger-enforced remote-version CAS and races remain between preflight and operation inclusion. Testnet is experimental. No build/tests run per previous user direction. Check C#/.NET10/MAUI compile, legacy result constructors/callers, warning-handling, proof fixture and Testnet cleanup races before release.

- Same batch: hardened `BrowserStellarLedgerRecords.ResolveAuthenticatedHdChildAsync` to require `StellarVaultStore.VerifyCurrentHdLineageAsync`, invoking the existing mnemonic-derived JS `inspectHdLineage` check after unique current-chain lookup. A matching chain snapshot alone is insufficient for deletion/update provenance.
