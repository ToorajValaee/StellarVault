using System.Security.Cryptography;
using System.Text;
using StellarVault.Core.Services;
using StellarVault.Core.Abstractions;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var key = VaultCryptoPrimitives.NewRandomBytes(32);
var nonce = VaultCryptoPrimitives.NewRandomBytes(12);
var plaintext = Encoding.UTF8.GetBytes("StellarVault test secret");
var aad = Encoding.UTF8.GetBytes("svault:v2:metadata");
var sealedEnvelope = VaultCryptoPrimitives.Seal(key, plaintext, aad);
Check(sealedEnvelope.Length == plaintext.Length + 28, "Invalid sealed envelope size");
Check(VaultCryptoPrimitives.Open(key, sealedEnvelope, aad).AsSpan().SequenceEqual(plaintext), "Envelope round-trip failed");
var sealedEnvelope2 = VaultCryptoPrimitives.Seal(key, plaintext, aad);
Check(!sealedEnvelope.AsSpan().SequenceEqual(sealedEnvelope2), "Nonce was reused");
var modifiedEnvelope = (byte[])sealedEnvelope.Clone();
modifiedEnvelope[0] ^= 1;
try { VaultCryptoPrimitives.Open(key, modifiedEnvelope, aad); throw new Exception("Modified envelope nonce accepted"); }
catch (CryptographicException) { }
var v2 = VaultV2Envelope.Seal(key, plaintext, aad);
Check(v2.Length == plaintext.Length + VaultV2Envelope.MinimumLength, "Unexpected v2 envelope length");
Check(VaultV2Envelope.Open(key, v2, aad).AsSpan().SequenceEqual(plaintext), "v2 round-trip failed");
var wrongVersion = (byte[])v2.Clone();
wrongVersion[4] = 3;
MustReject(() => VaultV2Envelope.Open(key, wrongVersion, aad), "Unsupported version accepted");
var wrongMagic = (byte[])v2.Clone();
wrongMagic[0] ^= 1;
MustReject(() => VaultV2Envelope.Open(key, wrongMagic, aad), "Incorrect magic accepted");
var tamperedV2 = (byte[])v2.Clone();
tamperedV2[^1] ^= 1;
MustReject(() => VaultV2Envelope.Open(key, tamperedV2, aad), "v2 tampering accepted");
MustReject(() => VaultV2Envelope.Open(key, v2, "other-context"u8), "v2 context mismatch accepted");
MustReject(() => VaultV2Envelope.Open(key, v2.AsSpan(0, 10), aad), "Truncated v2 envelope accepted");
static void MustRejectArgument(Action operation, string message)
{
    try { operation(); }
    catch (ArgumentOutOfRangeException) { return; }
    throw new Exception(message);
}
MustRejectArgument(() => VaultV2Envelope.Seal(key, new byte[VaultV2Envelope.MaximumPlaintextLength + 1], aad), "Oversized plaintext accepted");
MustRejectArgument(() => VaultV2Envelope.Seal(key, plaintext, new byte[VaultV2Envelope.MaximumContextLength + 1]), "Oversized context accepted");
MustReject(() => VaultV2Envelope.Open(key, new byte[VaultV2Envelope.MaximumLength + 1], aad), "Oversized envelope accepted");
var emptyEnvelope = VaultV2Envelope.Seal(key, ReadOnlySpan<byte>.Empty, aad);
Check(VaultV2Envelope.Open(key, emptyEnvelope, aad).Length == 0, "Empty plaintext round-trip failed");
var encrypted = VaultCryptoPrimitives.Encrypt(key, plaintext, aad, nonce);
Check(encrypted.Length == plaintext.Length + 16, "Unexpected ciphertext size");
Check(!encrypted.AsSpan(0, plaintext.Length).SequenceEqual(plaintext), "Plaintext leaked");
var decrypted = VaultCryptoPrimitives.Decrypt(key, encrypted, aad, nonce);
Check(decrypted.AsSpan().SequenceEqual(plaintext), "Round-trip failed");

static void MustReject(Action operation, string message)
{
    try { operation(); }
    catch (CryptographicException) { return; }
    throw new Exception(message);
}

