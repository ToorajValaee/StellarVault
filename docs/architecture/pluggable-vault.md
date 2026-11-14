# Pluggable vault architecture (2026-11-14)

## Core invariant

Core manages logical vault content, encrypted persistence, record versions, conflict rules, and explicit user-approved synchronization. A blockchain adapter is responsible for chain transactions, authorization, fees, confirmations, and verification. No adapter can assert remote commit from a local encrypted save. Never treat Horizon receipts alone as historical record proof.

## Orthogonal choices

1. **Account provisioning strategy** selects how an account is created/discovered/recovered. Stellar HD (SEP-5) and imported S-key linked accounts are independent first-class strategies. The linked strategy must verify child creation, configured parent/root signer weight and threshold, funding/reserves, and chain registry before reporting readiness. Reuse the current JS workflows only behind a verified adapter; no key material in account handles.
2. **Record type factory** selects stable logical types: text, file, folder, credential, later extensible types. MIME type and filename are *hints*, not trustworthy executable semantics. Unknown types remain opaque, not auto-previewed. The new factory is domain-only: it does not persist, encrypt, or serialize records.
3. **Storage mode** independently selects on-chain, opted-in encrypted local, or hybrid workflows. A local save must never imply on-chain publication. Encrypted chunk storage and authenticated indexes remain shared; chain-specific publication is adapter-owned.
4. **Blockchain adapter** determines capabilities, signer and transaction construction, record transport, fees, and finality semantics. EVM needs different implementations for HD derivation, smart-contract storage and programmable authorization; it cannot be implemented by substituting an EVM keypair into Stellar operations.

## Compatibility decisions

- Existing local catalogs currently distinguish text from files only by ContentType. A null content type is historical text; `text/plain` and `text/plain;...` are text. The new `VaultRecordTypeFactory.ClassifyLegacy` preserves this mapping. Do not rewrite stored ciphertext or indexes as part of the type registry introduction.
- Existing on-chain HD ledger and imported S-key JS workflows are not migrated by this change. No live strategy is registered yet. Do not call the new abstractions complete integrations or enable any Mainnet flow.
- Folder hierarchy, content type metadata, encrypted file chunks and pointers remain as-is. When a future versioned record envelope is introduced, add logical type ID, type schema version, canonical content digest, encrypted metadata, parent relation, and explicit optional file media descriptors. Unknown future types must roundtrip without reinterpretation.

## Future sharing and portable import/export

Sharing must be **explicitly authorized**, recipient-bound, revocable where the transport allows, and use new recipient-specific wrapping keys or grants. Never share a mnemonic, imported S-key, account signer key, or the master encryption key. A share link is not proof of permission; authenticate identities, scope, expiry, changes, and revocation.

Export must use an authenticated, versioned format with stable logical record IDs/types, encrypted payloads and manifests, chunk integrity, provenance/network context, and documented KDF parameters. Import must parse untrusted data under strict size/format limits, verify authentication before plaintext release, handle unknown types safely, and use conflict-aware remapping (including folder references). Cross-chain export/import must not pretend an EVM receipt is a Stellar transaction. Neither workflow is implemented by these contracts.

## Next implementation increments

- Wrap existing SEP-5 and S-key provisioning/discovery with concrete `IVaultAccountProvisioningStrategy` implementations, without changing existing signing paths.
- Route existing text/file creation through the record factory and retain legacy classification until schema migration is designed and tested.
- Introduce chain adapters behind current ledger transport and establish a cross-chain-independent encrypted record envelope.
- Build authenticated sharing and portable import/export **after** versioned metadata, recovery, and remote commit verification are stable.

## Implemented integration increment

- PWA and MAUI register a shared `IVaultRecordTypeFactory`, and the current local encrypted `VaultRecordWorkspace.SaveTextAsync` and `VaultBinaryWorkspace.SaveAsync` obtain their media definitions through that factory. The existing indexed ciphertext writer and read contracts have not changed.
- `VaultConnectedStellarAccountInventory` is registered in both hosts and inspects the selected *already-connected* HD or S-key account inventory. It does not create accounts, traverse the full linked chain, validate root signer thresholds, authorize transactions, or claim independent receipt verification.
- The new `VaultAccountStrategyFactory` remains an extension contract, not a live transaction route. A future `CreateNextAsync` implementation must preserve fresh quote/approval/fee/reserve checks and confirmed child authorization; never automatically submit because of a factory request.
- **Priority order:** finish Stellar HD and S-key account workflow parity, recovery, records/files and synchronization first. Then sharing and import/export. EVM remains a last-priority architecture consideration, not an active implementation target.

