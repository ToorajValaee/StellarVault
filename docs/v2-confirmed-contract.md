# SVault v2 — confirmed implementation contract

## Identity and funding

- Import an **existing** BIP-39 mnemonic, with optional BIP-39 passphrase.
- Derive Stellar Ed25519 accounts using SLIP-0010 paths `m/44'/148'/i'`.
- Index 0 MUST already exist on the selected Stellar network and have sufficient XLM. Never silently create or fund it.
- Index 0 is the transaction source and pays **all** transaction fees.
- For every new index >= 1, the root funds account creation and required reserves. Reserves belong to each child account; transaction fees are charged to the transaction source.
- Before any submission, load current balances, base reserve, ledger sequence and fee requirements from Horizon. Include minimum balance and sponsorship/subentry implications; do not rely on stale estimates.
- For child operations, require the child's signature or a deliberately configured delegated signer. Do not assume that the fee payer's signature authorizes child operations.
- Root registry updates and child account creation must be reconciled after timeouts or ambiguous transaction outcomes. Never advance indexes solely from local memory.

## Registry and recovery

- Registry lives in a ManageData entry on index 0, protected by a 32-byte HMAC.
- Never write an unauthenticated JSON registry to ManageData.
- The compact 18-byte .NET codec is an **internal payload codec**, not a valid on-chain authenticated envelope by itself.
- Persisted registry revisions alone cannot prevent rollback: reconcile ledger state and account existence, and document residual rollback threat.
- Scan deterministic indexes through the registry high-water mark and a bounded recovery window, even across missing indexes.
- Detect simultaneous writers by reloading root sequence and registry immediately before submission and reconciling failed/conflicting submissions.

## Encrypted folders

- Each folder has a stable opaque ID, display name, optional parent ID, creation time and update time.
- Folder names and parent IDs are encrypted before being placed on-chain; no plaintext folder metadata in ManageData keys.
- Records reference a folder ID inside their encrypted record payload. Missing/null folder ID means the root section.
- Support create, rename, move, nested folders, deletion with confirmation, and moving records between folders.
- Empty folders must persist. Deletion of nonempty folders requires an explicit policy (move children to root, recursive delete, or block); default to block.
- Folders are logical objects, **not** Stellar accounts. Actual count is constrained by blockchain capacity, fees, reserves, and app safety limits.

## Release gate

Do not merge to main or call this feature ready for testing until:
1. BIP-39 wordlist/checksum validation and known-vector derivation tests pass.
2. The existing secret-key-only unlock UI has been replaced with mnemonic import.
3. JS modules are loaded and connected to the C# session and store.
4. The existing sv:next/random-child path has been replaced with indexed discovery and creation.
5. Root-paid fees and reserve calculations work on Stellar testnet.
6. Authenticated registry creation, updates, and recovery pass interruption tests.
7. Folder CRUD and record moves persist encrypted across logout and recovery.
8. Automated JS and .NET tests and a full Blazor build pass.