var modifiedCiphertext = (byte[])encrypted.Clone();
modifiedCiphertext[0] ^= 1;
MustReject(() => VaultCryptoPrimitives.Decrypt(key, modifiedCiphertext, aad, nonce), "Ciphertext tampering accepted");
var modifiedTag = (byte[])encrypted.Clone();
modifiedTag[^1] ^= 1;
MustReject(() => VaultCryptoPrimitives.Decrypt(key, modifiedTag, aad, nonce), "Tag tampering accepted");
MustReject(() => VaultCryptoPrimitives.Decrypt(key, encrypted, Encoding.UTF8.GetBytes("different"), nonce), "Wrong associated data accepted");
var wrongKey = VaultCryptoPrimitives.NewRandomBytes(32);
MustReject(() => VaultCryptoPrimitives.Decrypt(wrongKey, encrypted, aad, nonce), "Wrong key accepted");
var wrongNonce = VaultCryptoPrimitives.NewRandomBytes(12);
MustReject(() => VaultCryptoPrimitives.Decrypt(key, encrypted, aad, wrongNonce), "Wrong nonce accepted");
MustReject(() => VaultCryptoPrimitives.Decrypt(key, new byte[15], aad, nonce), "Truncated ciphertext accepted");

var randomA = VaultCryptoPrimitives.NewRandomBytes(32);
var randomB = VaultCryptoPrimitives.NewRandomBytes(32);
Check(!randomA.AsSpan().SequenceEqual(randomB), "Random generator returned identical samples");
CryptographicOperations.ZeroMemory(plaintext);
CryptographicOperations.ZeroMemory(decrypted);
Console.WriteLine("PASS: AES-256-GCM round-trip, tampering, authentication, truncation and randomness checks");

// Chunked records span multiple authenticated 256 KiB encryption operations.
var recordId = Guid.NewGuid();
var bigPlain = VaultCryptoPrimitives.NewRandomBytes(VaultChunkedRecord.ChunkSize * 2 + 37);
using var source = new MemoryStream(bigPlain);
using var encodedRecord = new MemoryStream();
await VaultChunkedRecord.EncryptAsync(source, encodedRecord, key, recordId, 7);
encodedRecord.Position = 0;
using var staged = new MemoryStream();
await VaultChunkedRecord.DecryptAsync(encodedRecord, staged, key, recordId, 7);
Check(staged.ToArray().AsSpan().SequenceEqual(bigPlain), "Chunked record round-trip failed");
encodedRecord.Position = 0;
using var wrongGenerationStage = new MemoryStream();
try { await VaultChunkedRecord.DecryptAsync(encodedRecord, wrongGenerationStage, key, recordId, 8); throw new Exception("Wrong generation accepted"); }
catch (CryptographicException) { }
Check(wrongGenerationStage.Length == 0, "Failed decryption left staged data");
var corruptedRecord = encodedRecord.ToArray();
corruptedRecord[^1] ^= 1;
using var corruptedInput = new MemoryStream(corruptedRecord);
using var corruptedStage = new MemoryStream();
try { await VaultChunkedRecord.DecryptAsync(corruptedInput, corruptedStage, key, recordId, 7); throw new Exception("Corrupt manifest accepted"); }
catch (CryptographicException) { }
Check(corruptedStage.Length == 0, "Manifest failure did not roll back staging");
CryptographicOperations.ZeroMemory(bigPlain);
Console.WriteLine("PASS: chunked authenticated records and rollback-on-failure staging");

// Independent ciphertext objects: storage sees no plaintext and manifest controls ordering.
var objectMap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
var objectStore = new TestChunkStore(objectMap);
var objectRecordId = Guid.NewGuid();
var objectPlain = VaultCryptoPrimitives.NewRandomBytes(VaultChunkObjectStore.ChunkSize + 99);
using var objectSource = new MemoryStream(objectPlain);
var encryptedIndex = await VaultChunkObjectStore.WriteAsync(objectSource, objectStore, key, objectRecordId, 12);
Check(objectMap.Count == 2, "Expected two independently stored chunks");
using var objectStage = new MemoryStream();
await VaultChunkObjectStore.ReadAsync(encryptedIndex, objectStore, objectStage, key, objectRecordId, 12);
Check(objectStage.ToArray().AsSpan().SequenceEqual(objectPlain), "Content-addressed round-trip failed");
var firstId = objectMap.Keys.First();
objectMap[firstId][^1] ^= 1;
using var failedStage = new MemoryStream();
try { await VaultChunkObjectStore.ReadAsync(encryptedIndex, objectStore, failedStage, key, objectRecordId, 12); throw new Exception("Tampered chunk accepted"); }
catch (CryptographicException) { }
Check(failedStage.Length == 0, "Failed object verification left staged data");
CryptographicOperations.ZeroMemory(objectPlain);

