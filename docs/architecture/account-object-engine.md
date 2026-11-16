# Account-backed object engine — current design

## Product boundary

**VaultEngine** is a reusable, chain-neutral account-backed object database. **StellarVault** is its first application and the Stellar adapter is its first provider. An EVM adapter, standalone SDK, or hosted SaaS can consume the same object contracts later. The engine is not called StellarVault, is not a SaaS tenant/authentication service, and does not own blockchain signing keys. SaaS tenancy, hosted authentication, transport and RBAC belong outside it.

`src/VaultEngine.Core` is now an independent assembly with no project/package/framework dependencies. It owns object contracts, provider routing, approval/reconciliation capabilities, verified export destinations and provider-neutral transaction observation routing. `src/VaultEngine.Stellar` is the separate Stellar provider/service assembly: it references the engine and never StellarVault. Existing protocol bytes, HD/S-key separation, encryption, queue/CAS and signing rules are preserved.

`StellarVault.Core` has been removed entirely. The two application-native navigation/foreground event services live in `StellarVault.UI/Services`, which is shared with the MAUI host without a reverse UI-to-MAUI dependency. PWA, MAUI and shared UI explicitly reference the engine and Stellar provider. The HTTP API references the same engine and executes receipt observations through its router with a Horizon infrastructure adapter. Its hosted storage write endpoints remain unimplemented; engine participation in diagnostics does not imply hosted signing/storage support.

A future EvmVault can reference `VaultEngine.Core` and implement its own provider without referencing any StellarVault assembly. No real EVM provider is implemented by this extraction; the standalone engine test uses a test double to verify provider dispatch.

The optional `VaultEngine.Stellar.Horizon` library contains shared network/receipt HTTP integration and references only the engine. API tenant/authentication policy remains in `StellarVault.Api`. The broad `StellarVault.Application` and `StellarVault.Infrastructure` projects have been removed; future chains add meaningful provider/adaptor boundaries rather than cloning a project-layer template.

The independent static presentation host lives in `src/StellarVault.Landing`, with public assets under `wwwroot` and host configuration outside the document root. It has no .NET dependency or wallet/API implementation.

## Storage API and lifecycle

- `IAccountObjectStore`: `EstimatePutAsync`, `PutAsync` (new object staging), `GetAsync`, `OpenReadAsync`, `DeleteAsync`. Delete still requires a strategy-specific revision-bound journal adapter and is not generally wired.
- `IAccountObjectUpdate`: explicit `UpdateAsync(scope, descriptor, source, expectedRevision, options)`. It is **not upsert**. HD uses `hd-sha256:` followed by its existing remote-record fingerprint; imported S-key accounts use `linked-manifest:` followed by the canonical public manifest revision. Both staging workspaces recheck the original revision during encryption/queue insertion.
- `IObjectMutationReview`: read-only HD Sync review or existing one-shot S-key quote preparation and cancellation. The result means **AwaitingApproval**, not signed, transmitted or verified. The review is an opaque, short-lived, one-shot reference; it retains immutable public quote data only. The existing publishers retain signing and durable CAS responsibilities.
- `IObjectMutationExecution`: `SubmitApprovedAsync` consumes the exact current review with explicit fee/reserve and uncertain-outcome acknowledgments (plus separate public-network consent). It rechecks scope, local unlock epoch, linked signer epoch and selected endpoint, then delegates to the original HD or linked publisher. All sends remain `SubmittedUncertain`; it never retries or invents a second transport.
- `IObjectMutationReconciliation`: manually routes a recorded-hash unresolved mutation through the existing strategy-specific historical verifier and durable CAS. Missing or rejected evidence remains unresolved. No candidate hash assignment, broadcast, or retry occurs.
- `IVerifiedObjectExport`: streamed SVTREE3 verification into `IVerifiedObjectDestination`; all nodes and final SHA-256 are authenticated before a private destination may commit. `AtomicFileObjectDestination` offers non-overwriting same-directory temporary-file rename on trusted local filesystems. `OpenReadAsync` offers bounded 8 MiB verified in-memory reads. The destination may not expose unverified plaintext.
- `ObjectDescriptor`: stable object ID/type/name/MIME/parent plus optional `ObjectRecordFields` carrying username, URL, notes and tags. Ordinary credentials and folders reuse the existing `LedgerRecordDraft` representation. An update requires an explicit full fields snapshot rather than silently clearing fields omitted by the caller. Custom types and immutable file-tree edits require their own versioned protocol.

