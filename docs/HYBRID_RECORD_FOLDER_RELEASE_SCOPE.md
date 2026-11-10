# StellarVault — Hybrid records/folders release scope
Updated: 2026-11-10

## Product boundary
Hybrid is **encrypted local records and folders**, with explicit, individual paid synchronization to the selected Stellar HD vault. Direct on-chain files remain a separate feature. Hybrid file uploads, deletions, fee approval, and publish are hard-disabled by `VaultHybridFilePolicy` at Core and queue boundaries and hidden in both PWA and MAUI hosts. Legacy encrypted file journals are preserved for read-only investigation, not replay.

## Implemented development workflow
1. User explicitly opts into encrypted local persistence and unlocks the account-scoped device vault. Host adapters encrypt the queued mutation envelope; no unsigned local draft is represented as on-chain data.
2. Shared offline editor creates/revises/discards record and folder drafts. Stable entity IDs and parent dependencies are preserved with durable CAS. A pending parent cannot be bypassed; after independent confirmation its descendants become eligible. Confirmed historical updates to a parent are not treated as duplicate folder origins.
3. Shared remote-change editor stages version-pinned record/folder updates or deletions based on an authenticated connected ledger snapshot, maintains the originally pinned baseline during offline revision, and offers an explicit preserve-as-new-record fallback for a conflicting record update.
4. Shared review exposes ready, blocked, conflicting, and uncertain entries; fee quotes require the same unlocked account/network/session epoch and matching connected HD root. No quote is itself an authorization.
5. Explicit one-mutation approval uses a short-lived draft digest, fee/reserve ceiling, fresh ledger preflight, durable CAS reservation, and signed transaction hash checkpoint before broadcast. After submission the outcome is Uncertain, never auto-retried.
6. Exact historical evidence can reconcile supported create/update mutations. Unresolved or unrecorded transaction hashes remain quarantined. Deletion historical owner-journal review is **diagnostic only** and does not certify a queue delete.
7. Both hosts use the shared Core services and shared Razor hybrid components. Platform-specific encrypted storage and signing transports remain host adapters.

## Deliberate release restrictions
- The initial Mainnet-capable Hybrid publish scope is **CREATE record / CREATE folder** with individual approval and independent confirmation. Offline editing of published records/folders can still be staged on any network but **UPDATE/DELETE broadcasting is Testnet-only** because Stellar ManageData has no enforced compare-and-swap against the prior value.
- Multi-owner DELETE transactions are not atomic. Even a complete independently authenticated tombstone journal and a currently absent record do not eliminate concurrent-write races. Do not present deletion as confirmed or promise automatic recovery.
- Hybrid file actions stay disabled. Existing signed file journals must not be discarded or retransmitted automatically.
- No background synchronization: all network writes need user approval, one at a time. No optimistic success on unknown status.
- Direct on-chain CRUD/file paths are separate and must not be routed through the offline queue.

## Implementation freeze / next phase
The scoped records-and-folders workflow is implemented in source. **Do not infer release readiness from implementation status.** Next phase is explicit validation (no tests were manually executed as part of this implementation-only pass): shared/Core builds, encrypted storage and CAS failure injection, folder dependency graph, quote expiration, account/network switching, historical receipts, interruption/restart/no replay, PWA and native MAUI UI/storage parity, end-to-end Testnet, and security/release review. Any failure must reopen development. Mainnet remote update/delete and deletion-confirmation limitations remain explicit architecture decisions requiring a new ledger-enforced version protocol, not a test-only fix.
