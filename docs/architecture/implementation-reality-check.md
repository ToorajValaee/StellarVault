# StellarVault implementation reality check — 2026-11-15

This is an evidence-based correction to repeated development reports. **Existing in Core** is NOT the same as **wired for each account strategy**, **executed successfully**, or **production verified**. Never present existing features as missing simply because independent S-key parity is incomplete.

| Capability | Existing, confirmed from source | Current integration boundary |
|---|---|---|
| HD on-chain CRUD | Existing `IStellarLedgerRecords` and shared ledger workspace | Do not rebuild. Runtime/release verification is separate. |
| Independent S-key on-chain CRUD | Existing `IStellarSecretKeyLedger`, browser JS ledger, `VaultSecretKeyWorkspace` with quote/create/update/delete, account expansion | On-chain feature exists. Do not describe linked CRUD as unimplemented. Verify real Testnet and signer evidence before production. |
| Encrypted offline queue, consent and CAS | Existing `VaultPendingMutationQueue`, IndexedDB and filesystem encrypted stores, `VaultLocalQueueKeySession` | Mature HD design; linked S-key uses separate wrapped key and storage scope, reusing those stores. |
| HD/hybrid single-shot fee-approved sync | Existing `VaultManualSyncCoordinator`, `VaultHybridSyncReviewService`, `VaultSyncApprovalGuard` | Not missing. The linked strategy's signing/quote protocol differs and needs specific authorization/integration checks. |
| Folder hierarchy and dependency ordering | Existing `VaultQueuedCreatePolicy`, `VaultHybridFolderSyncPlan` and folder workspace | Reused by linked local create/edit/manual review. No second folder graph required. |
| Encrypted binary upload/chunks/recovery | Existing `StellarLedgerFileProtocol`, `StellarLedgerUploadCoordinator`, `VaultHybridFileWorkspace` and deletion/review mechanisms | Already implemented for HD paths, sometimes policy disabled; **linked signer file publication adapter** not wired. Do not say files as a whole are missing. |
| Receipt, registry, network history | Existing `HorizonVaultTransactionInspector`, historical registry/network inspectors, `HorizonVaultLinkedReceiptEvidence` | HD evidence path exists; linked S-key needs exact historical operation and signer policy proof before uncertain journals can be safely released. |
| Native/PWA shared Core and UI | `StellarVault.Core`, `StellarVault.UI`, both hosts and MAUI/native adapters | Existing architecture; host parity requires build/device verification, not a rewrite. |
| Linked offline local draft editor and manual create publication | Existing isolated `VaultLinkedDraftWorkspace`, `VaultLinkedDraftPublication`, `VaultLinkedQueueKeySession`, shared queue panel | Added in recent branch, reuses original encrypted queue. Source exists; neither full .NET build nor real Testnet execution is verified. |
| Linked account expansion | Existing `VaultLinkedAccountExpansion` direct on-chain reviewed workflow | Recent `VaultLinkedExpansionJournal` persists intent only for explicitly consented **queued** path; direct workspace still uses its original session quarantine. |
| Linked uncertainty assessment | Existing `VaultLinkedDraftRecoveryReview` and advisory receipt evidence | Read-only; **not** independent proof and cannot safely clear or retry a possible broadcast. |

## Why progress reports were misleading

1. Repeated “continue” turns added strategy-specific wrappers and new status documentation without first running a complete code capability inventory.
2. “Already implemented”, “available in HD”, “integrated in linked S-key”, and “production verified” were conflated. This led to repeatedly listing HD files, queue engines, manual sync and folder machinery as if they did not exist.
3. Commits were fragmented instead of being grouped into one end-to-end outcome and tested. The engineering handoff grew excessively while no reproducible .NET build/Testnet green result was established.
4. GitHub Actions was explicitly removed at the user's request to avoid runner/token consumption. This must remain disabled; the local/manual test route is a separate delivery requirement.

## Actual remaining work; do not expand without an audit

- Verify a clean .NET 10 Core, shared UI, PWA and MAUI build and run existing Core crypto and Node regression suites **locally**, not using GitHub Actions.
- Verify HD workflows already present are not regressed by independent S-key code.
- Confirm complete linked S-key record/folder local-create -> fresh quote -> approved broadcast -> independent on-chain operation/signer evidence -> durable queue confirmation/reconciliation on real Testnet.
- Adapt EXISTING HD file/chunk workspace and existing receipt verifier for the linked signer; do not build a competing protocol or queue.
- Make uncertain linked transactions restart-safe and independently provable before ever releasing quarantine. A matching current record is not proof of specific historical operations.
- Test both PWA and MAUI foreground/background/disconnect and multi-tab journal arbitration on real hosts.
- Review security, user-consent data retention/deletion, release deployment and public network readiness.

## Verification status

The source inventory above was inspected through the connected repository. No `dotnet` SDK was found in this execution environment. Code additions and source-only JavaScript parsing are not equivalent to build/test success. Do not give a percentage of production readiness without measured end-to-end gates.