Console.WriteLine("PASS: content-addressed chunk objects and authenticated manifest");


var blob = VaultCryptoPrimitives.Seal(key, "indexed chunk"u8, "record-1:chunk-0"u8);
var descriptor = new VaultChunkDescriptor(0, "objects/record-1/0", 13, Convert.ToHexString(SHA256.HashData(blob)));
var index = new VaultRecordIndex(Guid.NewGuid(), 1, 13, new[] { descriptor });
var sealedIndex = VaultChunkIndex.Seal(index, key, "vault-index"u8);
var restoredIndex = VaultChunkIndex.Open(sealedIndex, key, "vault-index"u8);
Check(restoredIndex.RecordId == index.RecordId && restoredIndex.Chunks.Count == 1, "Index round-trip failed");
Check(VaultChunkIndex.VerifyCiphertext(restoredIndex.Chunks[0], blob), "Chunk digest verification failed");
var corruptedBlob = (byte[])blob.Clone();
corruptedBlob[^1] ^= 1;
Check(!VaultChunkIndex.VerifyCiphertext(descriptor, corruptedBlob), "Tampered blob passed digest verification");
MustReject(() => VaultChunkIndex.Open(sealedIndex, key, "wrong-index"u8), "Wrong index context accepted");
MustReject(() => VaultChunkIndex.Validate(new VaultRecordIndex(index.RecordId, 1, 13, new[] { descriptor with { Index = 1 } })), "Out-of-order descriptor accepted");
MustReject(() => VaultChunkIndex.Validate(new VaultRecordIndex(index.RecordId, 1, 13, new[] { descriptor with { Sha256Hex = "BAD" } })), "Malformed digest accepted");
Console.WriteLine("PASS: authenticated record index, descriptor bounds and ciphertext hash validation");

