# Stellar account encrypted database (implementation)

`LocalStellarVault(rootDirectory).ForAccount(stellarGAddress)` composes persistent ciphertext objects, persistent compare-and-swap pointers, and a named-record API. The application must use a private storage directory, protect the 32-byte vault key, and authenticate/authorize the account separately. This API does **not** sign Stellar transactions or verify account ownership.

```csharp
var db = new LocalStellarVault(privateDirectory).ForAccount(stellarGAddress);
var version = await db.GetVersionAsync("profile/settings", vaultKey);
await using var input = File.OpenRead(sourcePath);
var committed = await db.PutAsync("profile/settings", version, input, vaultKey);
// For reads, use private in-memory staging and publish only after GetAsync returns.
using var staging = new MemoryStream();
await db.GetAsync("profile/settings", staging, vaultKey);
```

Names are mapped to deterministic, keyed 128-bit record IDs using HMAC-SHA-256 with a versioned domain and the canonical account address. Manifest encryption authenticates account and record identity. The same name in different accounts maps to different IDs. The 32-byte key must be random and must **not** be a raw password; Argon2id, platform key storage, account ownership checks, encrypted catalog/listing, deletion, quota management, and durable rollback protection remain pending.

Writes use optimistic concurrency: pass the pointer returned by `GetVersionAsync` or a previous `PutAsync`; `null` creates a new record. Concurrent changes throw `VaultWriteConflictException`. Writes stream up to 4096 chunks of 256 KiB each; updates currently rewrite the complete record. Reading into a disk file exposes plaintext on disk, so callers should prefer private memory or a separately encrypted staging mechanism. This is a storage API, not yet a production-ready wallet/vault unlock flow.

## Encrypted record catalog

`db.Catalog.ListAsync(vaultKey)` lists active named records, including content-type metadata and update timestamps. `db.PutIndexedAsync(name, expectedVersion, plaintext, vaultKey, contentType)` writes the encrypted record and then registers its name in the encrypted catalog. `db.Catalog.TombstoneAsync(name, vaultKey)` marks a catalog entry deleted without erasing stored ciphertext; this is a logical deletion only. Catalog metadata, including names, is encrypted in a reserved system record. The catalog is limited to 2048 entries and uses optimistic compare-and-swap with bounded retries.

**Consistency boundary:** data and catalog commits are separate. If catalog registration fails after a successful record write, the record remains retrievable by its name but may not appear in listings; callers must retry `Catalog.RegisterAsync`. A tombstone does not prevent direct reads or securely erase historical chunks. Cross-record atomic transactions, garbage collection, and authenticated catalog-to-record reconciliation are not yet implemented. This is an implementation milestone, not a production-ready transactional database.
