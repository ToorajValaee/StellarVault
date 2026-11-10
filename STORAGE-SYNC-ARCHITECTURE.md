# StellarVault storage and sync contract (new architecture)

## Choice shown before vault access

**On-chain only (default):** mnemonic-derived session in memory, no application-owned persistent mnemonic, local vault data, offline drafts, or credential envelope. Record/file/folder writes require online Stellar and explicit transaction approval. No device passphrase or device-unlock enrollment. Browser caching of application code is independent of user data; browser/network caches cannot be claimed to contain no metadata without verification.

**Encrypted on-device vault (opt-in):** user explicitly authorizes persisted encrypted mnemonic/key material and encrypted records/files/folders. Require a separate strong encryption passphrase or an appropriately reviewed OS-backed credential policy. The user must be able to inspect, lock, revoke and erase device data. Offline writes are **pending local drafts**, never represented as on-chain commits.

## Sync safety invariants

1. An explicit user action queues and synchronizes offline records/files/folders; no silent automatic paid transaction.
2. Every draft carries a stable idempotency identifier and account/network scope; prevent cross-network replay.
3. An attempted ledger submission is **uncertain** until verified by transaction hash/account sequence and remote state. Do not resubmit automatically on timeout.
4. Sync preserves dependency order: folders, records, chunk manifests, chunks, pointer updates; no partial file may appear as complete.
5. Conflicts require surfaced version/ETag or hash comparison and deliberate resolution; never blindly overwrite remote ciphertext.
6. Persist sync queue metadata encrypted and atomically along with drafts; distinguish queued, preparing, submitted, confirmed, failed and uncertain.
7. Sync previews fees, reserves and number of transactions before any approval; allow cancellation before submission.
8. Explicitly disclose that unsynchronized browser/device data can be destroyed by storage clearing, app removal, quota eviction or device failure.
9. The default on-chain-only mode must not instantiate or write persistent vault credentials as a side effect of login.
10. A device wipe must remove the encrypted vault and queue; deletion of local data never means deletion from Stellar.

## Cross-platform

Keep the consent gate, encrypted draft model, sync state machine and conflict semantics in StellarVault.Core. Adapt durable encrypted storage per host (IndexedDB PWA, platform secure storage + private database on native). Provide identical source-of-truth and conflict semantics. Backing up an encrypted seed to local storage is an explicit custody decision, not implicit consent.

## Delivery status

The consent gate has been introduced in core. **Neither mode is yet completely enforced by the current PWA**, whose access workflow still unconditionally uses local passphrase credentials. Do not release either selectable mode until access, all storage services, sync and deletion follow these invariants.

## Architecture status — 2026-11-03

Manual Sync coordination now lives in Core and is shared by both hosts. The PWA continues using its encrypted IndexedDB mutation store. MAUI registers a new native encrypted filesystem mutation store using the same account/network/id authenticated encryption contract, plus the shared queue and deny-by-default verifier.

This is platform infrastructure, not a completed hybrid experience. The shared offline-create/edit/review/publish workflow is not yet integrated across both UIs, native local-vault account authorization remains gated, and file/folder synchronization plus conflict handling still need completion. Direct on-chain operations are preserved as the established user flow.

## Shared offline record/folder slice (2026-11-03)

The shared Core workspace now supports local encrypted pending record/folder creation, revision, listing and removal. A shared Razor editor is integrated into PWA and MAUI. Pending child folders/records record local parent mutation dependencies; publishing remains blocked until the parent is confirmed.

On native, device-only encrypted storage can be explicitly opted into and unlocked with a separate local vault passphrase. This uses the same **local-only account namespace** authorizer as PWA; it is not cryptographic Stellar ownership verification and does not authorize transactions. Existing on-chain operations are preserved. The hybrid record/folder milestone is **not done** until reviewed queue publication, confirmation, recovery and native authorization are validated.

## Shared manual hybrid Sync — 2026-11-03

The reusable hybrid Sync UI now has an explicit review/approval/submit action for supported queued creates. Core checks the actual connected HD signing root against the local queue account, the selected network, unlocked session epoch, current remote inventory and fee bounds before any durable reservation; after reservation every attempted submission is quarantined Uncertain pending independent verification. Mainnet requires a second confirmation. PWA and MAUI mount the same component, preserving direct on-chain workflows.

The milestone is **not complete** until independent ledger confirmation enables safe dependency advancement, unsupported mutation classes are handled, and cross-platform release testing is performed. Never retry an Uncertain mutation automatically.

## Verified hybrid queue confirmation — 2026-11-03