Staging via Put/Update/Delete returns `Prepared` and never broadcasts, automatically expands accounts or claims a committed object. Separate explicitly approved execution delegates to the original provider publishers. Verification and signed historical reconciliation remain distinct capabilities.

## Mandatory account-capacity policy — **no backward compatibility**

The product has **not launched**, so there is **no legacy queue migration or permissive null-policy mode**.

`VaultPendingMutation.CapacityPolicy` is a non-null `ObjectCapacityPolicy`; its encrypted JSON field is **required**. Entries missing it are rejected during deserialization. The default for direct/internal queue creation is the strict `SingleAccount` product policy, **never implicit expansion**. The neutral API carries the caller's requested `ObjectWriteOptions.CapacityPolicy`; invalid enum values fail closed.

- **SingleAccount**: the existing account/chain capacity only. Both HD fee review and final publish-time fresh estimates reject `CreatesChildNode`, *before* durable CAS submission reservation. This also applies to version-pinned HD updates when their estimate requires expansion.
- **ExpandWithExplicitApproval**: may present account-expansion review, but does not authorize its transaction, unlock a signer, or auto-submit the deferred record. S-key ordinary quotes already reject `CreatesChildNode` and route actual chain expansion through their separate reviewed expansion coordinator.
- **Files**: HD and imported S-key `StageTreeFileAsync` persist the selected policy on **every encrypted SVTREE3 node**, not only the root. The HD final publisher checks each queued create again. Actual file publication still depends on dependency ordering, signed hash checkpoints, manual fee approval and independent historical reconciliation. No blanket unlimited-upload or production claim is warranted.
- **Approval binding**: the HD approval digest includes the exact durable capacity policy. Changing policy invalidates an old quote. The S-key fingerprint includes the serialized mutation including policy. Users must re-review any different policy.

Do not add pre-launch legacy fallbacks or treat absent capacity policy as permission to extend the chain.

## First provider and host usage

`StellarAccountObjectStoreProvider` reuses existing services: `VaultHybridDraftWorkspace` and `VaultLinkedDraftWorkspace` for staged creates and SVTREE3 files, `VaultHybridRemoteMutationWorkspace` and the linked revision reader for edits, `VaultHybridSyncReviewService` and `VaultLinkedDraftPublication` for fee reviews, and existing authenticated ledger/tree readers for Get and verified export. The neutral router is scoped and both PWA and MAUI resolve its optional capabilities through the same scoped instance.

HD mnemonic identity and independent imported S-key identity remain separate signing strategies behind a single shared UI. The engine never reclassifies an imported S-key as HD recovery. The original strategy-specific UI remains live during incremental migration; UI Edit loads a record+revision and requests business Update, not a blind Set.

## Remaining engineering and release gates

1. **Build/test current HEAD**: .NET 10 build, Core smoke harness, Node regression, PWA and MAUI runtime DI. Source-level checks and commits alone do not confirm compilation.
2. **Neutral approval/submission lifecycle validation**: the execution/reconciliation bridge is implemented and locally exercised through encrypted HD/linked queues with mocked transports. Live historical XDR, interruption/restart, native and cross-device validation remain required. Existing strategy/network restrictions still apply; this is not a hosted signed-envelope relay.
3. **Full file workflow**: end-to-end SVTREE3 per-node fee approvals, root-last historical reconciliation, restart and interruption recovery, clean-device restoration and platform-specific private export destinations.
4. **Complete record types and CRUD parity**: safe delete, versioned custom types, MIME/text discriminator and UI migration without different visuals per identity strategy.
5. **Additional consumers**: independent engine/provider assemblies are extracted and tested. Complete the HTTP storage adapters and migrate remaining application workflows to neutral capabilities where they match the existing approval/signing contracts; implement a real EVM provider separately.
6. **Security and device validation**: Testnet HD/S-key workflows, permission/consent testing, large files, signed revision race checks, native/browser secure storage behavior, external review.

**Production status: NO-GO.** Local build/test evidence is recorded in the engineering handoff; it does not establish device/Testnet or independent security validation.
