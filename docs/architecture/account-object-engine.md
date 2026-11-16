# Chain-neutral account-backed object storage engine

## Ownership and naming

**The engine is not StellarVault and is not named after Stellar.** Its public API namespace starts as `VaultEngine.Core` while the existing `StellarVault.Core` project provides a compatible transition location. Future assembly/repository extraction must preserve compatibility or provide explicit adapters; do not rename wire formats or existing namespaces blindly.

- **Engine:** chain-neutral typed object descriptors, streaming, content authentication, abstract capacity policies, revision checks, cost quotes, mutation intents and lifecycle coordination.
- **Providers:** `stellar`, `evm`, and future blockchain/storage backends. Providers own their unique account storage, signer rules, consensus proof, fees, reserves, gas, receipts and storage pricing.
- **Applications:** StellarVault is the first consumer; an eventual EVM vault, SDK, CLI and other applications use the same engine.
- **Hosting:** SaaS REST API is a separately deployed adapter on top of that same engine. API auth/RBAC, tenants, quotas, billing and deployments are outside the engine. The engine must remain usable offline/in-process without an HTTP server.

## Application-facing contract

`VaultEngine.Core.IAccountObjectStore` defines `EstimatePutAsync`, `PutAsync`, `GetAsync`, `OpenReadAsync` and `DeleteAsync`. Operations carry explicit `ObjectStoreScope(ProviderId, NetworkId, StoreId, AccountStrategyId)`; data objects use `ObjectDescriptor(ObjectId, TypeId, Name, ContentType, ParentId)`. Capacity policy is independent of wallet/account identity:

- `SingleAccount`: never extend an account chain or deploy a contract implicitly. Insufficient capacity is an explicit result.
- `ExpandWithExplicitApproval`: a provider may prepare capacity-expansion intent, but may not submit or consume funds until separately authorized and verified.

`PutAsync` and `DeleteAsync` **prepare intentions**, not automatically confirmed network writes. A returned `AwaitingApproval`, `CapacityRequired`, or `SubmittedUncertain` state must never be surfaced as a successful persisted object.

The `AccountObjectStoreRouter` provides a strict, no-fallback provider selection contract. It never guesses a network, derives a key or selects an account strategy. Provider adapters implement `IAccountObjectStoreProvider`.

## Existing implementation to reuse

StellarVault already has working protocol pieces under its existing namespaces: `VaultRecordTypeFactory`, `VaultStreamingFileTree`, `VaultAccountStrategyFactory`, `VaultLedgerRecordWorkspace`, `VaultLinkedRecordWritePlanner`, on-ledger history verification, encrypted offline journals, record reconciliation and explicit fee reviews. **Do not duplicate them.**

The first **Stellar provider adapter** (`StellarAccountObjectStoreProvider`) now calls the existing authenticated HD/linked ledger readers and encrypted local draft queue create operations for credentials and folders (ordinary UTF-8 payloads). Both hosts register it behind the neutral `IAccountObjectStore` router. `PutAsync` with `ObjectId = "new"` returns the newly allocated `ObjectId` and a `Prepared` intent; it neither publishes nor quotes a network transaction. The fee and capacity-expansion fields are nullable precisely to distinguish unquoted and unassessed work from a free operation or sufficient capacity.

**Fail-closed milestones:** single-account capacity enforcement for *published writes*, revision-pinned writes and deletes, file streaming, linked S-key fee quotes and historical proof binding are not yet integrated with the neutral adapter. For new ordinary HD records, `EstimatePutAsync` now calls the existing `IStellarLedgerRecords.EstimateAsync` and returns advisory XLM network fee, peak reserve, and `CreatesChildNode` as the capacity-expansion indicator. This estimate never commits funds, signs, publishes or guarantees that capacity will remain available; a fresh reviewed binding quote is still required. A `SingleAccount` request can **inspect** whether more capacity would be needed, but a `PutAsync` for that mode remains disabled until the publish path guarantees it will not extend a chain. Until then the adapter throws `NotSupportedException` for those requests; it must not replace the existing working vault UI paths. Only `ExpandWithExplicitApproval` is accepted for staging today, and no actual expansion is performed during staging. `ObjectId = "new"` is required for creates because the existing encrypted queue generates the actual 16-hex ID. The requested public `NetworkId` is `testnet`, `public` or the configured custom network, while the internal account-specific credential namespace is checked separately. The current on-ledger ordinary-record layout lacks a preserved logical type/MIME discriminator, so the new adapter intentionally limits writes to `credential` and `folder` instead of silently losing custom/text types. `GetAsync` exposes a `snapshot-sha256:` fingerprint, not an authoritative revision token for conditional writes. The next milestone is to reuse the existing verified streaming-file, fee/review and remote-mutation services through this interface, then migrate the UI without changing its behavior. Do not map EVM contract semantics onto Stellar account-data operations. The EVM provider comes later and may choose different native storage implementation, proof and gas rules without altering the object-store API.

