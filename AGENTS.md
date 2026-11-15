# AI coding guidance — StellarVault

Read [docs/ENGINEERING_HANDOFF.md](docs/ENGINEERING_HANDOFF.md) before modifying code. Update it with each meaningful repository change. Keep this file aligned with the product architecture and unresolved blockers.

## Current validation policy — supersedes older CI prohibitions (2026-11-15)

- User now **permits GitHub CI when useful**, but asked earlier to avoid unnecessary Actions/token use. One workflow, `.github/workflows/manual-verify.yml`, is registered on both `main` and this feature branch. Its ONLY trigger is `workflow_dispatch`: no push/PR triggers, no deployment, no network transactions. Do not add automatic runs without new user authorization.
- Select `architecture/pluggable-vault-records-and-chains` in Actions → Manual StellarVault verification → Run workflow. Build/test failures must be investigated from actual logs; do not assert that source parsing equals a successful .NET build.
- See `docs/verification/manual-validation.md` and `docs/architecture/implementation-reality-check.md`. Existing HD queue, file protocol, folder planner and verifier infrastructure must be reused instead of rebuilt. Ignore earlier historical instructions in this file saying GitHub Actions must *never* be recreated; they refer to the user's older instruction.
- Linked encrypted queue read-only inspection and foreign-scope rejection paths must zero decrypted draft buffers before returning or throwing.

## Architecture and product contract

- Shared .NET 10 domain/services: `src/StellarVault.Core`; PWA host: `src/StellarVault.Pwa`; reusable UI: `src/StellarVault.UI`.
- **Product architecture decision (2026-11-14):** two independent first-class *intended* credential/account strategies: BIP-39 SEP-5 mnemonic / deterministic HD child accounts, and imported Stellar S-key / linked independently created child accounts with explicit root public-key signer authorization. The latter is NOT an HD mnemonic substitute or a legacy-only migration. Reuse records/folders, encryption service contracts, on-chain review and hybrid workflows where safe, but do not cross their derivation/signing/discovery paths.
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

## 2026-11-03: encrypted deletion baseline + authenticated manifest-copy inventory

- New Core `VaultDeletionIntent` wraps the original published `LedgerRecordDraft` **and** all manifest-owning child G-addresses, encrypted as part of `VaultPendingMutation.EncryptedPayload`. New `IVaultLedgerManifestCopyInventory` host contract is implemented by the existing connected `BrowserStellarLedgerRecords` (also compiled by MAUI): enumerate `svh:<id>` keys from an HD chain snapshot with no warnings, then verify each owner's seed-derived lineage before returning a unique sorted owner list. It never uses a user-entered child address.
- On staging a delete, the shared Core `VaultHybridRemoteMutationWorkspace` reads owner accounts, rechecks the original record fingerprint and owner list before enqueue, and records both in the encrypted durable queue. `VaultPendingMutationQueue.PreflightRemoteMutationAsync` rejects new deletion intents without the envelope, a valid owner list or full original-draft content hash matching the pinned `ExpectedRemoteVersion`. Pre-existing legacy delete payloads remain diagnosable but cannot use the new source-coverage claims.
- Shared `VaultManualSyncCoordinator` checks fresh authenticated copy owners at eligibility review, transaction approval and immediately before submission reservation. This is a **preflight comparison**, not an atomic ledger-version condition: concurrent writers may still race after the last check, so queued remote writes remain Testnet-only.
- Core `VaultDeletionRecoveryReview` validates encrypted original-record fingerprint and owner set and rejects stale durable revisions. Host `HorizonVaultDeletionInspector` requires its recorded manifest-tombstone transaction to delete exactly the staged set of owner accounts (in addition to network, receipt and seed-derived child checks). UI shows explicit `StagedSourcesMatched`.
- **Deletion still never transitions Confirmed:** historical predecessor ciphertext provenance and atomic complete copy-set inclusion are not independently established by the stored snapshot and Horizon result. Do not change `EvidenceBasedVaultLedgerMutationVerifier` to confirm deletes based on this field. No builds/tests run by previous user preference; both hosts and Testnet must be validated.

## 2026-11-03: historical predecessor verification for staged deletion sources

- New PWA/MAUI-linked `HorizonVaultDeletionPredecessorInspector` performs a bounded, same-origin, read-only scan of each tombstone source account's descending operation history before its **specific tombstone operation ID**. It accepts only the nearest successful `svh:<recordId>` manifest WRITE; an intervening delete, malformed entry, pagination/response boundary failure or absent history fails closed.
- `HorizonVaultDeletionInspector.Finding` exposes the exact `TombstoneSource(AccountId, OperationId)` tuple only after a full, untruncated matching recorded transaction, strict source-set coverage and seed-authenticated HD lineage checks. No user-supplied source list may replace the encrypted staged intent.
- `HorizonVaultDeletionTombstoneReader` uses those operation IDs to find each predecessor manifest; it binds the candidate to the expected source and operation ID, resolves the seed-authenticated HD index and invokes the existing `HorizonVaultPublicationDiagnostic.InspectHistoricalRecordAsync` which authenticates network, registry, historical encrypted chunks and decrypted contents against the **original staged deletion record**. All staged children must pass. Core `VaultDeletionRecoveryReview` preserves queue/account/network/session CAS guard and returns diagnostic `HistoricalPredecessorMatched`.
- Shared UI now reports historic predecessor coverage independently from tombstone source-set matching/current absence. Both PWA and MAUI register the same predecessor inspector and linked adapter.
- **DO NOT enable deletion confirmation from this evidence**: even a positive per-source predecessor check is not an on-chain atomic expected-version condition, does not prove no unobserved contemporary manifest appeared on another HD child, and is only as trustworthy as the configured Horizon source. The queue stays `Uncertain`, `EvidenceBasedVaultLedgerMutationVerifier` remains create/update-only, Public/custom hybrid deletes remain blocked.
- Builds and tests not run per user preference; test malformed/truncated/history-pruned endpoints and cross-device simultaneous writes before enabling deletion confirmation.

## 2026-11-03: guarded last-boundary hybrid UPDATE/DELETE transport

- Added `IVaultGuardedLedgerMutations` to shared Core. `VaultManualSyncCoordinator` calls **only** `UpdateIfUnchangedAsync` or `DeleteIfUnchangedAsync` for approved queued remote changes; it will never silently fall back to the direct ledger CRUD writer. These carry the immutable `ExpectedRemoteVersion` fingerprint and, for delete, the encrypted intent's original HD manifest owner set.
- Both PWA and MAUI register the same `BrowserStellarLedgerRecords` instance for `IStellarLedgerRecords`, `IVaultLedgerManifestCopyInventory` and `IVaultGuardedLedgerMutations`. The transport's `UpdateCoreAsync`/`DeleteCoreAsync` retain public direct CRUD behavior and forward optional guarded inputs to the existing C# HD-v2 writer.
- `StellarVaultStore.RequireCurrentVersionAsync` re-reads/decrypts the full ledger record, rejects incomplete recovery, ambiguous/missing IDs and any fingerprint drift **inside the write transport**. Guarded DELETE also recomputes manifest-owning HD child accounts from a freshly loaded chain snapshot before building the manifest-delete writes and rejects a changed source set.
- This narrows stale-write time-of-check/time-of-use exposure but **IS NOT atomic CAS**. HD-v2 writes may span multiple root-funded Stellar transactions; concurrent devices can still interleave after the last check. Public/custom queued UPDATE/DELETE submission remains blocked, Testnet stays experimental, and uncertain outcomes remain no-retry. Direct on-chain flow remains backward compatible.
- Builds/tests not run per user direction. Validate browser/MAUI compilation and actual two-device conflicting Testnet transactions before any production enablement. Never describe this change as ledger-enforced version preconditions.

## 2026-11-03 — non-destructive offline update recovery and deletion quote repair

- Fixed shared `VaultHybridSyncReviewService` fee-review parsing for DeleteRecord/DeleteFolder: encrypted deletions use `VaultDeletionIntent(Original, ManifestSourceAccounts)`, not a bare `LedgerRecordDraft`. The service now resolves `intent.Original` for displayed title and `EstimateDeleteAsync`, while existing `PreflightRemoteMutationAsync` still validates the pinned fingerprint and original owner envelope. Invalid/legacy malformed payloads never get a paid quote.
- Hide queued remote update/delete approval quotes on Public and custom networks directly at the shared review service boundary (not just on the button/coordinator); experimental Testnet-only gate remains enforced in Core coordinator.
- Added `VaultHybridRemoteMutationWorkspace.PreserveUpdateAsNewRecordAsync` as a non-destructive offline recovery path for pending, version-pinned `UpdateRecord` only. It validates the saved payload, generates a titled "(offline copy)" variant, and calls the existing shared encrypted `VaultHybridDraftWorkspace.CreateAsync` to allocate a new ledger record ID and durable pending CREATE. It **never publishes, overwrites, rebases, or silently discards** the original update. The original must be inspected/discarded separately to clear the queue. Folder updates/deletions are intentionally excluded.
- Shared `VaultHybridRemoteChanges.razor` now exposes **PRESERVE AS NEW ENCRYPTED RECORD — NO OVERWRITE** in both PWA and MAUI. UI explains that this is a separately queued CREATE, not a remote change, and corrects stale text implying all remote update/delete submissions were disabled.
- This is a safe append-only workaround for preserving a user's *proposed* conflicting content, not an atomic conflict-resolution protocol or automatic merge. The current HD-v2 transport still lacks atomic conditional remote updates; Mainnet/custom queued updates/deletes remain blocked. Existing direct on-chain CRUD remains unchanged.
- No builds/tests run per user's prior preference. Validate PWA+MAUI compilation, deletion quotes, damaged payloads, safe-copy retention after queued write failures, and cross-device Testnet conflicts before production use.

