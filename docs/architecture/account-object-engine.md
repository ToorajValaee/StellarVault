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

The next implementation milestone is a real **Stellar provider adapter** that calls those existing components while retaining the original authenticated drafts, signed history proof and manual approval pipeline. Do not map EVM contract semantics onto Stellar account-data operations. The EVM provider comes later and may choose different native storage implementation, proof and gas rules without altering the object-store API.

## Migration and honesty boundary

The first commit introduces the neutral public interface, metadata/quote/intent types, strict provider router and Core smoke tests. It intentionally does **not** register a runtime Stellar provider, migrate PWA/Android components, launch SaaS endpoints or support EVM storage yet. This is the architectural seam needed for subsequent incremental wiring and tests.

The API must never take mnemonic/secret-key bytes by default. Browser/native callers keep local encryption and signing. A hosted adapter receives only explicitly authorized encrypted payloads and signed intents. Data confidentiality, availability, durable operation journals, cost commitments, tenant separation and error recovery require independent verification before launch.

Names such as `StellarVault` remain appropriate for the **application and existing compatibility code**, but new chain-neutral interfaces should be under `VaultEngine.Core` or an equivalently neutral engine namespace.