## Migration and honesty boundary

The initial chain-neutral interfaces and router are implemented, and the first narrow Stellar provider is now registered in both hosts. The PWA/Android UI has **not yet migrated** to the neutral interface, and the provider's unsupported features remain explicitly blocked. No SaaS endpoints or EVM backend are deployed. Preserve current UI and protocol compatibility during the phased migration.

The API must never take mnemonic/secret-key bytes by default. Browser/native callers keep local encryption and signing. A hosted adapter receives only explicitly authorized encrypted payloads and signed intents. Data confidentiality, availability, durable operation journals, cost commitments, tenant separation and error recovery require independent verification before launch.

Names such as `StellarVault` remain appropriate for the **application and existing compatibility code**, but new chain-neutral interfaces should be under `VaultEngine.Core` or an equivalently neutral engine namespace.


## Neutral file-write staging

`IAccountObjectStore.PutAsync` now accepts `ObjectDescriptor(TypeId = "file", ObjectId = "new", Name = fileName, ContentType = explicit MIME type)` for either Stellar HD or independent linked S-key accounts, with `ObjectCapacityPolicy.ExpandWithExplicitApproval`. The provider delegates **directly** to the corresponding existing `StageTreeFileAsync` implementation, retaining the SVTREE3 encrypted-node queue, bounded streaming, root-last manifest, interrupted-staging cleanup and later manual fee/transaction approval. It returns `Prepared`, `ObjectId = RootId`, and a synthetic `OperationId = "staged-tree:" + RootId` locator (not an on-chain receipt or per-node durable queue mutation ID). `EstimatedStoredBytes` is the plaintext file length; network fee, required reserve and expansion requirement remain **unknown** until the later reviewed publication plan.

Do not treat this as published file storage: files still require explicit per-node synchronization and historical proof. `GetAsync` / `OpenReadAsync` now delegate to the verified tree reader within an 8 MiB private temporary buffer. Legacy file formats and unbounded verification destinations are still unsupported by the neutral adapter. Text/plain file uploads are rejected until type discriminators preserve file semantics. Real device streaming, interruption rollback, capacity and quote verification are still mandatory.

## Verified file reads through the neutral engine

`GetAsync` and `OpenReadAsync` now recognize published SVTREE3 root records for both Stellar HD and linked Secret Key scopes. They reuse `VaultLinkedTreeFilePolicy.ReadAsync` and the existing `VaultStreamingFileTree.ReadVerifiedAsync` checksum/manifest traversal against a **fresh verified ledger snapshot**. The entire file must be reconstructed and its committed SHA-256 checked before the engine exposes metadata or a plaintext stream. The engine checks the root/network/ledger session again after verification and fails closed on duplicate IDs, missing nodes, unexpected protocol versions or tampering.

Because the current chain-neutral `OpenReadAsync` returns a `Stream` without accepting a destination, the adapter uses a private **8 MiB maximum** verification buffer. It zeros its backing allocation when disposed and also on verification errors. Content beyond that threshold is rejected with `NotSupportedException`; for large files the application must continue to use the existing private-destination streaming download workflow until an engine-level verified output-sink contract is implemented. Do not buffer unbounded files or expose partially authenticated bytes to API consumers.

Legacy single/multipart file formats are not yet wired to the neutral file reader and continue using the original strategy-specific adapters. The published-root locator remains a native Stellar record ID, not a standalone file blob. Testing on real HD/linked accounts and supported platforms is still required.

## Local authorization isolation

The linked encrypted queue has its own unlock epoch independent of the ledger connection epoch. The common object provider checks that epoch before and after staging a record, and the underlying `VaultLinkedDraftWorkspace` now includes it in its scope checkpoint for *all* awaited operations. Locking and re-unlocking the same S-key root offline cannot resume a previously authorized in-flight draft operation under the replacement local key session.


## Large verified streaming exports