## 2026-11-03: atomic local draft-parent revision and shared folder workflow

- Added Core `VaultPendingMutationQueue.RevisePendingCreateAsync` for local CreateRecord/CreateFolder edits: validates expected whole durable queue revision, payload, unique local dependency, parent record-kind/state/identity and ancestry cycle; commits the new serialized draft **and** `DependsOn` edge in a *single per-mutation* encrypted-store CAS. It forbids revision after submission and rejects stale editor/queue changes; creates no Stellar operations. This is an atomic local mutation replacement, **NOT a cross-entry/cross-device on-chain CAS**.
- `VaultHybridDraftWorkspace.ReviseAsync` now uses this method when changing parent folders (including moving to root, changing one pending parent to another, and switching from pending to confirmed parent). Previously this operation rejected all parent dependency changes; that made ordinary offline folder reorganization unworkable.
- Shared `VaultHybridDraftEditor.razor` exposes parent selection/clear, new child record/subfolder actions, parent folder IDs on local entries, and allows already-Confirmed folder entries to be parents. These controls run in the existing shared PWA/MAUI UI and preserve explicit local-storage consent and encrypted queue persistence.
- Pending local parents still BLOCK children until their own historical record is Confirmed; cycle/missing-dependency review remains fail-closed. The queue revision CAS cannot make a multi-entry folder tree transaction atomic, and cross-tab concurrent parent revisions could still leave a review-blocked cycle; do not automatically publish blocked entries.
- User preference: no builds/tests run; validate C#/.NET10 Razor compilation, queue CAS, folder parent edits/root moves, nested dependency confirmation and two-tab concurrent changes before production.

## 2026-11-03: stale-editor protection across shared offline record/folder editors