// Empty records are valid and authenticate the empty manifest.
using var emptyInput = new MemoryStream();
using var emptyEncoded = new MemoryStream();
await VaultChunkedRecord.EncryptAsync(emptyInput, emptyEncoded, key, recordId, 9);
emptyEncoded.Position = 0;
using var emptyStage = new MemoryStream();
await VaultChunkedRecord.DecryptAsync(emptyEncoded, emptyStage, key, recordId, 9);
Check(emptyStage.Length == 0, "Empty record did not round-trip");
// Truncated encrypted records must never leave published plaintext in staging.
var truncatedRecord = encodedRecord.ToArray()[..^5];
using var truncatedInput = new MemoryStream(truncatedRecord);
using var truncatedStage = new MemoryStream();
try { await VaultChunkedRecord.DecryptAsync(truncatedInput, truncatedStage, key, recordId, 7); throw new Exception("Truncated record accepted"); }
catch (EndOfStreamException) { }
Check(truncatedStage.Length == 0, "Truncation did not roll back staging");
Console.WriteLine("PASS: empty records and truncated record staging rollback");
// Atomic record pointer updates must reject stale writers.
var pointerIndex = new InMemoryVaultRecordIndex();
var pointerRecordId = Guid.NewGuid();
Check(await pointerIndex.ReadAsync(pointerRecordId) is null, "New record already exists");
var pointer0 = new VaultRecordPointer("manifest-v0", 0);
var pointer1 = new VaultRecordPointer("manifest-v1", 1);
Check(await pointerIndex.CompareExchangeAsync(pointerRecordId, null, pointer0), "Initial commit failed");
Check(!await pointerIndex.CompareExchangeAsync(pointerRecordId, null, pointer0), "Duplicate initial commit accepted");
Check(await pointerIndex.CompareExchangeAsync(pointerRecordId, pointer0, pointer1), "Update commit failed");
Check(!await pointerIndex.CompareExchangeAsync(pointerRecordId, pointer0, new VaultRecordPointer("stale", 1)), "Stale writer committed");
Check(await pointerIndex.ReadAsync(pointerRecordId) == pointer1, "Lost update in pointer store");
Console.WriteLine("PASS: atomic record pointers reject conflicting writers");
// Immutable content-addressed object store returns stable identifiers and isolated readers.
var contentStore = new InMemoryVaultChunkStore();
var objectCiphertext = VaultV2Envelope.Seal(key, "independent chunk"u8, "test:object"u8);
using var objectCiphertextInput = new MemoryStream(objectCiphertext);
var contentId = await contentStore.PutAsync(objectCiphertextInput);
Check(contentId == Convert.ToHexString(SHA256.HashData(objectCiphertext)).ToLowerInvariant(), "Unexpected content identifier");
using var duplicateCiphertextInput = new MemoryStream(objectCiphertext);
Check(await contentStore.PutAsync(duplicateCiphertextInput) == contentId, "Duplicate ciphertext changed identifier");
await using (var contentReader = await contentStore.OpenReadAsync(contentId))
{
    using var retrieved = new MemoryStream();
    await contentReader.CopyToAsync(retrieved);
    Check(retrieved.ToArray().AsSpan().SequenceEqual(objectCiphertext), "Stored ciphertext mismatch");
}
await contentStore.DeleteAsync(contentId);
try { await contentStore.OpenReadAsync(contentId); throw new Exception("Deleted ciphertext remained available"); }
catch (FileNotFoundException) { }
Console.WriteLine("PASS: content-addressed encrypted object storage");
// Publish encrypted manifests only after storage upload and atomic pointer update.
var committedObjects = new InMemoryVaultChunkStore();
var committedIndex = new InMemoryVaultRecordIndex();
var committer = new VaultRecordCommitter(committedObjects, committedIndex);
var committedRecordId = Guid.NewGuid();
var manifestV0 = VaultV2Envelope.Seal(key, "manifest-zero"u8, "record-manifest"u8);
var committed0 = await committer.CommitAsync(committedRecordId, null, manifestV0);
Check(committed0.Generation == 0, "Unexpected initial generation");
Check((await committer.ReadManifestAsync(committedRecordId)).AsSpan().SequenceEqual(manifestV0), "Committed manifest could not be read");
var manifestV1 = VaultV2Envelope.Seal(key, "manifest-one"u8, "record-manifest"u8);
var committed1 = await committer.CommitAsync(committedRecordId, committed0, manifestV1);
Check(committed1.Generation == 1, "Generation did not advance");
try { await committer.CommitAsync(committedRecordId, committed0, manifestV0); throw new Exception("Stale manifest commit accepted"); }
catch (VaultWriteConflictException) { }
Check((await committedIndex.ReadAsync(committedRecordId)) == committed1, "Conflict overwrote record pointer");
Console.WriteLine("PASS: manifest upload, atomic publish and stale writer rejection");
// Verified commit must reject a manifest for a different record or generation.
var verifiedRecordId = Guid.NewGuid();
var verifiedIndex = new VaultRecordIndex(verifiedRecordId, 0, 0, Array.Empty<VaultChunkDescriptor>());
var verifiedEnvelope = VaultChunkIndex.Seal(verifiedIndex, key, "verified-manifest"u8);
var verifiedPointer = await committer.CommitVerifiedAsync(verifiedRecordId, null, verifiedEnvelope, key, "verified-manifest"u8);
Check(verifiedPointer.Generation == 0, "Verified commit generation mismatch");
var verifiedRead = await committer.ReadVerifiedAsync(verifiedRecordId, key, "verified-manifest"u8);
Check(verifiedRead.RecordId == verifiedRecordId && verifiedRead.Generation == 0, "Verified manifest read mismatch");
try { await committer.CommitVerifiedAsync(Guid.NewGuid(), null, verifiedEnvelope, key, "verified-manifest"u8); throw new Exception("Foreign manifest accepted"); }
catch (CryptographicException) { }
try { await committer.CommitVerifiedAsync(verifiedRecordId, verifiedPointer, verifiedEnvelope, key, "verified-manifest"u8); throw new Exception("Old generation accepted"); }
catch (CryptographicException) { }
Console.WriteLine("PASS: authenticated manifest identity and generation at commit boundary");
CryptographicOperations.ZeroMemory(key);
CryptographicOperations.ZeroMemory(wrongKey);

sealed class TestChunkStore(Dictionary<string, byte[]> objects) : IVaultChunkObjectStore
{
    public Task PutAsync(string objectId, ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken = default)
    {
        objects[objectId] = ciphertext.ToArray();
        return Task.CompletedTask;
    }
    public Task<byte[]> GetAsync(string objectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(objects.TryGetValue(objectId, out var bytes) ? (byte[])bytes.Clone() : throw new FileNotFoundException(objectId));
}