## Recommended engineering approach

**Stop creating new architecture layers.** Pick one existing shared operation per turn, finish its linked-strategy adapter, run a reproducible local integration test, and report only: reused code, exact missing integration, committed behavior, and verification evidence. Do not mark complete based on documentation or source-pattern tests alone. EVM remains a later priority. Preserve separate HD/mnemonic and imported S-key vault entry paths.

## 2026-11-15 linked draft confirmation hardening
A current decrypted record matching an approved draft is not historical proof of a specific linked submission. The linked queue create publisher now leaves even successful, snapshot-matching submissions in `Uncertain`, with transaction hash retained, until independent signer/operation provenance can authorize confirmation. Parent/child queue progression stays blocked. This deliberately reduces availability to protect correctness and is not a finished reconciliation workflow. Verified only by source review so far; local Core smoke and Node suite must be rerun.

## 2026-11-15 expansion journal confirmation guard
Approved linked S-key account expansion now remains durably `Uncertain` even if the signer transport reports a successful transaction hash and a new tail is visible. The exact historical CreateAccount/SetOptions signer/pointer provenance is not yet proven. Queue replay and further dependent writes remain blocked until a separately reviewed reconciliation protocol is implemented. This is a deliberate fail-closed safety behavior, not a complete manual-sync or production solution. Local smoke execution is required.

## 2026-11-15 pre-broadcast signed hash recovery
The S-key signer and Core queue now preserve a scope-bound **signed transaction hash before Horizon broadcast**. Uncertain queues can retain it after a network timeout, and the read-only recovery reviewer can check the corresponding Horizon receipt after restart without relying on volatile JavaScript diagnostics. The hash is a public investigation handle, not a cryptographic attestation of operation content, signer policy or historical ciphertext. **No `Uncertain → Confirmed` reconciliation is enabled**; additional source, live Testnet and security verification are required. Source committed, executable validation pending.

## 2026-11-15 atomic signed-hash checkpoint before broadcast
The previously advisory pre-broadcast hash is now durably CAS-checkpointed for **journal-backed S-key create and linked expansion** before the transport can contact Horizon. The JavaScript signer awaits the C# IndexedDB/native filesystem store callback and refuses network submission on checkpoint failure, closing a power-loss interval between signing and storing the public transaction hash. The `Submitting` journal remains unresolved after an unknown outcome. This does not cover direct non-journaled writes and is not inclusion proof or historical signer/record verification. Local build/Testnet runtime validation is pending.

## 2026-11-15 linked historical skh/skc proof increment
Read-only Horizon historical S-key record operation inspector checks a single transaction's manifest + ordered encrypted chunks and SHA-256; this is **not** signer authorization, authenticated vault lineage, historical decryption to the reviewed record, or durable confirmation. The queue remains fail-closed. Executable tests pending local validation.

## 2026-11-15 linked expansion historical operation shape
A bounded read-only Horizon inspector now independently checks that the linked expansion transaction's CreateAccount, SetOptions and parent ManageData pointer have the expected operation structure, including optional native reserve top-up. The encrypted expansion journal remains blocked: observing a successful transaction and matching operation shape is **not** proof of historical signed authorization or parent lineage. Source committed; local mocked tests, live Testnet and security assessment pending.

## 2026-11-15 S-key queued update/delete publication
Independent S-key existing-record updates/deletes now use the existing encrypted, consented queue with exact original `skh` manifest revision, staged original remote deletion snapshot, explicit fresh fee quote, immutable CAS reservation and pre-broadcast signed-hash persistence. New UI actions stage edits/deletions without network writes, and publication enforces optimistic concurrency both before quoting and immediately before broadcast. JS signing rechecks original on-ledger manifest, and folder moves reject parent cycles. All outcomes still leave the queue `Uncertain` until actual historical decryption/signer proof is available. This closes *staging and publication*, not the production synchronization/reconciliation loop. Fresh executable tests are pending on the latest feature HEAD.

## 2026-11-15 proof-gated S-key CAS finalization and SVFILE1 preview
Independent linked encrypted journals now have a distinct cryptographic recovery coordinator for record/folder create/update/delete and linked-account expansion. The coordinator uses a previously durable hash, selected-network Horizon receipts, cryptographically hashed and signed XDR, decrypted historical record equality (or dual-signed child expansion) and fresh state checks before a single immutable `Submitting/Uncertain → Confirmed` encrypted CAS. It does not replay or auto-submit any transaction. The shared UI invokes finalization explicitly.

The shared SVFILE1 codec is now available through a typed linked-only single-record encrypted file workflow for **1–2048 bytes**, including checksum validation, local encrypted staging, reviewed create publication and verified download. This is not SVFILE2 multipart parity or a claim that the application's general 1 MiB file limit is supported on S-key. Tests were committed, not locally executed here. Mainnet, legacy migration and security assessment gates remain unchanged.
