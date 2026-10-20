# Chunked StellarVault records (experimental)

StellarVault is a secure data layer built around Stellar accounts, **not merely a seed-phrase wallet**. Logical records may contain structured data and attachments. Stellar ledger transactions should carry references, hashes, permissions or commitments as appropriate; large ciphertext is intended for a separate storage transport, not on-ledger transaction payloads.

## Initial binary record container

The experimental `VaultChunkedRecord` API streams input in **256 KiB chunks**. Each chunk has its own fresh random AES-256-GCM nonce and authentication tag using the v2 envelope. Context binds each chunk to the record UUID, generation and chunk index. A final authenticated manifest contains SHA-256 hashes of the ordered encrypted chunks and authenticates the complete header, count and plaintext length. The record has a 4,096-chunk implementation cap (~1 GiB), not a 1 MiB record cap; limits will be revisited for database pagination and large attachments.

Decrypt writes to **private, seekable staging storage** and truncates it on failure. The caller MUST NOT publish or interpret staged plaintext before the entire manifest verifies. Prefer an isolated temporary file with controlled permissions and deletion on failure. Plaintext staging on disk is not acceptable for real wallet secrets without encryption or OS-backed protection.

## Atomic record pointer prototype

`InMemoryVaultRecordIndex` implements the compare-and-swap record pointer contract for development and tests. An initial record uses generation 0; each successful update advances by one. Stale writers are rejected rather than silently overwriting another update. This implementation is **volatile**, so it must not be used as a persistent index, authorization source, or rollback protection mechanism.

## Next steps before production

- Split manifest, record index and ciphertext blobs into separately addressable storage objects to support random reads, updates and deduplication policies.
- Add a storage provider interface for browser IndexedDB, Android, desktop and optional remote content-addressed backends; never put large blobs directly into Stellar ledger entries.
- Use per-record encryption keys, derived via a reviewed KDF/key hierarchy, and authenticated owner/account binding.
- Provide atomic commit, crash recovery, stale-generation/rollback protection and garbage collection.
- Build large-file streaming tests, cross-platform tests and security review. The current code is **not integrated with production vault storage**.
