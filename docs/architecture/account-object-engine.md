# Account-backed object engine — current design

## Product boundary

**VaultEngine** is a reusable, chain-neutral account-backed object database. **StellarVault** is its first application and the Stellar adapter is its first provider. An EVM adapter, standalone SDK, or hosted SaaS can consume the same object contracts later. The engine is not called StellarVault, is not a SaaS tenant/authentication service, and does not own blockchain signing keys. SaaS tenancy, hosted authentication, transport and RBAC belong outside it.

The existing `StellarVault.Core` project temporarily contains the neutral `VaultEngine.Core` namespace and Stellar-specific adapters. Moving this namespace to an independently deployable assembly remains a build-verified extraction task; a namespace alone is not a separate library.

## Storage API and lifecycle

- `IAccountObjectStore`: `EstimatePutAsync`, `PutAsync` (new object staging), `GetAsync`, `OpenReadAsync`, `DeleteAsync`. Delete still requires a strategy-specific revision-bound journal adapter and is not generally wired.
- `IAccountObjectUpdate`: explicit `UpdateAsync(scope, descriptor, source, expectedRevision, options)`. It is **not upsert**. HD uses `hd-sha256:` followed by its existing remote-record fingerprint; imported S-key accounts use `linked-manifest:` followed by the canonical public manifest revision. Both staging workspaces recheck the original revision during encryption/queue insertion.
- `IObjectMutationReview`: read-only HD Sync review or existing one-shot S-key quote preparation and cancellation. The result means **AwaitingApproval**, not signed, transmitted or verified. The engine deliberately exposes no alternate signing/broadcast path.
- `IVerifiedObjectExport`: streamed SVTREE3 verification into `IVerifiedObjectDestination`; all nodes and final SHA-256 are authenticated before a private destination may commit. `AtomicFileObjectDestination` offers non-overwriting same-directory temporary-file rename on trusted local filesystems. `OpenReadAsync` offers bounded 8 MiB verified in-memory reads. The destination may not expose unverified plaintext.
- `ObjectDescriptor`: stable object ID/type/name/MIME/parent plus optional `ObjectRecordFields` carrying username, URL, notes and tags. Ordinary credentials and folders reuse the existing `LedgerRecordDraft` representation. An update requires an explicit full fields snapshot rather than silently clearing fields omitted by the caller. Custom types and immutable file-tree edits require their own versioned protocol.

The provider exposes only `Prepared` when locally staging encrypted records/files. It does not broadcast, automatically expand accounts or claim a committed object. Stored verification, exact fee authorization and signed historical reconciliation remain provider responsibilities.

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
2. **Neutral approval/submission lifecycle**: bind immutable mutation revisions and exact signer-scoped quotes to the existing durable CAS publisher, handle uncertain outcomes without retries, and verify historical transaction inclusion. No parallel transaction writer.
3. **Full file workflow**: end-to-end SVTREE3 per-node fee approvals, root-last historical reconciliation, restart and interruption recovery, clean-device restoration and platform-specific private export destinations.
4. **Complete record types and CRUD parity**: safe delete, versioned custom types, MIME/text discriminator and UI migration without different visuals per identity strategy.
5. **Independent packaging and additional consumers**: extract `VaultEngine.Core` to its own assembly after dependency audit; SaaS API and EVM provider come after Stellar correctness.
6. **Security and device validation**: Testnet HD/S-key workflows, permission/consent testing, large files, signed revision race checks, native/browser secure storage behavior, external review.

**Production status: NO-GO.** No fresh .NET/Node execution or real-device validation is claimed by these connector-only commits.
