# StellarVault capability reuse audit — 2026-11-14

This document corrects the ambiguity in earlier "remaining work" reports. **Already implemented**, **integrated in both application hosts**, **enabled for mnemonic/HD workflow**, **enabled for independent S-key workflow**, and **production verified** are separate claims. Do not report all of these as "missing" when only strategy-specific wiring is absent.

| Capability | Existing implementation | HD / hybrid use | Independent S-key use | Actual gap |
|---|---|---|---|---|
| Encrypted local pending-mutation storage | `IVaultPendingMutationStore`, `IndexedDbVaultPendingMutationStore`, `FileSystemVaultPendingMutationStore`, `VaultPendingMutationQueue` | Present, behind device-retention consent and unlocked local vault key | Not plugged in | Rebind an **independent authenticated S-key-derived storage-key lifecycle** before any S-key journal. Reuse queue CAS/store/consent mechanics; never silently use HD key. |
| Manual sync, approved fee review, uncertain-state queue transitions | `VaultManualSyncCoordinator`, `VaultHybridSyncReviewService`, `VaultSyncApprovalGuard` | Implemented on HD transport | Linked ledger uses its own quoted submit, no durable queue orchestration | Extract account-neutral approval/queue state policies, adapt ledger quote/verification, preserve distinct signer and account-allocation strategies. |
| Folder dependency ordering | `VaultHybridFolderSyncPlan`, `VaultQueuedCreatePolicy` | Existing shared Core planner with HD publication | Record/folder wire fields supported; offline linked folder publishing not wired | Reuse folder graph and validation, adapt destination transport and record identity. Do not build another folder dependency engine. |
| Hybrid file staging and encrypted queue | `VaultHybridFileWorkspace`, `VaultHybridFileDeletionWorkspace`, `VaultHybridFilePolicy` | Staging/recovery implementation exists; *publication currently disabled by product policy in some flows* | Not wired | Reuse bounded streaming, metadata, encrypted queue and checkpoint/recovery. New S-key binary protocol/adapter must be reviewed; do not claim hybrid files production-ready. |
| On-chain file protocol and chunking | `StellarLedgerFileProtocol`, `StellarLedgerUploadCoordinator`, checkpoint stores | HD file path exists | No linked-key file operation bridge | Reuse chunk planning/file records where wire format permits; adapt S-key signing and proof. |
| Historical receipt and registry inspection | `HorizonVaultTransactionInspector`, `HorizonVaultHistoricalRegistryInspector`, `HorizonVaultNetworkInspector`, `HorizonVaultLinkedReceiptEvidence` | Existing HD evidence pipeline | Receipt/network subset now reused for read-only S-key diagnosis | Verify **exact S-key operations, child signer policy and encrypted record generation**. HD registry HMAC cannot be reused as proof of linked-key history without adapting protocol. |
| Runtime submission uncertainty protection | `VaultPendingMutationQueue` durable CAS transitions on HD, `vault-secret-ledger.js` S-key runtime guard | Durable encrypted queue on opted-in HD/hybrid flows | Same-runtime root/network quarantine now implemented | Cross-tab/device arbitration and explicit consent-backed encrypted linked-key durable journal. No unapproved local storage. |
| Shared record-type classification | `VaultRecordTypeFactory`, `VaultStellarDraftClassification` | HD record workspace uses it | Linked write planner uses it | Extend explicit type metadata and file transport, **not** duplicate record factories. |
| Account strategy and inventory | `VaultAccountStrategyFactory`, `StellarHdAccountStrategy`, `StellarLinkedAccountStrategy` | Connected HD inspection | Connected linked inspection; auto capacity planning on create | Genuine verified child enumeration and reuse of existing strategy-specific publication approvals. HD derived children and independent linked S-key children remain separate. |
| PWA/native UI and core sharing | Shared `StellarVault.UI`, Core services, MAUI linked PWA adapters | Large shared stack | Shared linked UI and JS ledger | Validate parity and remaining S-key file/hybrid UI flows; avoid rebuilding components. |

## Immediate refactor rule

1. **Inventory the existing implementation first**, including any product-policy-disabled feature and its tests.
2. If the mechanism is already in Core, extract its **account-neutral mechanics** and keep protocol-specific signing, key derivation, receipt proofs and network rules behind adapters.
3. Never route independent imported S-key mode through the mnemonic-derived HD session, HD registry HMAC, or an unrelated local vault key.
4. Never silently enable disabled hybrid file publication, resume an uncertain transaction, store an unencrypted journal, or change Mainnet write policy.
5. Label status precisely: *implemented*, *registered*, *end-to-end wired*, *policy enabled*, *tested*, *audited* and *production ready* are different.

## Refactor performed with this audit

The existing `IVaultRecordTypeFactory` now feeds one `VaultStellarDraftClassification` in Core, reused by the HD `VaultLedgerRecordWorkspace` and linked `VaultLinkedRecordWritePlanner`. Both hosts register the same class. No new storage format, signer behavior, account strategy, fee policy or on-chain mutation is introduced by this change.

## Validation and priorities

This audit is based on inspected code paths, **not executed test results**. Legacy queue, sync and binary code may have restrictions, TODOs or defects despite being present. Do not assert they are complete merely because service classes exist.

Next: reuse the existing approved-sync review and evidence primitives while adding an independent credential scope for linked S-key mode; then binary/file parity and end-to-end tests. EVM remains final priority.

## 2026-11-14 integration increment — shared approval scope, not a new queue

Extracted `VaultApprovalScopeGuard.RequireFresh` to validate account address, exact scoped network identity and quote lifetime without knowing mnemonic, imported secret, encryption key, transaction quote format or transport. The existing HD `VaultSyncApprovalGuard.RequireFresh` now reuses it while retaining its original encrypted-queue draft fingerprint and pending/CAS checks. S-key `VaultLinkedAccountExpansion` now uses the same scope+age guard for its single-use reviewed expansion quote, in addition to exact quote equality, strategy selection, signer transport and current chain count/tail preflight. S-key quote remains capped at two minutes; transport already has its own quote expiry.

**Not integrated yet:** the encrypted HD hybrid queue cannot simply be registered against an independent S-key vault because its key provider and session manager belong to a separate local vault; similarly, the HD `VaultManualSyncCoordinator` accepts `IStellarLedgerRecords` not the linked-key quote transport. The next actual integration is to decouple those interfaces around the existing CAS/queue/recovery state machinery, establish a separate authenticated S-key local encryption key, and reuse existing policy, before enabling linked offline sync. This is not missing foundational queue technology.
