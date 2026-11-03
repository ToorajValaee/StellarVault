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