- **Data-loss fix:** Core `VaultPendingMutationQueue.RevisePendingCreateAsync` now requires the payload SHA-256 fingerprint supplied when the local create draft was opened, compares it with the current encrypted-queue plaintext payload fingerprint and still requires full durable-revision CAS. A browser tab with an older opened version cannot overwrite another tab's newer edit. Parent dependencies and revised payload still change in one per-mutation CAS.
- New `VaultPendingMutationQueue.RevisePendingUpdateIfUnchangedAsync` provides the same explicit source-payload fingerprint / durable store CAS for version-pinned pending UpdateRecord/UpdateFolder. It never changes the immutable `ExpectedRemoteVersion`, entity ID, network, or dependencies and never submits network writes.
- `VaultHybridDraftWorkspace.DraftItem` and `VaultHybridRemoteMutationWorkspace.StagedItem` now expose a payload revision digest (not the record's pinned remote baseline). Each editor captures that digest when opening an entry and passes it through Core on save. Stale editors fail closed instead of silently adopting the newest queue entry. The remote payload revision is distinct from `BaselineSha256`.
- Both shared Razor editors subscribe to `VaultSessionManager.StateChanged`, clear cached draft lists, editor fields and opened revisions on lock/account switch, and reload local encrypted drafts only for a matching session epoch. Both unsubscribe in Dispose. This reduces the chance of showing stale account contents after a local lock.
- The local queue still has no cross-device transactional lock; these changes protect individual local queue revisions, not atomic on-chain synchronization. Do not claim multi-device remote CAS. PWA/MAUI builds and tests deferred per user request; validate compilation, session events, two-tab stale-save behavior, and resumed queue states before production.

## 2026-11-03 — reconcile folder ID and durable dependency graph before Sync

- The shared `VaultQueuedCreatePolicy.Problems` now fails closed when an encrypted CreateRecord/CreateFolder draft's `FolderId` and `DependsOn` describe different local parents. Checks unique local parent IDs, actual CreateFolder kind, unavailable/failed/conflict parents, required exact pending parent mutation ID, orphaned dependency references, malformed parent IDs, and dependency ancestry cycles. An ordinary remote/confirmed parent requires no pending dependency (an existing matching confirmed-parent dependency is accepted for backward compatibility).
- `VaultPendingMutationQueue.ReviewReadyAsync` and `PrepareReviewAsync` both apply the same create policy, so mismatched, missing or cycled local folders cannot silently appear ready. Existing fee preflight already invokes this policy before publication. Children of pending parents remain blocked until the exact parent mutation is Confirmed.
- Shared `VaultHybridSyncReview.razor` now displays human-readable blocked reasons and explains whether to wait for independently verified parent publication or reopen the encrypted draft to repair parent selection. Both PWA and MAUI consume the same Core and Razor behavior. No automatic retries or ledger writes are added.
- Queue-local checks are not an atomic multi-entry storage transaction and cannot establish remote-version CAS across devices. No PWA/MAUI builds or Testnet runs performed per previous user instruction; verify compilation and recovery under parent revision/discard/confirm sequences before release.

## 2026-11-03 — guided parent-to-child hybrid Sync review

- New shared Core `VaultHybridFolderSyncPlan` builds a read-only account/network-scoped sequence over locally encrypted CreateFolder/CreateRecord mutations, using the same `VaultSyncReview.Ready/Blocked` policy as the queue. Distinguishes ReadyForReview, WaitingForParent, NeedsRepair, AwaitingVerification, Confirmed and NotPublishable. Follows queued folder ancestry with cycle bounds and never signs, estimates or broadcasts automatically.
- `VaultHybridSyncReviewService.Report.FolderPlan` is populated from the queue's full scoped inventory and current readiness snapshot. Both PWA and MAUI use the same shared Core service.
- Shared `VaultHybridSyncReview.razor` displays parent/child ordering, statuses, record IDs and concrete next action. The user can explicitly request fresh cost review for the next eligible entries; no child is submitted automatically. After a submission attempt, the component reloads its durable unresolved queue to show its state instead of clearing the UI. After authenticated recorded-hash confirmation, the same plan refresh shows newly eligible children.
- Added session StateChanged invalidation in the shared Sync review: stale approval snapshots and cached queue/proof results are cleared when the unlocked vault changes. Async load also checks the original session epoch and network scope before exposing results; on lock/switch no approval is retained.
- This is a guided manual workflow, not a cross-device atomic transaction or automated batch sender. Parent confirmation only means independent recorded-hash validation on this queue; a child's paid publication still requires a fresh remote inventory, quote, and separate explicit approval.
- No PWA/MAUI builds/tests run per previous user direction. Acceptance still needs actual parent-folder publish → independently verified confirmation → child fee review → child publish → confirmation plus interruption/lock scenarios.

## 2026-11-03 — interruption quarantine as a first-class manual workflow

- New `VaultPendingMutationQueue.QuarantineInterruptedIfUnchangedAsync(expected)` accepts an exact opened durable `Submitting` mutation and atomically transitions it to `Uncertain` only when the full scoped queue revision still matches; a stale UI cannot quarantine another tab's changed entry. It NEVER broadcasts, retries, deletes, or confirms a Stellar transaction. Existing legacy `QuarantineInterruptedAsync` remains for host recovery.
- Shared `VaultHybridSyncReview.razor` now exposes **QUARANTINE INTERRUPTED SUBMISSION — NO RETRY** for `Submitting` entries. The handler checks current account/network/epoch, verifies the queue entry has not changed, invokes the exact CAS operation, then refreshes unresolved entries and the shared parent-child Sync plan. No hash is inferred; a missing hash still requires independent history investigation.
- `VaultHybridFolderSyncPlan` no longer describes unrelated children as ReadyForReview when any other submission is `Submitting`/`Uncertain` or a queue conflict globally blocks further paid Sync. Such steps show AwaitingVerification or NeedsRepair; they become eligible only after queue reconciliation and a new explicitly approved quote. Parent-first ordering and existing create confirmation semantics remain.
- Both hosts consume the same Razor/Core workflow; direct on-chain CRUD unchanged. Quarantining is NOT confirmation and does not lift the global unresolved submission gate. No PWA/MAUI build or Testnet run performed per user's standing preference.

## 2026-11-04 — bounded hashless hybrid submission investigation (read-only)

- New Core `IVaultUnrecordedSubmissionReader`, `VaultUnrecordedSubmissionFinding`, and `VaultUnrecordedSubmissionReview` support investigation when an existing durable CreateRecord/CreateFolder or Testnet UpdateRecord/UpdateFolder is Submitting/Uncertain **without a recorded hash**. The Core service snapshots the exact scoped queue mutation, unlocked session epoch, network and endpoint, runs host history review, then rejects findings if the revision/session/network changed; NEVER attaches a discovered hash or transitions queue state.
- New shared PWA/MAUI `HorizonVaultUnrecordedSubmissionReader` inspects a **bounded latest root-funded Stellar transaction window** (at most 32 of the newest 100 transactions), rejects malformed, redirected, oversize and out-of-order Horizon responses, checks manifest write/source, connected seed-authenticated HD child and full historical registry/chunk/decrypted-record equality to the approved queued draft. It reports all matching historical transaction hash candidates. **No candidate is uniquely attributable to the interrupted mutation without the originally durable transaction identity.** Results are investigation leads only, always `SearchComplete=false`, and zero matches does NOT establish failure.
- Shared `VaultHybridSyncReview` offers `INVESTIGATE MISSING TRANSACTION HASH — READ ONLY` for supported kinds and explicitly labels any candidate `NOT QUEUE CONFIRMATION`. Candidates never enter `ConfirmReconciledAsync` and cannot authorize retry. Hashless DELETE and file mutations remain unsupported; network receipt history or eventual contracts must provide stronger provenance to resolve them.
- PWA DI and native MAUI DI plus linked source registration reuse the same Core/host implementation. Direct on-chain flow unchanged, Testnet-only queued UPDATE restriction preserved.
- No builds, Testnet calls or device tests run per user request. Before production, validate Horizon paging-token behavior, bounded historical availability, actual missing-hash crash fixtures, browser and MAUI compilation and session races. Missing original hashes still block Sync indefinitely until an independently trustworthy manual recovery protocol exists.

## 2026-11-04: signed manifest transaction hash checkpoint before hybrid CREATE broadcast

- Added Core `IVaultPreparedCreateLedger` as a distinct hybrid-only create transport contract; the existing direct on-chain `IStellarLedgerRecords.PublishWithIdAsync` remains unchanged. Both PWA and MAUI DI resolve `IVaultPreparedCreateLedger` to the same connected `BrowserStellarLedgerRecords` adapter, not another signing session.
- The JS HD-v2 sponsorship helper now exposes a strictly optional `onSignedTransaction` callback for a fully signed ManageData transaction. In the hybrid create path, `stellarVault.stellar.submitHdManageData` requires a single `svh:<recordId>` manifest write and awaits `DotNetObjectReference<SignedManifestCheckpoint>.CheckpointSignedManifestAsync(signedHash)` **after transaction signing, before Horizon submission**. A failed callback prevents final-manifest broadcast. All earlier encrypted chunk writes retain their existing transaction flow and are NOT treated as the record's commit.
- `StellarVaultStore.AddAsync` can now receive an optional signed-manifest callback, routed only to the batch containing the final manifest write. The `SignedManifestCheckpoint` JS interop receiver is single-use and validates lowercase 64-hex hashes.
- `VaultPendingMutationQueue.CheckpointSignedCreateManifestAsync` CAS-persists that exact prepared hash into the encrypted durable `Submitting` mutation. Must match the full queue revision, remain hashless, and be CreateRecord/CreateFolder. It never confirms a transaction or changes to Uncertain. The JS callback fails closed if encrypted storage cannot persist it.
- `VaultManualSyncCoordinator.PublishApprovedCreateAsync` now exclusively uses the prepared-hash interface for queued CREATE, updates its locally held durable revision after the hash checkpoint, then marks the same revision Uncertain after the JS return/failure. A lost Horizon response cannot erase a checkpointed hash; historical confirmation still requires the independent HD/registry/chunk/decrypted proof. A transaction may have been signed/checkpointed without landing, so never replay it automatically.
- **Important limits:** Only hybrid CREATE is checkpointed. Queued UPDATE/DELETE still lack a pre-broadcast primary-transaction hash hook and remain experimental Testnet-only with no atomic version condition. A crash before final-manifest signing can still leave a hashless Uncertain mutation after earlier chunk writes or child allocation. Builds and live Testnet not run under user's previous direction; validate JS/.NET interop callbacks, checkpoint durability, device host packaging, Horizon receipt hash equality, and cross-device recovery before production.

## 2026-11-04: signed primary update manifest checkpoint (Testnet hybrid only)

- Added Core `IVaultPreparedUpdateLedger` to checkpoint the exact fully signed primary UPDATE manifest transaction hash before the JS signer sends it to Horizon. Both PWA/MAUI DI resolve this interface to the same already-connected `BrowserStellarLedgerRecords` adapter used by direct CRUD; direct `UpdateAsync` remains backward-compatible.
- `StellarVaultStore.UpdateAsync` accepts an optional signed-manifest checkpoint callback; the existing `SubmitWritesAsync` forwards it ONLY for the commit batch containing the final `svh:<id>` write. Encrypted chunk writes happen first and obsolete copy cleanup happens after commit. The JS sponsor awaits the durable checkpoint before primary-manifest broadcast; an exception blocks that manifest's broadcast.
- `VaultPendingMutationQueue.CheckpointSignedPrimaryManifestAsync` extends the create-only durable compare-and-swap to queued CreateRecord/CreateFolder/UpdateRecord/UpdateFolder in `Submitting` with no previous hash. Existing create API is retained as a delegating alias. DELETE is deliberately excluded because its multi-copy tombstone protocol may need multiple manifest-delete transactions and one recorded hash cannot authenticate their full set.
- `VaultManualSyncCoordinator.PublishApprovedRemoteMutationAsync` uses `preparedUpdates.UpdateIfUnchangedWithPreparedHashAsync` for Testnet UPDATE, snapshots the newly checkpointed exact queue revision, and marks it `Uncertain` after a receipt or unknown failure while preserving the signed hash. It NEVER records the later cleanup hash in lieu of the manifest hash. Existing guarded DELETE remains unchanged and no auto-retries are introduced.
- These changes **do not** provide atomic ledger compare-and-swap against another device, do not guarantee no partial orphaned chunk writes before final manifest signing, and do not authorize Mainnet/custom queued updates or deletes. Existing historic create/update verifier remains the only path to `Confirmed`. Build/test/real Testnet validation not run under user preference. Validate JS callback reentrancy, durable storage failures, post-checkpoint Horizon errors, exact-hash reconciliation and PWA/MAUI compilation.

## 2026-11-04: encrypted signed deletion manifest journal

- Queued DeleteRecord/DeleteFolder now has an optional backward-compatible SignedDeletionManifests array of owner/hash pairs, sealed in the existing account/network-scoped encrypted mutation entry. Both native file and PWA IndexedDB CAS comparators check the journal.
- The new CheckpointSignedDeletionManifestAsync validates the immutable staged VaultDeletionIntent owner set, unique source and signed hash, full durable Submitting revision, then CAS-appends the checkpoint before a specific manifest tombstone is broadcast. Changes in other tabs abort that tombstone submission.
- New IVaultPreparedDeletionLedger is implemented by the shared connected BrowserStellarLedgerRecords adapter and registered by both hosts. StellarVaultStore.DeleteAsync forwards an optional per-owner callback to submitHdManageData; the JS sponsor awaits the callback for each signed svh:<recordId> deletion transaction before sending it to Horizon. Direct CRUD retains its original path.
- VaultManualSyncCoordinator uses the prepared deletion transport on Testnet and tracks the updated durable mutation revision. The legacy TransactionHash remains null for journaled deletes because one hash cannot represent all HD owner tombstones. The shared Sync UI displays each prepared source/hash with NOT CONFIRMED warnings.
- Important: a signed hash is not a successful receipt, a complete multi-owner deletion is not atomic, partial commits are possible, and replay remains prohibited. Historic per-owner receipt verification and a ledger-enforced version protocol still need implementation. Mainnet/custom queued update/delete remains blocked.
- No builds/tests or Testnet transactions performed per prior user direction. Validate both encrypted storage CAS round-trips, JSInterop callbacks, partial broadcasts and session interruptions before release.

## 2026-11-04: read-only per-owner historical verification of signed deletion journal

- New shared Core `VaultDeletionJournalReview` examines a Submitting/Uncertain queued deletion with optional encrypted `SignedDeletionManifests` entries. It authenticates the original `VaultDeletionIntent` (including exact staged HD manifest owners and fingerprinted original record), checks unique owner/hash entries and validates complete session/network/queue revision before returning evidence. Nothing is submitted, retried, reattached to a queue hash, or marked Confirmed.
- It reuses the **existing** `IVaultDeletionTombstoneReader` for each journaled owner/hash with a singleton expected owner set. This verifies the successful historical tombstone transaction, expected HD child source, previous historical manifest, seed-authenticated registry/chunks, and approved decrypted predecessor via the existing PWA/MAUI evidence stack. Each owner is labelled VerifiedHistoricalTombstone, UnresolvedTransaction, or NotCheckpointed. The overall report includes counts, current absence, and a deliberately non-authoritative AllStagedOwnersHistoricallyVerified indicator; `IsQueueConfirmationProof` is always false.
- Both hosts register the new Core service. Shared `VaultHybridSyncReview` exposes `VERIFY PER-OWNER DELETION HISTORY — READ ONLY`, displays each HD owner/hash/status and missing owner, and explicitly states the evidence is NOT atomic deletion or queue confirmation. Results are discarded on vault session changes. The underlying queue and transaction writer remain untouched.
- Release blockers: multi-transaction deletion is not atomic against another wallet; history/current absence is not proof of a ledger-enforced version precondition; no automatic replay/confirmation; Mainnet/custom update/delete remain disabled. PWA/MAUI builds and Testnet failure-injection runs not executed per prior user preference.

## 2026-11-04 — deletion journal evidence versus live inventory, distinct facts

- Tightened HorizonVaultDeletionInspector: a successful tombstone receipt is now rejected unless its transaction source is exactly the connected/staged HD root account. Authenticated child operation source alone does not establish who paid/signed the surrounding transaction.
- VaultDeletionJournalReview no longer merges the "currently absent" observations returned by each per-owner historical investigation. Those reads may occur at different times while a second device writes. It performs **one separate final diagnostic current inventory** through the connected root-bound IStellarLedgerRecords and IVaultLedgerManifestCopyInventory interfaces. The snapshot is explicitly advisory, not atomic, and never changes queue status or authorizes retries.
- The review distinguishes CurrentSnapshotAvailable, CurrentRecordAbsent, CurrentManifestOwners and UnexpectedCurrentOwners. Unexpected owners are accounts with current manifests not in the immutable staged VaultDeletionIntent owner set; they indicate possible concurrent/recreated data that an earlier deletion approval did not cover. If current read fails, it reports state unknown, not absent. Signed per-owner historical receipts and predecessor authentication are independently reported even without a fresh snapshot.
- The shared PWA/MAUI Sync screen reports unknown current inventory and unexpected HD owners prominently. AllStagedOwnersHistoricallyVerified still means only immutable historical evidence; IsQueueConfirmationProof remains false. Mainnet/custom queued update/delete blocks, manual approval and no-replay remain unchanged.
- No builds, unit tests, or live Testnet runs performed per prior request. Acceptance requires source compilation and Testnet scenarios with concurrent writes during forensic inspection, root fee-payer mismatch, unavailable Horizon and recreated manifest owners. Current inventory is time-of-read evidence, not a ledger-enforced CAS.

## 2026-11-07 — signer-side optimistic HD manifest byte guards for queued writes

- Hybrid Testnet UPDATE now passes the exact base64 ManageData manifest values of every old HD record copy, as observed in the last authenticated C# LoadChainAsync snapshot, to the primary manifest write. Immediately before signing that write, hd-sponsorship.js reloads those source accounts from Horizon and requires every svh:<recordId> value to equal its expected byte string. If moving the final manifest to a new child, it also requires the destination manifest key to remain absent.
- Hybrid Testnet DELETE passes the exact pre-deletion manifest value for each per-owner tombstone. The same signer checks the intended HD child value immediately before signing/broadcasting that tombstone. It is not applied to later orphan-chunk cleanup. A changed or previously deleted manifest blocks the signed transaction rather than silently deleting a newer version.
- The optional guard properties flow from StellarVaultStore.JsManageDataWrite into existing submitHdManageData and writeSponsoredData; guards apply only to the prepared hybrid manifest phase and are not added to direct on-chain CRUD. All existing pre-broadcast durable signed hash checkpoints and no-retry reconciliation rules remain.
- **Not atomic:** the checking Horizon read can race before ledger inclusion. A different wallet can change values between the final compare and the signed operation. This is defense in depth, NOT a ledger-level CAS or cross-device synchronization protocol. Multi-owner delete remains non-atomic; Mainnet queued UPDATE/DELETE remain disabled.
- No build, runtime or Testnet verification performed per user request. Required acceptance includes changed-manifest rejection, new-target occupancy, same-owner update, cross-owner move, multi-owner deletion where an earlier owner is already deleted, and PWA/MAUI interop serializer casing.

## 2026-11-07 — root-sequence fence for HD-v2 sponsored writes

- `writeSponsoredData` now loads and validates the root Horizon account FIRST, then loads the target HD child and any other manifest guard accounts sequentially. It no longer fetches root and child in `Promise.all`. The root account is the Stellar transaction source and its loaded sequence is embedded by `TransactionBuilder(root)`.
- This ordering creates a useful sequence fence for ordinary StellarVault HD-v2 writers: if another app device commits a root-sourced transaction after this root snapshot, the current transaction carries the old root sequence and Horizon/Core rejects it as stale (`tx_bad_seq`). If it committed before the root snapshot, subsequent child/manifest reads should observe the newer ledger state and the exact byte guards reject the stale manifest intent. Existing HD-v2 sponsored child writes, account allocation and registry initialization also use the same root as transaction source.
- The fence is per root account and only serializes transactions that use that root as their transaction source. A client that signs a child-sourced transaction directly, a separate writer that bypasses the root-source protocol, inconsistent/stale Horizon reads, or a race outside the ordered read/sequence assumptions can bypass the guarantee. This is **not a Stellar conditional ManageData operation** and is not universal ledger-enforced compare-and-swap. Do not describe it as atomic cross-device protection.
- No retry behavior was added. A stale-sequence rejection still enters the existing conservative Uncertain/reconciliation path; do not blindly replay. Mainnet queued UPDATE/DELETE remain disabled pending validation and a stronger protocol.
- No builds, JS tests, or Testnet runs performed. Verify with two independent unlocked devices: same root sequence concurrent updates, stale delete after update, create/move destination collision, unrelated root-sourced transaction, and root/child Horizon read ordering. Confirm a tx_bad_seq never leads to automatic retry.

## 2026-11-07 — shared encrypted hybrid file staging (PWA + MAUI)

- Added `VaultHybridFileWorkspace` in Core. It stages a bounded (1 MiB) file as one encrypted account/network queue mutation of kind `UploadFile`. The durable intent contains filename/content type/optional parent folder, original bytes, SHA-256 fingerprint and a collision-checked stable ID inventory for every SVFILE2 part and the final manifest. IDs are allocated once and the plan is reproducible after restart; bytes are held only in memory until the host queue adapter encrypts the serialized mutation. No ledger call occurs during staging.
- Reconstructed plans are validated against the original bytes/fingerprint, unique 16-hex IDs, entry count and queue manifest ID. Corrupt existing upload intents block allocation of new file IDs. The manifest ID is the queue entity ID; pending local folder parents become explicit queue dependencies and wait for independent folder confirmation.
- Added shared `VaultHybridFileDraftEditor`. MAUI uses the existing native file picker adapter; PWA now has `BrowserVaultNativeFileAccess` with a transient browser picker and a zeroing memory stream. PWA and MAUI both register the same Core workspace. The PWA VaultHome now exposes the same shared Hybrid Sync workspace (records, files, remote changes, review) already used by NativeVaultWorkspace.
- Existing local-only `VaultBinaryWorkspace` and old Stellar upload checkpoint coordinator remain distinct. This commit adds encrypted queue staging only; it does NOT publish file parts. The Sync review explicitly says per-part approval, signed pre-broadcast checkpoints, manifest-last publication and independent historical confirmation are not yet connected to the hybrid queue. File deletion is also not implemented in this workflow.
- No builds/tests/browser/MAUI or Testnet transactions run per standing user instruction. Validate picker cancellation and native file access, 1 MiB boundary/oversize streams, encrypted queue roundtrip/restart, ID collision/corruption, folder dependency, session switch and PWA/MAUI UI parity before proceeding.

## 2026-11-07 — hybrid file staging memory and identity hardening

- File staging now derives the exact entry count from the bounded file length and allocates collision-checked stable IDs directly; it no longer materializes all chunk draft strings merely to discover IDs.
- The temporary staged-byte copy is zeroed immediately after serializing the intent. The queue payload copy is zeroed after the host store has accepted it. Decrypted queue snapshots used by staging and plan reconstruction are cleared after use. File listing validates the SHA-256 and expected entry count without rebuilding every chunk; chunk strings are materialized only when GetPlanAsync is explicitly called.
- StageAsync returns a metadata-only FileDraftItem rather than the queue mutation containing the plaintext serialized file intent. This reduces accidental retention of file payloads in UI/component state.
- Still no file publishing path; no build/tests/runtime validation.

## 2026-11-10 — durable file-part checkpoint journal and read-only first-entry preflight

- Queued UploadFile now supports optional encrypted SignedFileEntries records (immutable ledger record ID and exact fully signed transaction hash). CheckpointSignedFileEntryAsync accepts only an exact Submitting queue revision, validates the encrypted file content fingerprint and original ordered part/manifest IDs, requires the next entry in sequence, prohibits duplicate hashes, and atomically appends through encrypted store CAS. Failure MUST abort that transaction before broadcast. A signed hash is not a receipt and journal presence never authorizes replay.
- Updated BOTH IndexedDB and native file-store durable revision comparators plus Core deletion/recovery same-revision checks so a stale browser tab or host session cannot overwrite the file journal. An unsubmitted queue item cannot be enqueued/discarded with a prepared file journal.
- Shared VaultHybridFileWorkspace now exposes signed entry identities and the next immutable ID, validates journal ordering, and flags corrupt journal/intent metadata. Added PreviewFirstEntryAsync as an explicitly READ-ONLY inspection of a pending, untouched file: matching unlocked HD root, no unresolved queue, parent folder currently on ledger, fresh collision-free remote record ID inventory, and fee estimate for the FIRST file entry only. This preview does not capture a financial approval snapshot, reserve, sign, or broadcast anything.
- The PWA/MAUI shared file draft panel displays prepared hashes and no-retry warnings, next IDs, and an optional read-only first-entry estimate. Previews are cleared on queue refresh and session switch.
- Still missing: approval and reservation for each part, JS pre-broadcast callback hookup, transaction receipt verification for each checkpoint, manifest-last commit after all parts are independently verified, and restart continuation policy. All writes remain disabled for hybrid UploadFile. No .NET builds, device tests or Testnet submissions were run by preference.


## 2026-11-10 — Testnet-only hybrid file manual publisher and final reconciliation

- New Core VaultHybridFileWorkspace.QuoteNextFileEntryAsync and PublishApprovedFileEntryAsync implement a separate, short-lived per-entry cost approval. Only an untouched Pending file or a previously signed Uncertain file can be reviewed. Later entries require every earlier signed hash to have independently authenticated historical HD/ciphertext evidence and an identical contiguous current ledger snapshot.
- File publication reuses IVaultPreparedCreateLedger.PublishWithPreparedHashAsync for each record. VaultPendingMutationQueue.ReserveFileUploadAsync or ReserveVerifiedNextFileEntryAsync performs durable CAS before signing; CheckpointSignedFileEntryAsync records the signed record ID and exact signed hash **before** JS broadcasts. Every attempt is quarantined as Uncertain; no auto-retry or auto-advance. The last manifest is never published unless all earlier parts are independently verified. Only Testnet is enabled pending release checks.
- ConfirmCompletedFileAsync verifies every immutable signed entry historically, checks current exact plan visibility including manifest, and CAS-marks the UploadFile queue Confirmed. The shared file panel now offers per-part explicit fee approvals and final no-broadcast reconciliation. The on-chain direct CRUD path was not modified.
- New workflow change runs shared UI/PWA build on main pushes. This session did not execute local dotnet or live Testnet; CI completion is a separate observation. Outstanding: validate CI, historical evidence per-part on live Testnet, crash and loss-of-receipt recovery, concurrent device writes, MAUI builds, production safety review, hybrid file deletion, and Mainnet enablement decision.


### 2026-11-10 CI finding (main push)

- GitHub Actions run 42792765911 on commit ad52b15 passed .NET 10 shared UI/Core build, .NET 10 PWA build, PWA publish, PWA asset validation and JavaScript parsing.
- The subsequent bundled Node regression-test stage failed with 11 assertions (including legacy MAUI asset-path expectation, PWA navigation/layout snapshots and recent-vault storage expectations). These failures have NOT been triaged as pre-existing versus newly caused, and must not be ignored or bypassed. Since that stage failed, .NET crypto tests and release packaging were skipped in this run. Android preview was skipped by push-event workflow policy.
- Mainnet hybrid file uploads stay disabled. Full historical Testnet proof, interruption/restart scenarios, multi-device concurrency, native MAUI compilation, integration tests and the Node suite must pass before declaring the feature production ready.


### 2026-11-10 verified CI follow-up

- Historical run failures were addressed by fixing the incorrect SEP-53 verifier's canonical prefix+raw-message hashing (official vector now passes), and updating outdated Node test fixtures/expectations to reflect current PWA/MAUI packaging, unlocked session behavior, and authenticated history provider. No production security checks were disabled.
- GitHub Actions run 42793953386 on commit 114f56d5 completed **successfully**: Core/UI build, PWA restore/build/publish, static asset checks, JS parse, .NET crypto vectors, Node browser/native asset regressions, and PWA release packaging. The shared multipart ID reconciliation now rejects case-aliasing hex IDs.
- Added multipart Core crypto regression coverage at commit 26f136d5: stable ordered file identities, missing chunks, manifest-last semantics, current payload mismatch, case-alias duplicates. Its newer workflow run is a separate acceptance check.
- Hybrid manual file publishing implementation is now present end-to-end in source, but **Mainnet is disabled**. The remaining release gates are live Testnet per-part signing and crash recovery (with historical evidence), real PWA/MAUI host interop, native MAUI build, multi-device concurrent writes, browser permission flows, security review and hybrid file deletion. Never conflate green PWA CI with verified production safety.


## 2026-11-10 — hybrid published-file deletion staging (implementation only)

- Added Core VaultHybridFileDeletionWorkspace, registered identically in the PWA and MAUI scoped service hosts. The shared file editor can browse currently published SVFILE1/SVFILE2 records, stage a complete encrypted DeleteFile queue intent, inspect its latest ledger consistency and discard only an untouched pending draft. No ledger transaction is signed, submitted, or estimated during staging.
- File deletion snapshots bind the original manifest and every referenced chunk, original record content and metadata, filename and SHA-256 decoded file fingerprint. Duplicate/ambiguous IDs, incomplete file content, malformed manifests, shared chunk IDs and competing active local queue operations fail closed. The workflow recaptures chunk ownership after network I/O and checks immutable state at inspection time.
- DeleteFile **does not have a signer/journal/reconciliation publisher** yet. The shared UI explicitly says deletion signing is unavailable. Do not adapt generic DeleteRecord signing or infer full-file deletion from absence of the manifest. A complete multipart delete needs per-entry source-owner lineage, durable signed tombstone identities, independent historical receipts, no-replay and final absence review.
- Per explicit user direction, no builds, tests, CI inspection or Testnet transactions were executed for this implementation. Release verification remains deferred until the development work is complete.


- The generic PendingMutationQueue.ReviewReadyAsync/PrepareReviewAsync now explicitly blocks DeleteFile as an unsupported paid Sync action. It remains present for encrypted staging and read-only checks, but cannot appear as a ready generic mutation. This prevents accidental routing through the ordinary record/folder publish coordinator while the multi-record tombstone journal is unfinished. Commit f281a974.


## 2026-11-10 — Product scope decision: Hybrid files DISABLED

Per product owner direction, **Hybrid mode is records/folders only**. Direct on-chain SVFILE functionality is unchanged. File selection, encrypted staging, multipart upload, paid file-part quoting/broadcast and remote-file deletion staging are unavailable in Hybrid mode, on both PWA and MAUI. Shared UI removes the hybrid file editor and explains that file storage belongs to the direct on-chain path.

Shared Core `VaultHybridFilePolicy.RequireEnabled()` is called at the queue enqueue/reservation/file-hash-checkpoint boundary, upload Stage/Quote/Publish and file-deletion staging. Queue Ready/PrepareReview explicitly blocks UploadFile and DeleteFile, so no generic Sync coordinator can accidentally treat a legacy file mutation as publishable.

**Recovery of old drafts:** Existing encrypted UploadFile/DeleteFile queue entries are retained, never silently discarded/migrated. Historical read-only inspection and already signed upload confirmation remain available in Core for deliberate recovery, but fresh file publishing and automatic replay are blocked. The ordinary on-chain file path is NOT blocked.

**Scope/release:** Existing experimental hybrid file implementation stays in code behind a hard-disabled product policy rather than being removed, to protect old journals and enable an explicit future decision. Do not enable by local setting. This change deliberately was not built or tested per current user instruction. Continue implementing hybrid records/folders and host interop; reserve validation until implementation is finished.


## 2026-11-10 — Records/folders Hybrid implementation freeze candidate

Product scope and deliberate restrictions: see `docs/HYBRID_RECORD_FOLDER_RELEASE_SCOPE.md`. Hybrid files are disabled in both hosts/Core. Mainnet-capable Hybrid Sync covers explicit CREATE record/folder; staged UPDATE/DELETE signing is still Testnet-only due lack of a Stellar-enforced optimistic version CAS; multi-owner DELETE does not have a safe confirmation transition.

This implementation-only pass added: strict unlocked-session epoch/network binding in `VaultHybridDraftWorkspace` across create/revise/list/discard; corrupt local create drafts now appear as noneditable warnings in the shared editor instead of disappearing; `VaultHybridSyncReviewService` requires a matching unlocked session and connected HD root before fee quoting; `VaultQueuedCreatePolicy.ParentCandidates` aligns create/revise dependency resolution and ignores stale confirmed non-create mutations without dropping the original confirmed folder dependency; generic remote update preflight rejects reserved SVFILE payloads; remote conflict review and Sync messaging now describe the real Testnet-only policy rather than stale 'not enabled' text.

No manual builds, tests, CI inspection, device runs, or Testnet writes were performed for this pass, per user direction. Repository push triggers may independently run existing GitHub Actions. Do not call this production-ready; handoff is for focused validation of the scoped feature set. If results identify errors, return to implementation.


## 2026-11-14 — Secret Key vault Phase 1 (read-only)

Read `docs/SECRET_KEY_CHAIN_OPTION.md` and the latest `docs/ENGINEERING_HANDOFF.md` before adding account-chain writes. Phase 1 introduced `VaultCredentialMode` / `VaultAccessCredential`, `IStellarSecretKeyVaultDiscovery`, `VaultSecretChainProvisioningPlanner`, and independent `vault-secret-chain.js` read-only inspection, bundled and DI-registered in PWA and MAUI. Both entry screens expose the explicit credential selection, but S-key currently allows **read-only inspection only**. S-key signing, record decrypt/read/write, safe atomic child provisioning and hybrid sync are NOT implemented. Existing HD `IStellarLedgerRecords.ConnectAsync` remains strictly mnemonic.

The JS inspector enforces S-key checksum, network passphrase, bounded linked-account traversal, no cycles, root signer thresholds and rejection of roots with `sv:registry:v2`. Supported pointer names `sv:next:v1` and `sv:next` are provisionally interpreted as base64 ASCII Stellar G-addresses, NOT verified compatibility with all historical versions. Never treat an address pointer as signing permission, ignore unknown recovery formats, infer access from only a current snapshot, auto-retry an uncertain transaction, or persist/log an S-key. All future network-mutating paths require exact signer policy, child master-weight rules, reserve/fee consent, receipt plus independent historical confirmation and clean-device recovery proof.

Phase-1 mock JS cases were exercised; the committed Node tests and .NET/MAUI builds have not been run in CI/device during this pass. Do not claim the second mode is production-ready.


## 2026-11-14 — Dual architecture Phase 2 supersedes earlier read-only milestone

The earlier Phase 1 S-key inspection-only status in this file is a historic milestone, not the latest implementation state. **Phase 2 Testnet implementation is committed**, but does not yet satisfy product-wide feature parity or production gates. Review `docs/SECRET_KEY_CHAIN_OPTION.md` and `docs/ENGINEERING_HANDOFF.md` for exact behavior and shortcomings.

The new independently versioned Secret Key protocol is implemented in `src/StellarVault.Pwa/wwwroot/js/vault-secret-ledger.js` and exposed through `IStellarSecretKeyLedger` / `BrowserSecretKeyLedger`. Its imported S-key is not handed to HD services; it derives independent AES-GCM encryption, stores `skh:/skc:` encrypted manifests/chunks in one reviewed atomic transaction, manages root-authorized independent child accounts through explicit user-approved CreateAccount+SetOptions+ManageData, and rejects mainnet mutation. The shared `VaultSecretKeyWorkspace` in UI serves PWA `/secret-vault` and MAUI. Script and adapter asset/DI registration must stay synchronized in both hosts. Both scripts enforce signer policy/network binding and fail-closed recovery.

**Hard blocker warnings:** unknown legacy `svh:/svc:` record formats are not auto-migrated; account-chain registry authentication and independently verified historical evidence are not yet production complete; Stellar has no native ManageData compare-and-swap; existing HD hybrid queue, binary files and platform file workflows are **not** automatically applicable to S-key sessions. Do not route a Secret Key queue to `IStellarLedgerRecords` (HD), store S-key plaintext, automatically retry uncertain writes, or enable Mainnet based solely on mocked tests. Signing, crypto, recovery, and account provisioning need audited Testnet validation before promotion. `tests/secret-key-ledger.test.cjs` contains mocked ledger workflow coverage; test runner, .NET 10 compilation and real devices were unavailable during this pass.

## Pluggable design direction (2026-11-14)
- See `docs/architecture/pluggable-vault.md`. Account provenance, record logical type, storage mode, and blockchain transport are independent; do not conflate MIME extension detection with safe file preview.
- The new account/record factories are extension contracts and legacy MIME classification only, not wired multi-chain support. Preserve historical ciphertext layout and do not enable S-key Mainnet writes or sync broadcasts.
- Future sharing must never export account/seed/master encryption keys; future import/export needs authenticated versioned envelopes and conflict-safe folder mapping.

## Priority clarification — 2026-11-14
- EVM is **last priority** and only motivates clean interfaces; do not build EVM functionality now. Complete and validate existing Stellar HD and independently linked S-key paths, common record/file handling and safe synchronization first; sharing and portable import/export come later.
- The local record type factory is actively used for new text/binary saves. The account-strategy factory is **not** a live provider; `VaultConnectedStellarAccountInventory` only inspects already-connected sessions. No implicit account creation or transaction broadcast via factory calls.

## 2026-11-14 integration note
- Registered Stellar HD and linked provisioning-strategy implementations are read-only for connected inspection. CreateNext and per-node Discover fail closed; linked UI validates inventory via factory without bypassing transaction approval.
- Record type factory now validates HD and linked on-chain drafts as credential/folder types. Existing on-chain serialization remains unchanged.
- Local binary text/plain files are rejected until file-vs-text discriminator migration exists. Changes are not build/test verified.

## Reviewed linked expansion milestone
- Linked chain expansion now routes through `VaultLinkedAccountExpansion`, retaining exact quote/approval and root/network scope; it consumes pending intent before the network operation, never retries an uncertain submission, and checks connected count/tail after. This is **not** production-safe cross-device deduplication or proof of historical signer authorization.
- Do not replace the explicit S-key quote UI with an unapproved generic factory CreateNext; keep existing HD transaction behavior untouched. EVM is last priority. This work has not been compiled or device-tested.

## Storage-driven chain capacity rule
- The linked S-key UI must not require users to request another account. `VaultLinkedRecordWritePlanner` handles a typed create-only storage-capacity signal from the Stellar JS planner, preparing an expansion quote as part of the requested record save. The extra account and the record write each require their own explicit user-approved quote and verified outcome; never automatically broadcast the second transaction.
- Do not treat insufficient funds, MAX_OPS, update-account exhaustion, or maximum chain length as permission to allocate another account. These require explicit failure or a separately audited migration/large-record protocol. Preserve in-memory draft handling and no-blind-retry uncertainty.
- This integration is source-only, not build/run validated.

## Linked-account safety update (2026-11-14)
- Reserve one available tail ManageData slot for the next sv:next:v1 link when planning *new* records. Never extend a 1000-entry tail or allocate a child for a record that already exceeds the atomic transaction limit.
- On submission uncertainty, the linked JS session and UI block further mutations. Read-only Refresh cannot unblock them. Cross-session durable reconciliation remains unimplemented and is required before production release.
- Regression test source cases were added but not run; never report test pass without executing. EVM is still last priority.

## Read-only linked submission diagnostic
- Linked S-key JS transport now retains a volatile public-only unresolved submission snapshot, exposed through InspectUncertainSubmissionAsync and read-only UI. No signing material, plaintext, ciphertext or original draft enters this diagnostic.
- This is not a durable journal or independent historical proof; a missing snapshot must not be treated as completed or failed transaction. No tests/builds executed in this batch.

## Linked recovery inspection (2026-11-14)
- `VaultLinkedRecoveryInspector` is registered in PWA/MAUI and observes current linked account tail or record visibility for an uncertain submission without any mutation. Its result is **not historical proof and cannot clear a write block**.
- Existing encrypted hybrid queue requires an independent local vault key and device consent; do not persist S-key vault unresolved diagnostics using that queue without proper isolation and an audited key lifecycle.
- No builds/tests executed, no durable linked journal. EVM last priority.

## Linked uncertain-recovery boundary — 2026-11-14
- Same-runtime S-key lock/reopen no longer drops an unresolved public-only marker; connected UI checks this before enabling writes. Full reload and other devices are NOT protected. Never claim durable recovery.
- Read-only Horizon-linked receipt adapter checks selected HTTPS network identity, receipt and root source. It is advisory and cannot confirm exact operation or record, cannot clear quarantine, and never submits. Native host explicitly links adapter. Tests were added but not executed.
- Keep explicit local-storage consent and independent S-key encryption lifecycle as blockers for durable journal; no plaintext localStorage shortcuts. EVM last.

## Cross-session linked guard update
- JS unresolvedByVault must be checked at plan start, submit start and immediately before network transmission across all sessions sharing exact root/network. Do not move the last guard earlier: async preflight can interleave. This protects only one JS realm, not reloads or devices.
- New create requests against an already saturated tail must fail rather than triggering a new-account quote, because the tail cannot accept a chain pointer. Node regression cases added but not executed; no CI checks visible. EVM remains last priority.

## Capability reuse directive — 2026-11-14
- Before writing a new subsystem, consult `docs/architecture/capability-reuse-audit.md`: encrypted pending queue, consent, manual sync/reconciliation, folder graph, file staging/chunking, Horizon historical readers and shared PWA/MAUI UI ALREADY exist for parts of HD/hybrid. Report **wiring gaps**, **policy disables** and **verification gaps** separately from absent implementation.
- `VaultStellarDraftClassification` reuses the existing record factory in BOTH HD and linked record planners. Preserve two independent account/signing strategies: never use HD-derived secrets, registry HMAC or unlocked HD device keys in imported S-key mode.
- Prefer extracting cross-strategy logic from existing Core rather than adding parallel file, queue, approval or recovery engines. Neither build nor test was run for this batch. EVM remains last.

## Shared approval policy refactor (2026-11-14)
- `VaultApprovalScopeGuard` is the shared account/network/age review check. Existing HD queue `VaultSyncApprovalGuard` continues to require its own payload digest and CAS checks, while S-key expansion retains exact quote comparison and separate reviewed signer transport. Do not rewrite those strategy-specific authorization checks as one generic submit function.
- Do not claim linked offline queue parity: independent S-key authenticated encryption/storage-key lifecycle and protocol adapter are not wired. Reuse existing encrypted queue and folder/file mechanics once isolated. Added regression source, not executed; EVM last.

## Consent-bound encrypted queue reuse (2026-11-14)
- `VaultScopedQueueReview` is the read-only shared queue+folder snapshot used by HD `VaultHybridSyncReviewService`; it delegates to existing encrypted queue/CAS, does not persist anything or publish. Recheck the active local vault epoch and exact network/consent after async reading.
- `VaultLinkedQueueInspection` is strictly read-only and `CanPublish=false`. Matching public account address does not authenticate queue format or authorize imported S-key signing. It is not a linked-sync UI workflow; do not silently reinterpret HD-format queued data as S-key drafts. Both hosts register it.
- Next use existing CAS, queue state policy, folder graph and file mechanisms, after strategy-tagged authenticated local credential/key lifecycle and a protocol-specific approval transport. No builds/tests for this source batch. EVM last.

## Shared queue strategy discriminator (2026-11-15)
- `VaultPendingMutation.OriginStrategyId` is optional trailing metadata. Null always means existing legacy HD; only `stellar-hd-v2` and `stellar-secret-key-chain` are recognized. Reject unknown IDs. Authenticated encrypted store CAS equality includes this value, and it cannot change during transitions.
- HD queue enqueue/fee review must not accept tagged linked intents; linked read-only inspection must never count legacy entries as S-key. Strategy metadata is **not signer ownership evidence**. No linked local persistence/sync is enabled without a separately authenticated encryption key, local-storage consent and historical operation verification.
- Keep reusing existing queue, folder graph and file infrastructure; regression source not executed. EVM is last priority.

## Queue-key lifecycle seam and linked-origin isolation (2026-11-15)
- Existing IndexedDB/MAUI encrypted queue stores now accept `IVaultQueueKeySession`; current `VaultLocalQueueKeySession` uses the existing local-vault unlock key, not imported S-key credentials. Do not register it as an S-key queue key source. The stores still enforce explicit device-storage consent and existing AES-GCM AAD/storage IDs.
- `VaultPendingMutationQueue.SameDurableRevision` must compare origin; linked-origin entries must not enter HD state transitions or ready review. Read-only mixed-queue inspection remains allowed. The S-key provider requires independently authorized/derived keys and distinct namespace before enabling writes.
- Source-only refactor, static regression cases not executed. Prefer adapting existing stores/CAS rather than another queue or file engine. EVM last.

## Single-snapshot encrypted queue review (2026-11-15)
- Reuse pure `VaultPendingMutationReviewPolicy.Build(all)` for HD queue readiness. `VaultScopedQueueReview` must read `ReviewAllAsync` once and construct readiness and folder graph from that same authenticated snapshot; do not reintroduce separate asynchronous reads in one review.
- Preserve HD-only readiness, linked-origin fail-closed, existing file policy and all consent/key-session requirements. Do not claim an S-key queue publisher exists yet. Source regression assertions were updated; tests not executed (no local checkout/dotnet).


## S-key linked expansion preflight and lock safety (2026-11-15)
- `VaultLinkedExpansionPreflightException` means Core did NOT invoke the linked ledger transport for that specific quote. It is safe to discard the unsubmitted review, but never infer that sibling or earlier transactions are confirmed/absent. After `ledger.SubmitApprovedAsync` any error is potentially uncertain; never retry blindly or classify that exception as preflight.
- Lock must invoke `VaultLinkedAccountExpansion.AbandonUnsubmittedAsync` even if UI has already lost the visible quote. This only invalidates Core reviews still pending before transmission; runtime uncertainty markers remain intact.
- Linked JS `inspectUncertain` checks the global same-runtime vault tombstone so parallel sessions see each other's unresolved writes. Tail UPDATE writes must preserve a spare entry for `sv:next:v1`; do not only reserve on CREATE.
- Source regression cases added; no Node execution, .NET build or live Testnet confirmation. Continue reusing existing Core queue/folder/file machinery rather than creating parallel engines; EVM last.


## Independent linked-key encrypted queue seam (2026-11-15)
- `VaultLinkedQueueKeySession` is separate from `VaultLocalQueueKeySession`/HD session and uses existing Argon2id wrapped random data-key lifecycle ONLY after connected S-key signer+network verification and explicit device-retention consent. Its credential namespace `secret-chain:queue-key:v1:` is distinct from HD, and wrapped records are isolated by exact account/network scope.
- `VaultLinkedQueueStorageScope` prefixes queue ciphertext namespace `secret-chain:queue:v1:`. `IndexedDbVaultLinkedQueueSnapshotStore` and `FileSystemVaultLinkedQueueSnapshotStore` reuse existing encrypted stores with this independent key and namespace (native has a distinct directory). Linked queue inspector NO LONGER reads HD `VaultScopedQueueReview`; it is read-only, rejects HD-origin ciphertext and always `CanPublish=false`.
- PWA/MAUI DI and lock/background/dispose paths must zero the S-key queue key. Never auto-enable S-key durable mutation writes, manual sync or encrypted local enrollment without explicit consent, accurate signer provenance and operation verification.
- Branch CI validation workflow added; recent builds/tests report failure but connector exposes no output. Do not assert a green build. Investigate CI before production. EVM last.

## Live S-key signer validation for encrypted local queue (2026-11-15)
- `VaultLinkedQueueKeySession.ExecuteAsync` verifies live JS signer/linked chain via `ledger.InspectAccountInfoAsync` before and after key callbacks; cached `IsConnected` cannot authorize local encrypted queue access after JS signer revocation. Network outage fails closed. No true offline S-key unlocking until separate authenticated local proof is developed.
- Branch and PWA release workflows run the S-key regression suite, but recent CI reports failure with no accessible logs via connector. Do not call the feature tested or production-ready. HD queue remains independent and no linked queue publisher is enabled.


## GitHub CI disabled; shared S-key device panel (2026-11-15)

- At explicit user request, **DO NOT recreate GitHub Actions CI workflows**: all four `.github/workflows/*.yml` files were removed from active feature branch and existing three from main. User wants to minimize GitHub workflow usage. Validation must be local/manual unless the user changes this requirement.
- `VaultLinkedLocalQueuePanel` (shared PWA/MAUI) grants explicit independent S-key consent, supports separate local passphrase create/unlock/lock, and only allows read-only encrypted queue inspection. No local linked publish/Sync stage exists. Disabling consent is not deleting data.
- `VaultLinkedLocalRetentionGate` is a separate required session consent barrier in S-key key/credential/queue read services; HD global device consent alone is insufficient.
- `IStellarSecretKeyLedger.ConnectionEpoch` changes on connect/disconnect; `VaultLinkedQueueKeySession` binds unlock to that exact epoch so the local key cannot remain authorized after reconnecting the same imported account. Static tests added, builds/test execution remain unverified.


## Linked S-key queued create pipeline — 2026-11-15
- `IVaultLinkedDraftStore` **reuses** the original encrypted PWA/native queue stores; add/delete/CAS only after S-key-specific consent and key/session/network origin checks. CAS may transition Pending→Submitting→Confirmed/Uncertain only without changing any immutable field or a confirmed 64-hex transaction hash. Do not route through the HD queue authorizer.
- `VaultLinkedDraftWorkspace` stages encrypted create records/folders with original Core folder parent dependency logic. `VaultLinkedDraftPublication` manually quotes, binds full queue revision+signer epoch, requires explicit approval, checks remote inventory, reserves via durable CAS BEFORE sending and fails closed on uncertainty. No automatic retry/rebroadcast. Connected record read-back is not independent historical operation proof.
- The shared `VaultLinkedLocalQueuePanel` now offers create/list/discard, parent folder selection and reviewed manual create publication on PWA and MAUI. Not yet true offline: key session and stage require connected live S-key ledger. Queued updates/deletes, file upload parity, queued capacity expansion and durable historical reconciliation still outstanding. Do not describe complete production sync.
- `tests/StellarVault.Core.CryptoTests/LinkedDraftSmoke.cs` exercises native encrypted queue and fake linked ledger, and Node static checks cover the integration, but **no .NET build or executable test run was possible here**. GitHub Actions must stay removed on main and feature branch; user explicitly prohibited CI due token usage. EVM remains last.


## Independent retention fix (2026-11-15)
- CRITICAL: `VaultLinkedLocalRetentionGate.DeviceRetention` is an independent `VaultDeviceRetentionGate`, and ONLY this gate may be passed to existing encrypted store implementations for S-key credentials/queue. Never inject the global HD `VaultDeviceRetentionGate` into linked queue services or toggle HD retention from `VaultLinkedLocalQueuePanel`. The linked consent gate grants/resets its own device policy.
- S-key local consent and HD local consent are separate even when an account G-address is the same. Preserve the existing strategy-specific encrypted credential/queue namespaces for backward compatibility.
- Smoke source and Node static assertions updated; not built/executed. No GitHub CI per user request. See `docs/ENGINEERING_HANDOFF.md`.


## Offline-only linked queue authorization and uncertain assessment (2026-11-15)

- Existing imported S-key local wrapped credentials may be unlocked offline with PUBLIC root G-address, selected network and separate local passphrase via `VaultLinkedQueueKeySession.UnlockOfflineAsync`, with explicit S-key-specific device retention consent. Offline queue key use MUST NOT contact Horizon, infer ownership from public root alone, access HD credentials, or authorize publication. First-time enrollment still requires the connected signer and chain.
- `CanPublish` requires a separate **live signer-backed queue unlock**, exact selected/connected network passphrase, active epoch and consent. Reconnecting a signer after an offline-only unlock does not promote that queue session. Core `VaultLinkedDraftPublication` must enforce `CanPublish` regardless of UI state. Switching network must lock/fail closed; `IsUnlocked` must not throw in UI for malformed custom network settings.
- `VaultLinkedDraftWorkspace` may stage/list/discard local record/folder creates offline, but remote parent IDs and record collisions remain unverified until online preflight. Shared PWA/MAUI panel must disable publish/remote recovery while offline and remain reachable from locked S-key entry screen.
- `VaultLinkedDraftRecoveryReview` is read-only advisory for durable Uncertain/Submitting queue entries: NO TryReplaceAsync, queue release, retry or confirmation based only on matching current-state fields or Horizon receipt. Exact historical operation and signer proof is still an unimplemented release blocker.
- LinkedDraftSmoke and Node source regression checks expanded; no .NET compile or integration test execution was possible. No GitHub Actions CI; user explicitly asked to remove it. EVM last.


## S-key draft editing uses shared CAS and cycle checks (2026-11-15)
- `VaultLinkedMutationTransitionPolicy` is the only linked store transition validator for both PWA IndexedDB and native filesystem. Pending create drafts may revise content + DependsOn atomically with same immutable mutation identity; only Pending→Submitting→Confirmed/Uncertain are supported publication transitions. Never allow Uncertain/Confirmed/Submitting→Pending or mutation ID/origin/account/network changes.
- `VaultLinkedDraftWorkspace.ReviseAsync` requires an exact current SHA-256 fingerprint of the opened plaintext-serialized payload, checks local pending parent ancestor cycles, and CAS-commits a revision. Shared `VaultLinkedLocalQueuePanel` handles explicit editing while keeping password fields masked and preserving unverified offline remote parent IDs. A draft edit never signs or broadcasts.
- Tests added but not executed; do NOT recreate GitHub Actions CI. Production blockers remain historical proof/reconciliation, queued updates/deletes and file chunks, staged capacity expansion, device Testnet tests and security audit.


## Manual-only linked queue ordering (2026-11-15)

- Use pure `VaultLinkedManualSyncPlan.Build` for read-only next-ready draft ordering from a SINGLE authenticated encrypted queue snapshot. Do not generate a second queue or retry engine. Feed existing `VaultQueuedCreatePolicy.Problems` blockers, disable suggestions on ANY Uncertain/Submitting/Conflict, and require `VaultLinkedQueueKeySession.CanPublish`.
- The UI `Review next ready draft` only calls the original S-key manual review/quote. Always require separate explicit user fee approval for each on-chain transaction; no auto-batch broadcast, background sync or retry.
- Local fake-ledger smoke and Node source assertions were added but not executed. No GitHub Actions per user request; production release blockers remain exact historical proof/recovery, queued update/delete, files, capacity expansion and complete .NET/MAUI/Testnet validation.


## Journaled linked account capacity for manual encrypted drafts (2026-11-15)

- Manual S-key queue capacity expansion MUST go through `VaultLinkedExpansionJournal`, reusing `VaultLinkedAccountExpansion` for signer/fee authorization and existing encrypted queue stores for atomic journal transitions. `ExtendLinkedChain` is Linked-only; HD enqueue must explicitly reject it.
- Expansion journal ID MUST deterministically bind authenticated root+network+current tail so concurrent browser tabs cannot Add two different intents for the SAME account tail. Reserve Pending→Submitting via existing CAS BEFORE network submit; leave Uncertain/Submitting on unknown outcome, never retry automatically. Pending intent after a pre-reservation crash also blocks all linked queue publishing. Only still-Pending (never reserved) intents may be explicitly CAS-deleted; never clear Submitting/Uncertain/Confirmed without independent operation-level proof.
- The shared PWA/MAUI panel shows a separate fee-reviewed account-expansion step, then requires a separate record fee review. `VaultLinkedManualSyncPlan` and `VaultLinkedDraftPublication` block ordinary creates while any expansion intent remains unconfirmed. `VaultLinkedDraftRecoveryReview` is advisory only.
- This only journals expansion via the encrypted queue UI. The older direct workspace expansion may still lack restart-durable unknown submission state. Manual Node/.NET/device validation outstanding; no GitHub Actions per user request. Do not claim production ready.


## Canonical authenticated vault route / shared workspace (2026-11-15)

- **Current product decision:** PWA has a single authenticated workspace route, `/vault`, for both first-class account strategies. `/secret-vault` is now a redirect/deep-link alias, NOT a separately mounted workspace. Empty S-key first-run still visits the common `/initialize` and then `/vault`. Historic references above to a distinct PWA S-key route are superseded.
- `VaultWorkspaceModeResolver` in Core selects `Locked`, `MnemonicHd`, `StellarSecretKey`, or `ConflictingSessions` exclusively from authenticated host session states; it rejects simultaneous active HD and linked S-key sessions and does not infer strategy from record contents, local consent, address shapes, or UI routes. `Index.razor`, `Initialize.razor`, and `Vault.razor` enforce this. HD local-only sessions must still open `/vault` when Stellar is offline.
- PWA `Vault.razor` hosts `VaultLinkedWorkspaceShell` when the imported S-key signer is active, preserving the modern app rail, mobile tabs, folder tree, list/grid selection, inspector, settings and help. `VaultSecretKeyWorkspace` is embedded with a selected view; its independent S-key signer, account-chain review, encrypted local queue, and history checkpoint remain completely separate from the HD services. Its local queue component stays mounted when switching PWA tabs, to preserve pending in-memory approval state. The nonembedded MAUI component continues to show its own queue without being hidden by the PWA view selector.
- S-key tree file parts/branches are hidden from ordinary explorer; only authenticated file roots are shown. Generic text reveal/edit/delete of immutable file trees remains blocked; verified download stays in linked Sync. The S-key offline-only local encrypted draft unlock remains available from a disclosure on locked `/vault`, with separate passphrase/consent and no Stellar writes.
- `tests/shared-vault-workspace.test.cjs` and `VaultWorkspaceModeSmoke` pin route and security isolation. New code was edited through GitHub; exact HEAD still requires local `scripts/verify-release.ps1` compilation, browser rendering, PWA refresh and MAUI device regression. This refactor unifies navigation and explorer presentation but **does not yet merge all strategy-specific editor and Sync form internals** into a single cross-host component; do not claim total UI-component consolidation.


## Shared UI completed at component boundaries (2026-11-15, supersedes older separate workspace statements)

- Product invariant: **one canonical `/vault`, one responsive application shell, one collection of UI components, independent authenticated HD and imported S-key logic**. No UI should derive or guess account mode from records; `VaultWorkspaceModeResolver` is the explicit authenticated-session guard.
- Both PWA strategies render under the SAME `src/StellarVault.Pwa/Pages/Vault.razor` sidebar, screen header, mobile tabs and toolbar. Former `VaultLinkedWorkspaceShell.razor` was deleted; `/secret-vault` is only a redirect. S-key uses an embedded strategy-bound content controller, not another app shell.
- Actual shared Razor components: `src/StellarVault.UI/Components/VaultExplorerTree.razor` (hierarchical folder tree), `VaultExplorerItemRow.razor` (public encrypted file/record row and action menu), `VaultRecordFields.razor` (HD/S-key published AND encrypted local draft editors), and `VaultRecordInspector.razor` (record information, secret reveal/copy, safe file actions and tabs). These are used from BOTH strategies on PWA AND the native MAUI host. Shared `VaultPublishedFileClassification` hides internal hierarchical nodes, and prevents editing/deleting file roots via generic record handlers.
- **Strategy-specific behavior lives behind callbacks and services**: HD registry, mnemonic account derivation and journal; imported S-key independently linked account chain, signer/review/expansion journal and isolated locally encrypted queue. Never rewrite them into a generic signer or mix HD/S-key device retention and credentials.
- `VaultLinkedLocalQueuePanel` has stable mounted `View="drafts"` vs `View="sync"` surfaces; its default `"all"` remains for explicit offline queue recovery and nonembedded use. Switching PWA/MAUI tabs must preserve pending quote, unsigned authorization, and uncertain submitted history. The MAUI linked component stays mounted via stable native content key, not re-instantiated on every tab.
- HD file trees and S-key file trees are presented as **one file root**. Plaintext file is recovered only through the original mode's verified reader; no generic text reveal/edit/delete is allowed on an encrypted manifest or child. Whole-tree delete remains explicitly unsupported.
- Legacy HD and S-key UI markup can remain for strategy-specific transaction approval/capacity review, but new shared record/folder/browser visuals must go in the shared UI components instead of duplicating markup. Native and PWA user flows should remain aligned.
- Connector edits have NOT compiled this exact HEAD. Run `powershell -File scripts/verify-release.ps1` locally, hard-refresh PWA, exercise both credential modes and independent S-key offline encrypted draft recovery, test mobile navigation while a quote is pending, and confirm no HD access in S-key mode before considering production sign-off.
