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
var manifestV0 = VaultChunkIndex.Seal(new VaultRecordIndex(committedRecordId, 0, 0, Array.Empty<VaultChunkDescriptor>()), key, "record-manifest"u8);
var committed0 = await committer.CommitVerifiedAsync(committedRecordId, null, manifestV0, key, "record-manifest"u8.ToArray());
Check(committed0.Generation == 0, "Unexpected initial generation");
Check((await committer.ReadManifestAsync(committedRecordId)).AsSpan().SequenceEqual(manifestV0), "Committed manifest could not be read");
var manifestV1 = VaultChunkIndex.Seal(new VaultRecordIndex(committedRecordId, 1, 0, Array.Empty<VaultChunkDescriptor>()), key, "record-manifest"u8);
var committed1 = await committer.CommitVerifiedAsync(committedRecordId, committed0, manifestV1, key, "record-manifest"u8.ToArray());
Check(committed1.Generation == 1, "Generation did not advance");
try { await committer.CommitVerifiedAsync(committedRecordId, committed0, manifestV1, key, "record-manifest"u8.ToArray()); throw new Exception("Stale manifest commit accepted"); }
catch (VaultWriteConflictException) { }
Check((await committedIndex.ReadAsync(committedRecordId)) == committed1, "Conflict overwrote record pointer");
Console.WriteLine("PASS: manifest upload, atomic publish and stale writer rejection");
// Verified commit must reject a manifest for a different record or generation.
var verifiedRecordId = Guid.NewGuid();
var verifiedIndex = new VaultRecordIndex(verifiedRecordId, 0, 0, Array.Empty<VaultChunkDescriptor>());
var verifiedEnvelope = VaultChunkIndex.Seal(verifiedIndex, key, "verified-manifest"u8.ToArray());
var verifiedPointer = await committer.CommitVerifiedAsync(verifiedRecordId, null, verifiedEnvelope, key, "verified-manifest"u8.ToArray());
Check(verifiedPointer.Generation == 0, "Verified commit generation mismatch");
var verifiedRead = await committer.ReadVerifiedAsync(verifiedRecordId, key, "verified-manifest"u8.ToArray());
Check(verifiedRead.RecordId == verifiedRecordId && verifiedRead.Generation == 0, "Verified manifest read mismatch");
try { await committer.CommitVerifiedAsync(Guid.NewGuid(), null, verifiedEnvelope, key, "verified-manifest"u8.ToArray()); throw new Exception("Foreign manifest accepted"); }
catch (CryptographicException) { }
try { await committer.CommitVerifiedAsync(verifiedRecordId, verifiedPointer, verifiedEnvelope, key, "verified-manifest"u8.ToArray()); throw new Exception("Old generation accepted"); }
catch (CryptographicException) { }
Console.WriteLine("PASS: authenticated manifest identity and generation at commit boundary");
// A valid authenticated manifest must not publish dangling or corrupted ciphertext references.
var danglingRecordId = Guid.NewGuid();
var danglingDescriptor = new VaultChunkDescriptor(0, new string('a', 64), 1, new string('b', 64));
var danglingIndex = new VaultRecordIndex(danglingRecordId, 0, 1, new[] { danglingDescriptor });
var danglingEnvelope = VaultChunkIndex.Seal(danglingIndex, key, "verified-manifest"u8);
try { await committer.CommitVerifiedAsync(danglingRecordId, null, danglingEnvelope, key, "verified-manifest"u8.ToArray()); throw new Exception("Missing chunk was published"); }
catch (FileNotFoundException) { }
Check(await committedIndex.ReadAsync(danglingRecordId) is null, "Dangling record pointer published");
var mismatchRecordId = Guid.NewGuid();
var mismatchCiphertext = VaultV2Envelope.Seal(key, "x"u8, "chunk"u8);
using var mismatchStream = new MemoryStream(mismatchCiphertext);
var mismatchId = await committedObjects.PutAsync(mismatchStream);
var mismatchDescriptor = new VaultChunkDescriptor(0, mismatchId, 1, new string('b', 64));
var mismatchEnvelope = VaultChunkIndex.Seal(new VaultRecordIndex(mismatchRecordId, 0, 1, new[] { mismatchDescriptor }), key, "verified-manifest"u8);
try { await committer.CommitVerifiedAsync(mismatchRecordId, null, mismatchEnvelope, key, "verified-manifest"u8.ToArray()); throw new Exception("Mismatched chunk digest published"); }
catch (CryptographicException) { }
Check(await committedIndex.ReadAsync(mismatchRecordId) is null, "Corrupted chunk pointer published");
Console.WriteLine("PASS: reject missing and mismatched ciphertext before record publication");
// Full encrypted record lifecycle: chunk uploads, authenticated index and CAS pointer.
var fullStore = new InMemoryVaultChunkStore();
var fullPointers = new InMemoryVaultRecordIndex();
var fullService = new VaultEncryptedRecordService(fullStore, fullPointers);
var fullRecordId = Guid.NewGuid();
var fullPlain = VaultCryptoPrimitives.NewRandomBytes(VaultChunkedRecord.ChunkSize + 71);
using var fullSource = new MemoryStream(fullPlain);
var fullPointer = await fullService.WriteAsync(fullRecordId, null, fullSource, key, "full-record"u8.ToArray());
Check(fullPointer.Generation == 0, "Full record initial generation mismatch");
using var fullStage = new MemoryStream();
await fullService.ReadAsync(fullRecordId, fullStage, key, "full-record"u8.ToArray());
Check(fullStage.ToArray().AsSpan().SequenceEqual(fullPlain), "Full encrypted record round-trip failed");
using var staleSource = new MemoryStream(fullPlain);
try { await fullService.WriteAsync(fullRecordId, null, staleSource, key, "full-record"u8.ToArray()); throw new Exception("Full record stale write accepted"); }
catch (VaultWriteConflictException) { }
using var wrongContextStage = new MemoryStream();
try { await fullService.ReadAsync(fullRecordId, wrongContextStage, key, "wrong-record"u8.ToArray()); throw new Exception("Full record wrong context accepted"); }
catch (CryptographicException) { }
Check(wrongContextStage.Length == 0, "Wrong context leaked staged plaintext");
CryptographicOperations.ZeroMemory(fullPlain);
Console.WriteLine("PASS: end-to-end encrypted chunked record service and CAS conflicts");
var fsRoot = Path.Combine(Path.GetTempPath(), "svault-ciphertext-test-" + Guid.NewGuid().ToString("N"));
try
{
    var diskStore = new FileSystemVaultChunkStore(fsRoot);
    byte[] diskCiphertext = VaultV2Envelope.Seal(key, "persistent"u8, "fs-test"u8);
    using var diskInput = new MemoryStream(diskCiphertext);
    string diskId = await diskStore.PutAsync(diskInput);
    var reopenedStore = new FileSystemVaultChunkStore(fsRoot);
    await using (var diskRead = await reopenedStore.OpenReadAsync(diskId))
    {
        using var bytes = new MemoryStream();
        await diskRead.CopyToAsync(bytes);
        Check(bytes.ToArray().AsSpan().SequenceEqual(diskCiphertext), "Filesystem ciphertext persistence failed");
    }
    using var diskDuplicate = new MemoryStream(diskCiphertext);
    Check(await reopenedStore.PutAsync(diskDuplicate) == diskId, "Filesystem deduplication failed");
    await File.WriteAllBytesAsync(Path.Combine(fsRoot, diskId), new byte[] { 1, 2, 3 });
    try { await reopenedStore.OpenReadAsync(diskId); throw new Exception("Corrupted filesystem object accepted"); }
    catch (CryptographicException) { }
    CryptographicOperations.ZeroMemory(diskCiphertext);
    Console.WriteLine("PASS: filesystem ciphertext persistence, deduplication and tamper rejection");
}
finally { if (Directory.Exists(fsRoot)) Directory.Delete(fsRoot, recursive: true); }
var persistentRoot = Path.Combine(Path.GetTempPath(), "svault-index-test-" + Guid.NewGuid().ToString("N"));
try
{
    var diskObjects = new FileSystemVaultChunkStore(Path.Combine(persistentRoot, "objects"));
    var diskIndex = new FileSystemVaultRecordIndex(Path.Combine(persistentRoot, "pointers"));
    var diskService = new VaultEncryptedRecordService(diskObjects, diskIndex);
    var diskRecord = Guid.NewGuid();
    using var firstInput = new MemoryStream("persisted record"u8.ToArray());
    var firstPointer = await diskService.WriteAsync(diskRecord, null, firstInput, key, "persisted"u8.ToArray());
    var reopenedService = new VaultEncryptedRecordService(
        new FileSystemVaultChunkStore(Path.Combine(persistentRoot, "objects")),
        new FileSystemVaultRecordIndex(Path.Combine(persistentRoot, "pointers")));
    using var recovered = new MemoryStream();
    await reopenedService.ReadAsync(diskRecord, recovered, key, "persisted"u8.ToArray());
    Check(recovered.ToArray().AsSpan().SequenceEqual("persisted record"u8), "Record failed to survive index and object reopen");
    using var updateInput = new MemoryStream("new generation"u8.ToArray());
    var secondPointer = await reopenedService.WriteAsync(diskRecord, firstPointer, updateInput, key, "persisted"u8.ToArray());
    Check(secondPointer.Generation == 1, "Persistent record generation did not advance");
    using var conflictingInput = new MemoryStream("conflict"u8.ToArray());
    try { await reopenedService.WriteAsync(diskRecord, firstPointer, conflictingInput, key, "persisted"u8.ToArray()); throw new Exception("Persistent stale writer accepted"); }
    catch (VaultWriteConflictException) { }
    Console.WriteLine("PASS: encrypted record survives service restart and rejects stale CAS updates");
}
finally { if (Directory.Exists(persistentRoot)) Directory.Delete(persistentRoot, recursive: true); }
var casRoot = Path.Combine(Path.GetTempPath(), "svault-cas-test-" + Guid.NewGuid().ToString("N"));
try
{
    var firstIndex = new FileSystemVaultRecordIndex(casRoot);
    var secondIndex = new FileSystemVaultRecordIndex(casRoot);
    var casRecord = Guid.NewGuid();
    var firstCandidate = new VaultRecordPointer(new string('a', 64), 0);
    var secondCandidate = new VaultRecordPointer(new string('b', 64), 0);
    var attempts = await Task.WhenAll(
        firstIndex.CompareExchangeAsync(casRecord, null, firstCandidate),
        secondIndex.CompareExchangeAsync(casRecord, null, secondCandidate));
    Check(attempts.Count(won => won) == 1, "Independent filesystem index instances both won CAS");
    var winner = await firstIndex.ReadAsync(casRecord);
    Check(winner == firstCandidate || winner == secondCandidate, "Filesystem CAS winner not persisted");
    Console.WriteLine("PASS: filesystem CAS serialized across independent index instances");
}
finally { if (Directory.Exists(casRoot)) Directory.Delete(casRoot, recursive: true); }
// Simulate a crash after a temporary pointer file was written but before atomic publication.
var recoveryRoot = Path.Combine(Path.GetTempPath(), "svault-crash-test-" + Guid.NewGuid().ToString("N"));
try
{
    var recoveryIndex = new FileSystemVaultRecordIndex(recoveryRoot);
    var recoveryId = Guid.NewGuid();
    var recoveryPointer = new VaultRecordPointer(new string('c', 64), 0);
    Check(await recoveryIndex.CompareExchangeAsync(recoveryId, null, recoveryPointer), "Initial crash-recovery pointer failed");
    await File.WriteAllTextAsync(Path.Combine(recoveryRoot, ".pending-abandoned"), "{incomplete");
    var recoveredIndex = new FileSystemVaultRecordIndex(recoveryRoot);
    Check(await recoveredIndex.ReadAsync(recoveryId) == recoveryPointer, "Interrupted temporary file changed committed pointer");
    var nextRecovery = new VaultRecordPointer(new string('d', 64), 1);
    Check(await recoveredIndex.CompareExchangeAsync(recoveryId, recoveryPointer, nextRecovery), "Recovery after abandoned temporary file failed");
    Check(await new FileSystemVaultRecordIndex(recoveryRoot).ReadAsync(recoveryId) == nextRecovery, "Recovered pointer not durable across reopen");
    await File.WriteAllTextAsync(Path.Combine(recoveryRoot, recoveryId.ToString("N") + ".json"), "{incomplete");
    try { await recoveredIndex.ReadAsync(recoveryId); throw new Exception("Corrupted committed pointer accepted"); }
    catch (System.Text.Json.JsonException) { }
    Console.WriteLine("PASS: abandoned temporary pointer recovery and corrupted committed pointer rejection");
}
finally { if (Directory.Exists(recoveryRoot)) Directory.Delete(recoveryRoot, recursive: true); }
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