Both hosts now compile the same historical Horizon evidence adapter for the Core evidence verifier. For an already recorded transaction hash, the adapter binds the unlocked local vault and actual connected HD root, resolves the child index from the connected chain, verifies historical manifest, registry, chunk ciphertext and decrypted draft, and gives Core record-to-transaction evidence. Only after successful verification and durable queue CAS does a mutation become Confirmed. The shared UI offers manual recorded-hash verification; unknown-hash entries remain blocked. Confirmed parent folders unblock dependent local children for separate approval. This is pending build, Testnet and security validation, not a production release claim.

## Hybrid remote modifications, 2026-11-03

A shared Core service and Razor component now stage offline encrypted updates/deletes of published records/folders across PWA and MAUI. Proposed mutations pin the full remote record fingerprint and expose read-only divergence checks before future Sync. The user can revise an offline update or discard an unsubmitted change without network access. Actual update/delete hybrid publication and historical confirmation are still disabled until signed version preflight, durable per-entity uniqueness and independent receipt linkage are implemented. Direct on-chain operations remain unchanged.

## Testnet-only queued update/delete Sync — 2026-11-03

Shared Core fee review and manual approval now support version-pinned remote update/delete mutations. Original record fingerprint, fresh remote inventory, parent constraints and cost limits are rechecked before durable reservation; a single attempted writer call is quarantined `Uncertain` pending a future transaction-specific proof. Because the underlying Stellar writer does not support atomic on-chain version preconditions, Public Mainnet and custom-network queued update/delete submission remain disabled in Core and UI. Existing direct on-chain CRUD remains unchanged. Creation fee-estimation routing is corrected to use create rather than update estimates. PWA/MAUI compile and Testnet validation remain outstanding.

## Update proof, primary receipt identity (2026-11-03)

The hybrid queue now records the manifest-commit hash for updates and manifest-delete hash for deletions, instead of later cleanup receipts. Legacy direct on-chain return hashes remain backward compatible. The shared Core evidence verifier and PWA/MAUI Horizon history adapter now permit recorded-hash UPDATE confirmation on Testnet after full decrypted historical record-to-transaction proof. This does not enforce predecessor-version atomicity. DELETE remains Uncertain because tombstone historical proof is absent; queued Mainnet/custom remote UPDATE/DELETE publishing stays blocked.

Recorded-hash child resolution now also requires seed-derived `inspectHdLineage` verification in the connected HD session, rather than trusting a matching read snapshot alone. No deletion queue confirmation is enabled.

## Staged deletion original and all-copy matching (2026-11-03)

New hybrid deletions persist an encrypted `VaultDeletionIntent` containing the full original approved record and seed-authenticated HD manifest-owner child accounts. Core preflight and manual approval revalidate the original SHA-256 record baseline and current owner inventory; recovery compares the tombstone transaction's exact source set with staged owners and reports whether coverage matched. PWA and MAUI reuse the same Core interface and ledger adapter. This is preflight and diagnostic evidence, NOT an atomic on-chain version check or complete historical predecessor proof. Delete confirmation remains disabled, and queued Mainnet/custom remote mutations stay gated.

## Historical deletion predecessor coverage (2026-11-03)

The shared PWA/MAUI deletion investigation can now resolve each tombstone source's nearest preceding historical manifest write and verify its encrypted chunks, registry and decrypted plaintext against the originally staged deletion record. A separate `HistoricalPredecessorMatched` finding is reported alongside source-set coverage and current absence. It remains diagnostic only, not queue confirmation: no atomic remote-version condition exists and concurrent edits/copies may still race. Core remains fail-closed, Mainnet remote writes remain gated.

## Guarded optimistic ledger transport — 2026-11-03

Both hosts now register the same `IVaultGuardedLedgerMutations` interface implemented by their connected HD-v2 adapter. The Core queued remote UPDATE/DELETE coordinator passes the immutable pinned baseline (and deletion manifest owner inventory) to the writer. The writer itself re-reads the decrypted remote record and rechecks ownership before writing; it fails closed if the expected baseline or deletion-copy set drifted. This minimizes, but cannot eliminate, cross-device race windows. These are **not atomic Stellar record preconditions**, and Mainnet/custom queued remote writes remain blocked pending a transactional coordination protocol.

## Conflict-safe record copy and deletion quote support (2026-11-03)

Hybrid deletion fee review now correctly unwraps `VaultDeletionIntent.Original` before estimating a version-pinned DeleteRecord/DeleteFolder; the strict queue preflight still validates original fingerprint and manifest owners. Public/custom queued update/delete quotes are blocked in the shared Core review layer. Pending offline record updates can be saved as a new encrypted CREATE draft with a fresh ID using the shared Core and UI on PWA/MAUI; this preserves proposed data without overwriting any published record or automatically discarding the source. It is an append-only recovery option, not atomic multi-device conflict resolution.