The neutral router now implements the **optional** `IVerifiedObjectExport` capability; providers opt in with `IVerifiedObjectExportProvider`. The caller supplies `IVerifiedObjectDestination`, whose `OpenTemporaryAsync` must return a **private, unobservable** writable staging stream, `CommitAsync` must publish it atomically or fail, and `AbortAsync` must destroy any partial plaintext.

`StellarAccountObjectStoreProvider.ExportVerifiedAsync` supports published SVTREE3 file roots in both HD and independent Secret Key modes. It uses **the same** `VaultLinkedTreeFilePolicy.ReadAsync` authenticated tree reader against one fresh ledger snapshot. After writing and flushing to the destination, the provider closes the temporary stream, revalidates account/network/signer scope and cancellation, and only then commits the destination. Verification, read, flush or commit errors trigger `AbortAsync` without claiming success. A consumer must implement the destination's nonvisibility/atomic-commit and cleanup guarantees; accepting a public download response stream as a temporary destination violates this contract.

The older `OpenReadAsync` remains a convenience method with an 8 MiB private-memory cap. `ExportVerifiedAsync` does not have that cap: it uses bounded node buffers and a destination-supplied temporary stream; disk use depends on the destination. This does not lift storage/transaction costs or protocol limitations and is not support for legacy file formats.

Neither API bypasses the existing reviewed publication or historical proof pipeline. The current tests cover successful authenticated export, tampered-child abort, and unsupported-provider rejection. Real PWA/native destination implementations, cancellation/permissions and exact-head builds remain separate release gates.


## Atomic private filesystem destination

`VaultEngine.Core.AtomicFileObjectDestination` is a concrete `IVerifiedObjectDestination` for native applications and trusted hosted-server filesystem storage. It requires a fully qualified output path in an existing trusted private directory. `OpenTemporaryAsync` creates an exclusive unpredictable `.vaultengine-*.partial` file in the same directory; the public final path is absent during verification. Once the provider has verified all SVTREE3 node commitments and closed the temporary writer, `CommitAsync` moves the staging file to the final name with `overwrite: false`, rejecting both pre-existing targets and concurrent final-path creation. `AbortAsync` and asynchronous disposal remove unfinished staging files.

This is **not** a generic browser download implementation and must not be pointed at a web/static serving directory, a shared untrusted directory, or a location with unreviewed filesystem/symlink permissions. Rename is atomic only for suitable local filesystems, not a guarantee of crash-durable writes or secure SSD erasure. Enforce destination-directory privacy and user consent outside Core. There is no automatic host-level destination registration; a native/hosted caller must supply a trusted, authorized target path explicitly.

The Core smoke harness now exercises temporary invisibility, commit, abort, disposal, existing-target rejection, and concurrent-target creation. The tests are committed but require an actual .NET execution run.

## Neutral publication review (no second publisher)

The engine now exposes the **optional** `IObjectMutationReview` contract through `AccountObjectStoreRouter` when a provider implements `IObjectMutationReviewProvider`:

- `ReviewAsync(scope, operationId)` obtains a fresh fee/reserve preview for a **previously staged** mutation.
- `CancelReviewAsync(scope)` cancels a live linked-S-key quote. HD reviews are read-only and require no quote cancellation.
- The resulting `ObjectMutationReview` contains an operation ID, object ID, `AwaitingApproval` state, native-asset quote and an opaque review reference. **It is not a signing authorization or transaction receipt.**

The Stellar provider reuses the existing `VaultHybridSyncReviewService.ReviewAsync` for HD and `VaultLinkedDraftPublication.PrepareAsync` for the independent S-key strategy. Linked quotes still use the existing single-use `SubmitAsync`, exact fee checks, durable submitting checkpoint and historical reconciliation; **the neutral interface deliberately does not expose an alternate submit method.** The HD review only returns a quote if that exact draft is still ready, the encrypted queue is conflict-free and the current account/network is unchanged.

The linked publisher's own `PendingReview` scope now includes `VaultLinkedQueueKeySession.UnlockEpoch`. A lock followed by re-unlock of the same account invalidates its old reviewed quote even if the Stellar connection epoch is unchanged. The neutral adapter also checks the local epoch after awaits and cancels stale quotes.

**Capacity-policy limitation:** this work has not yet persisted `SingleAccount`/expansion intent in the encrypted mutation journal, nor made the publisher enforce it. Until those rules are wired, single-account `PutAsync` stays blocked and account expansion remains a separately reviewed operation. SaaS approval endpoints and UI migration are still pending.

