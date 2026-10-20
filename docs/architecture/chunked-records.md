# Chunked StellarVault records (experimental)

StellarVault is a secure data layer built around Stellar accounts, **not merely a seed-phrase wallet**. Logical records may contain structured data and attachments. Stellar ledger transactions should carry references, hashes, permissions or commitments as appropriate; large ciphertext is intended for a separate storage transport, not on-ledger transaction payloads.

## Initial binary record container

The experimental `VaultChunkedRecord` API streams input in **256 KiB chunks**. Each chunk has its own fresh random AES-256-GCM nonce and authentication tag using the v2 envelope. Context binds each chunk to the record UUID, generation and chunk index. A final authenticated manifest contains SHA-256 hashes of the ordered encrypted chunks and authenticates the complete header, count and plaintext length. The record has a 4,096-chunk implementation cap (~1 GiB), not a 1 MiB record cap; limits will be revisited for database pagination and large attachments.

Decrypt writes to **private, seekable staging storage** and truncates it on failure. The caller MUST NOT publish or interpret staged plaintext before the entire manifest verifies. Prefer an isolated temporary file with controlled permissions and deletion on failure. Plaintext staging on disk is not acceptable for real wallet secrets without encryption or OS-backed protection.

## Atomic record pointer prototype

`InMemoryVaultRecordIndex` implements the compare-and-swap record pointer contract for development and tests. An initial record uses generation 0; each successful update advances by one. Stale writers are rejected rather than silently overwriting another update. This implementation is **volatile**, so it must not be used as a persistent index, authorization source, or rollback protection mechanism.

## Content-addressed storage prototype

`InMemoryVaultChunkStore` now provides immutable SHA-256-addressed encrypted objects, bounded writes, duplicate-object reuse, digest verification on reads, and deletion. This is an **ephemeral testing backend**, not durable or remotely accessible storage. Deduplication only applies to byte-identical ciphertext; independently encrypted plaintext uses random nonces and therefore normally produces different identifiers. A persistent backend must provide durable writes, atomic pointer updates, tenant isolation, integrity verification, garbage collection and crash recovery.

## Publish-after-upload transaction prototype

`VaultRecordCommitter` uploads an already encrypted/authenticated manifest into `IVaultChunkStore`, then atomically advances the pointer via `IVaultRecordIndex.CompareExchangeAsync`. Stale writers receive `VaultWriteConflictException`. Failed CAS may leave an orphaned encrypted manifest: garbage collection must use a reachability scan and a grace period, **never immediately delete an object that could be shared**. The preferred `CommitVerifiedAsync` and `ReadVerifiedAsync` paths authenticate the index and check record identity and generation against the pointer. The unauthenticated `CommitAsync` entry point is now private, so external callers must use verified commits. Account authorization, durable rollback resistance, and complete chunk-reference validation remain outside this coordinator. A production implementation must combine verification and authorization within the trusted transaction boundary, and must have a durable atomic index.

Verified reads now use a single record-pointer snapshot for both object retrieval and generation checking, avoiding a race where an intervening update could cause a false identity mismatch. This is snapshot consistency for one manifest read, **not** a cross-record transaction or trusted rollback anchor.

Verified commits now preflight each referenced encrypted chunk by reading its bytes, checking its bounded envelope length and SHA-256 digest, and refusing publication when a reference is missing or corrupted. This does **not** decrypt each chunk or prove its AES-GCM key/context matches the manifest; a production writer must encrypt and bind chunks consistently. Non-transactional stores may change between preflight and later reads; durable immutable objects and authorization remain required.

## Unified encrypted record service

`VaultEncryptedRecordService` provides a development-stage end-to-end path: streaming plaintext into individually encrypted 256 KiB objects, storing those objects by ciphertext SHA-256, sealing an authenticated index, verifying all referenced objects, and atomically publishing the manifest pointer. Reads authenticate the manifest and every chunk before returning successfully; partial plaintext is rolled back from private staging on failure. The staging stream **must remain private until success**, and should not be an unencrypted disk file for secrets. The implementation currently rewrites all chunks for updates and has no persistent backend, account authorization, garbage collection, or trusted rollback anchor.

## Persistent ciphertext objects

`FileSystemVaultChunkStore` implements the existing encrypted-object interface with temporary-file writes, content-addressed filenames, deduplication, size limits and SHA-256 verification when reopening objects. It can retain ciphertext across process restarts. This **does not** make record pointers persistent: `InMemoryVaultRecordIndex` is still ephemeral. The directory needs platform-appropriate permissions, backups and durability policy. It is not a secure enclave, transactional database, or defense against malicious filesystem rollback.

## Persistent record pointers

`FileSystemVaultRecordIndex` stores each record pointer in an atomically replaced JSON file and enforces sequential generations and compare-and-swap under a process-local semaphore. Combined with `FileSystemVaultChunkStore`, the unified encrypted-record service can reopen an existing record after a process restart. **Do not use multiple writer processes** against the same directory: the semaphore is not a cross-process lock, so concurrent writers can lose updates. The pointer file is not independently authenticated or rollback-resistant; a trusted monotonic anchor and crash/power-loss durability testing are still required.

## Next steps before production

- Split manifest, record index and ciphertext blobs into separately addressable storage objects to support random reads, updates and deduplication policies.
- Add a storage provider interface for browser IndexedDB, Android, desktop and optional remote content-addressed backends; never put large blobs directly into Stellar ledger entries.
- Use per-record encryption keys, derived via a reviewed KDF/key hierarchy, and authenticated owner/account binding.
- Provide atomic commit, crash recovery, stale-generation/rollback protection and garbage collection.
- Build large-file streaming tests, cross-platform tests and security review. The current code is **not integrated with production vault storage**.
