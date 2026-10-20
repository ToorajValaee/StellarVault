using System.Security.Cryptography;
using System.Text;
using StellarVault.Core.Services;

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
CryptographicOperations.ZeroMemory(key);
CryptographicOperations.ZeroMemory(wrongKey);
Console.WriteLine("PASS: content-addressed chunk objects and authenticated manifest");

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