## Offline folder move/reparent, 2026-11-03

Local encrypted record and folder drafts can now move among root, pending local folders and already-confirmed parent folders. Shared Core changes payload and queue dependency edge together through one durable per-mutation CAS and rejects stale revisions or dependency cycles. Shared UI in PWA/MAUI offers child creation, folder selection, root move and visible parent IDs. Pending-parent children remain blocked from Stellar publication until confirmed. On-chain atomic multi-device coordination remains an independent release blocker.

## Offline editor revision isolation (2026-11-03)

Create and remote-update editors now carry their *opened payload SHA-256*, distinct from pinned remote-version fingerprints. Shared Core queue methods refuse stale local saves before durable revision CAS, including folder-dependency changes. Both PWA/MAUI shared editors clear cached local content on vault lock/account switch and reload only for the matching session epoch. This protects against older tabs overwriting newer device-local edits, but does not solve cross-device on-chain concurrency.

## Folder dependency consistency, 2026-11-03

Shared Core now checks that every pending create's serialized parent FolderId matches its durable queue dependency. Missing, stale, ambiguous, failed, non-folder and cyclic local parents cannot appear ready. The shared Sync UI explains blocked reasons and the required manual correction; pending-parent children still wait for independent parent confirmation. No automatic reparenting or transaction retry, and no change to direct on-chain writes.

## Guided nested-folder Sync (2026-11-03)

The shared Core read-only `VaultHybridFolderSyncPlan` turns encrypted create-folder/create-record queue entries into a manual parent-first verification/publishing sequence, with explicit waiting/ready/repair/unresolved statuses. The shared Sync UI renders each step across PWA and MAUI, offers optional fresh cost review for ready entries, reloads after a submission attempt or historical confirmation, and invalidates cached approvals on vault session changes. No automatic network writes, retries or child approvals; current ledger inventory and explicit per-mutation approval remain required.

## Interrupted submission quarantine and global Sync gate (2026-11-03)

The shared Sync UI now offers explicit no-retry quarantine of a durable Submitting mutation. Core verifies its exact queue revision and CAS-updates only to Uncertain; account/network/session checks and immediate workflow reload prevent stale screen actions. The guided folder/child Sync plan no longer marks children ready while any unresolved submission or conflict globally prevents new fee approvals. Neither action changes the direct on-chain flow or permits automatic broadcast/replay.

## Missing-hash history investigation — 2026-11-04

Added a read-only Core+PWA/MAUI investigation for unresolved queued record/folder CREATE and Testnet UPDATE lacking recorded transaction hashes. Searches a bounded recent root-funded transaction window for candidate manifest writes, authenticates each through HD lineage and historical registry/chunks/plaintext, and shows potential hashes without modifying the encrypted queue or asserting attribution. Zero matches is never proof of transaction failure, and no candidate authorizes retry/confirmation. Hashless DELETE remains excluded. Stronger pre-broadcast receipt identity and full Testnet validation remain release blockers.

## Hybrid CREATE signed-manifest checkpoint (2026-11-04)

The queued CREATE writer now checkpoints the fully signed final-manifest transaction hash in the encrypted durable queue's Submitting entry **before** sending that transaction to Horizon. JS awaits a single-use .NET callback and aborts final-manifest broadcast on a persistence error; coordinator continues from the updated exact CAS revision and preserves the hash if Horizon response is lost. Direct on-chain APIs remain unchanged and historical confirmation remains mandatory. Hashless failures before the final manifest is prepared are still possible, and remote UPDATE/DELETE need separate prepared-hash and atomic version work.

## Queued Testnet UPDATE signed hash (2026-11-04)

Hybrid queued updates now checkpoint the signed primary `svh:<id>` commit transaction hash in durable `Submitting` before broadcasting that exact transaction. Direct on-chain updates remain on their previous API. The checkpoint is preserved across unknown receipts and later cleanup writes; independent historical confirmation remains mandatory. Multi-owner deletion still requires its own protocol, and optimistic version checks remain non-atomic across Stellar transactions.

## 2026-11-04: queued deletion signed transaction journal

Hybrid Testnet deletion now checkpoints each HD manifest-owning child account and the hash of its signed tombstone transaction in encrypted queue metadata before each broadcast. Both storage hosts include this journal in durable revision CAS and the shared UI exposes it. A single legacy hash is intentionally not used to represent a multi-owner delete. Signed hashes are not receipts; partial deletion and concurrency races remain possible. No automatic retries or confirmations are enabled.

