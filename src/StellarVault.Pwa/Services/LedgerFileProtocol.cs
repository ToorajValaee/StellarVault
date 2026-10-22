using System.Security.Cryptography;
using System.Text.Json;
using StellarVault.Core.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// File payload protocol for the existing HD ledger record format.
/// SVFILE1 is a single record. SVFILE2 separates an authenticated manifest from
/// ordered, separately authenticated record chunks. A manifest is published last.
/// Readers must reject incomplete, duplicated or reordered chunks.
/// </summary>
public static class LedgerFileProtocol
{
    public const string SinglePrefix = "SVFILE1:";
    public const string ManifestPrefix = "SVFILE2:";
    public const string ChunkPrefix = "SVCHUNK2:";
    public const int MaxChunkBytes = 2048;
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxParts = (MaxFileBytes + MaxChunkBytes - 1) / MaxChunkBytes;

    public sealed record Single(string FileName, string ContentType, string DataBase64, string? Sha256Hex = null);
    public sealed record Manifest(string FileName, string ContentType, long Length, string Sha256Hex, string[] PartIds);
    public sealed record Part(string FileId, int Index, string DataBase64, string Sha256Hex);

    public static bool IsFile(LedgerRecordItem item) =>
        item.Content.StartsWith(SinglePrefix, StringComparison.Ordinal) ||
        item.Content.StartsWith(ManifestPrefix, StringComparison.Ordinal);

    public static string EncodeSingle(string name, string contentType, ReadOnlySpan<byte> bytes) =>
        SinglePrefix + JsonSerializer.Serialize(new Single(name, contentType,
            Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes))));

    public static byte[] DecodeFile(LedgerRecordItem item, IReadOnlyList<LedgerRecordItem> all)
    {
        if (item.Content.StartsWith(SinglePrefix, StringComparison.Ordinal))
        {
            var one = JsonSerializer.Deserialize<Single>(item.Content[SinglePrefix.Length..])
                ?? throw new InvalidDataException("Missing file metadata.");
            ValidateName(one.FileName);
            var bytes = Convert.FromBase64String(one.DataBase64);
            try
            {
                if (bytes.Length > MaxChunkBytes)
                    throw new InvalidDataException("Legacy file exceeds supported bound.");
                Verify(bytes, one.Sha256Hex);
                return bytes;
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        if (!item.Content.StartsWith(ManifestPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Not a recognized file record.");

        var manifest = JsonSerializer.Deserialize<Manifest>(item.Content[ManifestPrefix.Length..])
            ?? throw new InvalidDataException("Missing file manifest.");
        ValidateName(manifest.FileName);
        if (manifest.Length < 0 || manifest.Length > MaxFileBytes ||
            manifest.PartIds is null || manifest.PartIds.Length > MaxParts ||
            manifest.PartIds.Length != (manifest.Length + MaxChunkBytes - 1) / MaxChunkBytes ||
            manifest.PartIds.Distinct(StringComparer.Ordinal).Count() != manifest.PartIds.Length)
            throw new InvalidDataException("Invalid file manifest bounds or part references.");

        byte[] file = new byte[checked((int)manifest.Length)];
        try
        {
            int offset = 0;
            for (int i = 0; i < manifest.PartIds.Length; i++)
            {
                var id = manifest.PartIds[i];
                var record = all.SingleOrDefault(r => r.Id == id)
                    ?? throw new InvalidDataException("File is incomplete; a ledger part is missing.");
                if (!record.Content.StartsWith(ChunkPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException("A referenced ledger part has the wrong type.");
                var part = JsonSerializer.Deserialize<Part>(record.Content[ChunkPrefix.Length..])
                    ?? throw new InvalidDataException("Invalid file part.");
                if (part.FileId != item.Id || part.Index != i)
                    throw new InvalidDataException("Ledger file part association or ordering mismatch.");
                byte[] segment = Convert.FromBase64String(part.DataBase64);
                try
                {
                    int expected = (int)Math.Min(MaxChunkBytes, manifest.Length - offset);
                    if (segment.Length != expected)
                        throw new InvalidDataException("Incorrect file part length.");
                    Verify(segment, part.Sha256Hex);
                    segment.CopyTo(file.AsSpan(offset));
                    offset += segment.Length;
                }
                finally { CryptographicOperations.ZeroMemory(segment); }
            }
            Verify(file, manifest.Sha256Hex);
            return file;
        }
        catch { CryptographicOperations.ZeroMemory(file); throw; }
    }

    public static string FileName(LedgerRecordItem item)
    {
        if (item.Content.StartsWith(SinglePrefix, StringComparison.Ordinal))
            return JsonSerializer.Deserialize<Single>(item.Content[SinglePrefix.Length..])?.FileName
                ?? throw new InvalidDataException("Invalid file metadata.");
        if (item.Content.StartsWith(ManifestPrefix, StringComparison.Ordinal))
            return JsonSerializer.Deserialize<Manifest>(item.Content[ManifestPrefix.Length..])?.FileName
                ?? throw new InvalidDataException("Invalid file manifest.");
        throw new InvalidDataException("Not a file.");
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 ||
            name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..")
            throw new InvalidDataException("Invalid file name in ledger metadata.");
    }

    private static void Verify(ReadOnlySpan<byte> bytes, string? hex)
    {
        if (hex is null) return; // SVFILE1 predates checksums.
        if (hex.Length != 64 || !Convert.TryFromHexString(hex, stackalloc byte[32], out int written) || written != 32)
            throw new InvalidDataException("Invalid file hash.");
        var actual = SHA256.HashData(bytes);
        try
        {
            var expected = Convert.FromHexString(hex);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                    throw new InvalidDataException("File checksum mismatch.");
            }
            finally { CryptographicOperations.ZeroMemory(expected); }
        }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }
}
