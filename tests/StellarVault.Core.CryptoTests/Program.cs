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
CryptographicOperations.ZeroMemory(key);
CryptographicOperations.ZeroMemory(wrongKey);
CryptographicOperations.ZeroMemory(plaintext);
CryptographicOperations.ZeroMemory(decrypted);
Console.WriteLine("PASS: AES-256-GCM round-trip, tampering, authentication, truncation and randomness checks");
