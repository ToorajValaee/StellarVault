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

Do not treat this as published file storage: files still require explicit per-node synchronization and historical proof. `GetAsync` / `OpenReadAsync` for SVTREE3 remain intentionally unsupported in the first provider; they must delegate to the existing verified tree reader when wired. Text/plain file uploads are rejected until type discriminators preserve file semantics. Real device streaming, interruption rollback, capacity and quote verification are still mandatory.

## Local authorization isolation

The linked encrypted queue has its own unlock epoch independent of the ledger connection epoch. The common object provider checks that epoch before and after staging a record, and the underlying `VaultLinkedDraftWorkspace` now includes it in its scope checkpoint for *all* awaited operations. Locking and re-unlocking the same S-key root offline cannot resume a previously authorized in-flight draft operation under the replacement local key session.
