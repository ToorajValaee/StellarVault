# Addressable encrypted record indexes

The new `IVaultBlobStore` interface provides opaque ciphertext Put/Get/Delete operations. `VaultRecordIndex` identifies the logical record, generation, ordered chunks, lengths, storage keys and SHA-256 hashes. `VaultChunkIndex.Seal/Open` authenticates the serialized index with the v2 envelope.

This is **not yet a transactional database**. A storage repository must atomically publish the index only after ciphertext chunks are durable, validate each fetched ciphertext against its hash, bind the chunk encryption context to record identity/generation/index, reject stale revisions, and garbage-collect unreachable objects. Object keys and hashes are not authorization credentials. The current index is not yet connected to Stellar transactions or to existing production storage.