## UI Edit and explicit business-layer Update

In the application, **Edit is a read-modify-write interaction**, not a separate blockchain operation. The UI loads the record and original revision, lets the user change values, then requests `IAccountObjectUpdate.UpdateAsync(scope, descriptor, source, expectedRevision, options)`. This is **not** an upsert and not an immediate on-chain save: it stages a revision-pinned encrypted mutation that must pass the original separate fee review, approval, signer authorization and historical ledger verification.

The neutral router now offers `IAccountObjectUpdate` as an optional capability; PWA and MAUI alias it to the existing scoped engine router. The Stellar adapter delegates to the **existing** `VaultHybridRemoteMutationWorkspace.StageUpdateAsync` for HD and `VaultLinkedDraftWorkspace.StageUpdateAsync` for independent Secret Key accounts. There is no duplicate ledger writer.

Revisions are deliberately different across strategies:
- HD: `GetAsync` returns `hd-sha256:<fingerprint>`, using the exact existing `VaultHybridRemoteMutationWorkspace.Fingerprint` format. Before staging the engine confirms the current published record matches the caller's original version; the workspace does its own fresh remote preflight before enqueuing.
- Independent Secret Key: `GetAsync` retrieves the authoritative base64 manifest version via `IStellarSecretKeyRecordRevisionReader` and returns `linked-manifest:<version>`. StageUpdate rechecks that canonical revision before sealing the new mutation. A generic SHA256 fingerprint cannot authorize an S-key update.

**Structured-field parity (current):** `ObjectDescriptor.Fields` now carries username, URL, notes and tags, all mapped to the existing Stellar `LedgerRecordDraft` without introducing a competing ledger format. `GetAsync` returns these fields. On `UpdateAsync`, the caller must provide a non-null `ObjectRecordFields` snapshot (even if all its values are empty) to distinguish an intentional clear from an old client omitting fields. Existing HD and Secret Key queue validators remain authoritative. SVTREE3 files and custom types remain outside this update contract; the original full-featured UI edit paths remain operational until migrated and tested. `SingleAccount` publication enforcement and live rich typed-record updates are not yet wired. `ObjectMutationIntent.State = Prepared` does **not** mean that an on-chain edit succeeded.

The existing device-local UI already uses a versioned compare-and-swap call, `VaultRecordWorkspaceView.SaveAsync -> Workspace.SaveTextAsync(name, text, version)`. That is a different local encrypted-record workflow; the new neutral update adapter concerns published Stellar records. Do not conflate a local save with a ledger publication.


## Durable single-account ordinary record policy

The first end-to-end capacity-policy increment now supports **ordinary record/folder creates** staged through `IAccountObjectStore.PutAsync` with either `ObjectCapacityPolicy.SingleAccount` or `ExpandWithExplicitApproval`. Both HD and independent S-key staging use their original encrypted queues and persist the requested `ObjectCapacityPolicy?` on `VaultPendingMutation` with its authenticated account/network/strategy metadata. Null is reserved for older, pre-engine journal entries; it retains legacy behavior rather than guessing the user's intent.

The original HD `VaultHybridSyncReviewService` suppresses an approval quote if a single-account mutation's read-only ledger estimate says `CreatesChildNode`. Critically, `VaultManualSyncCoordinator` **re-estimates immediately before durable submission reservation** and refuses the operation with `VaultAccountCapacityRequiredException` if the fresh estimate requires another account. The shared `VaultCapacityPolicyGuard` ensures both gates apply the same rule. `VaultSyncApprovalGuard.Digest` includes the recorded capacity choice, so a mutation cannot switch policy while retaining the previous user's fee approval.

The independent Secret Key journal also preserves the explicit choice. Its existing `VaultLinkedDraftPublication` rejects any ordinary create quote that attempts `CreatesChildNode`, and its one-shot account-expansion workflow remains separate. Unknown/corrupt policy enum values are rejected before quote/publication. **A request to allow expansion does not itself authorize an expansion transaction.**

File-tree uploads still reject `SingleAccount` through the neutral API because each child node would need a separately enforced capacity policy before publication. Revision-pinned update `SingleAccount` requests also remain blocked until their mutation policy is persisted and enforced in that publishing path. Legacy/UI-specific old mutation paths are not silently rewritten. Neither real-node Testnet verification nor the full .NET build has run in this environment.