## Stellar-first integration increment
- The factory now dispatches concrete HD and linked-key strategies for already-connected **read-only account inspection**, with chain-neutral output. The linked workspace uses this to validate root, count and tail against selected strategy. CreateNext and full per-node discovery deliberately reject until verified workflow support exists; no unapproved transaction may occur.
- Existing HD record estimate/publish/update and linked-key quoted record drafts consult the shared logical type factory, with no payload format migration.
- New local binary text/plain records are blocked because legacy MIME-only metadata cannot distinguish a plain-text file from editable text. A versioned type discriminator will remove this restriction later.
- EVM is last priority: no EVM implementation is authorized in this phase.

## Account provisioning safety boundary — current implementation
- `VaultLinkedAccountExpansion` is the shared Core coordinator for the existing S-key chain's explicit expansion action. The selected UI displays the exact quote (fees, reserve, top-up and signer change) and obtains fresh approval before invoking it; the coordinator verifies the same quote and current chain count/tail before transport submission.
- It binds quotes to the selected Stellar network credential namespace, so changing custom-network passphrase invalidates pending reviews. Each review is single-use and not automatically retried if submission outcome becomes uncertain. An increase in connected chain count is only a session observation, not independent historical signer proof.
- This does not make `VaultAccountStrategyFactory.CreateNextAsync` safe to invoke generically. HD child allocation remains bound to HD record publication, and linked-child creation needs its separate reviewed quote. Cross-tab/durable submission coordination and full child signer authorization proofs are still required.
- EVM remains a future architecture consideration only.

## 2026-11-14 storage-driven rollover implementation
A new linked-key record save now calls the shared `VaultLinkedRecordWritePlanner`; the underlying Stellar planner determines if the current tail exceeds its subentry capacity. The UI no longer asks the user to independently extend the chain. If capacity is needed, the Core planner prepares a linked-account expansion quote, the user explicitly reviews creation fees/reserves/signer changes, and, after verifying the advanced chain, Core prepares the original record-save quote for another explicit approval. A successful expansion is **not** a successful record save. No background submit occurs.

The current trigger is narrowly the new-record subentry limit, not every possible reserve, funding, operation-count, or storage limit. Updating an existing record cannot silently relocate it to another chain account. Recovery after interruption and durable multi-stage intent require future authenticated checkpoint/reconciliation design. EVM remains last priority.

## Rollover headroom and uncertain-submission guard
For new linked records the planner reserves the final available data entry on the tail for the signed chain pointer. Once a tail reaches the absolute entry ceiling, the current pointer design cannot safely extend it. No workaround or blind new account creation is attempted. Oversized writes that exceed the atomic operation limit cannot be fixed by account rollover, and existing record revisions stay on their original account until an authenticated relocation protocol exists.

An uncertain S-key transaction quarantines the current JS and UI session against further writes. A read-only refresh can inspect the chain but cannot clear the uncertainty. This is a defense-in-depth in-memory guard, **not** durable synchronization or historical recovery. A fresh session still requires transaction reconciliation.

## Read-only uncertain submission inspection
A linked-ledger submission reaching the network attempt boundary exposes an in-memory public-only snapshot (operation type, root and target accounts, record ID, network identity, optional transaction hash). The snapshot guides investigation, but cannot prove execution. UI refresh does not clear quarantine and there is no retry action. Session closure destroys the snapshot: persistent recovery must respect explicit device-storage consent and independently verify historical ledger operations before unblocking. EVM stays out of scope.

## Recovery inspection versus durable replay protection
The shared linked recovery inspector observes current account/record visibility without signing or modifying the ledger. Presence of a child as the current tail or of a record with the same identifier is not independent historical proof of the exact operation. No observation automatically clears write quarantine. Existing hybrid queue persistence is bound to a separate unlocked local encrypted-vault session and explicit local device consent; do not adopt it for independent S-key vaults until a separate authenticated encryption-key lifecycle is designed and verified. Durable multi-device reconciliation remains unimplemented. EVM is still last priority.

## Read-only reconciliation extension (2026-11-14)
The linked JS signing transport now retains public-only unresolved transaction information across lock/reopen within the same loaded runtime, and the UI fails closed if it finds it. The marker does not persist across full reloads or other hosts. Historical evidence is now separated from current account/record observations: a read-only adapter checks the configured HTTPS Horizon's network identity and successful receipt hash/source. Even a matching receipt must NOT be used to confirm an encrypted record, child signer state, or permit retry. Full operation-level authorization/revision proof and explicit consent-backed encrypted durable journal remain required before production.