## Multi-owner deletion journal evidence — 2026-11-04

The signed deletion journal now has a read-only per-owner historical verifier in shared Core, using the existing strict HD-authenticated tombstone/predecessor verifier for each recorded source/hash. Shared PWA/MAUI Sync shows verified, unresolved and uncheckpointed owners, with incomplete historical coverage clearly identified. Even when all historical tombstones match, it does not mark a queued deletion Confirmed or allow any retry: multi-transaction deletion remains non-atomic and cross-device writes can race.

## Deletion historical evidence vs current inventory (2026-11-04)

Strict tombstone history now also binds the successful transaction receipt's root source account. Multi-owner historical verification is separate from a single final time-of-read current ledger/manifest-owner snapshot. The report distinguishes unknown current inventory and unexpected HD owners created after the staged deletion intent; neither historical coverage nor present absence authorizes queue confirmation, replay or Mainnet queued deletion.

## Last-chance manifest byte guards (2026-11-07)

Queued Testnet update/delete now supplies exact expected HD-v2 manifest ManageData bytes to the signer. Before signing the final update manifest, the JS adapter checks every old manifest owner against Horizon and rejects changes; a new destination must still be empty. Before each deletion tombstone, it checks that owner's expected manifest value. No direct CRUD behavior is changed and no retries are added. The guards narrow but cannot eliminate cross-device races: ledger-level atomic conditions and Testnet validation remain outstanding.

## Root sequence ordering fence — 2026-11-07

The HD-v2 sponsored writer now reads the root account and captures its transaction sequence before reading target/guard child accounts. As all standard StellarVault HD-v2 writes are root-sourced transactions, this serializes compliant devices using the same root: a concurrent accepted transaction after the root read invalidates the old sequence, while a prior transaction should be visible in later child reads. This is not universal CAS: direct child-sourced or noncompliant writes can bypass it. No automatic retries; uncertain outcomes remain quarantined.

## Hybrid file staging parity — 2026-11-07

PWA and MAUI now expose the shared hybrid file staging UI and use the same Core `VaultHybridFileWorkspace`. It stores a bounded 1 MiB file, metadata, SHA-256 fingerprint, optional parent-folder dependency and collision-checked stable part/manifest IDs inside one encrypted `UploadFile` queue entry. The PWA uses a browser picker adapter; MAUI uses its native file picker. The staged immutable SVFILE2 plan can be reconstructed after restart and is not sent to Stellar during staging.

This closes the offline staging gap only. Per-part approval/checkpointing, manifest-last publication in the hybrid coordinator, interrupted upload reconciliation, complete-file historical verification and deletion are still missing. The legacy upload coordinator remains separate. No builds or live validation were run.

## Hybrid file staging memory hardening (2026-11-07)

The shared file workspace now allocates stable part IDs from file length without constructing all chunk strings during staging. It clears the temporary plaintext copy and serialized queue payload after the encrypted host store accepts the mutation, returns a metadata-only summary to UI, and reconstructs chunk drafts only when explicitly requested for a future publisher. File listing validates the byte fingerprint and part-count/manifest identity without materializing every chunk.

## Multipart file journal foundations (2026-11-10)

Encrypted hybrid UploadFile queue entries now support an ordered SignedFileEntries journal with exact per-part Stellar transaction hashes, verified original stable part/manifest IDs, and atomic host CAS against stale tabs. The shared file UI shows prepared identities and the next ID, warns against replay, and offers a read-only first-part cost/remote-collision/parent preflight. No fee approval, signing or broadcasting is enabled for hybrid files until per-part recovery and independent historical verification are implemented.


## Read-only hybrid multipart ledger observation — 2026-11-10

Core VaultHybridFileWorkspace.InspectLedgerProgressAsync reconstructs the immutable encrypted UploadFile plan and compares the **current** connected-root ledger inventory against every expected record payload in order. It rejects duplicate record IDs, mismatching staged content, a visible later stage without an earlier stage, corrupt signed-entry order, changed local queue state and account/session changes. The shared PWA/MAUI file draft editor exposes this inspection and explicitly distinguishes currently matching records from durable signed checkpoints. No queue transitions, signing or broadcasts occur.

This is **not historical receipt verification**: a matching current record does not establish that its corresponding signed hash succeeded or that the local queue produced it; absent records do not establish a failed broadcast. Never resume, mark Confirmed, or retry using these observations. Next: independently authenticate each signed transaction/record link, then implement per-part approval, pre-broadcast checkpointing and manifest-last publishing with a conservative restart policy. No builds, native/browser tests or live Testnet runs were executed for this change.
