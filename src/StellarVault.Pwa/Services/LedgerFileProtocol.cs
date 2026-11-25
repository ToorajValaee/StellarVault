using VaultEngine.Stellar.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Compatibility facade for PWA callers. The shared Core owns all encoding,
/// chunk ordering, manifest verification and recovery rules for every host.
/// </summary>
public static class LedgerFileProtocol
{
    public const string SinglePrefix = StellarLedgerFileProtocol.SinglePrefix;
    public const string ManifestPrefix = StellarLedgerFileProtocol.ManifestPrefix;
    public const string ChunkPrefix = StellarLedgerFileProtocol.ChunkPrefix;
    public const int MaxChunkBytes = StellarLedgerFileProtocol.MaxChunkBytes;
    public const int MaxFileBytes = StellarLedgerFileProtocol.MaxFileBytes;
    public const int MaxParts = StellarLedgerFileProtocol.MaxParts;

    // Retain the legacy public metadata shapes for source compatibility.
    public sealed record Single(string FileName, string ContentType, string DataBase64, string? Sha256Hex = null);
    public sealed record Manifest(string FileName, string ContentType, long Length, string Sha256Hex, string[] PartIds);
    public sealed record Part(string FileId, int Index, string DataBase64, string Sha256Hex);

    public static IReadOnlyList<(string Id, LedgerRecordDraft Draft)> BuildUpload(
        string title, string filename, string contentType, ReadOnlySpan<byte> bytes,
        string? folderId = null, string[]? existingIds = null) =>
        StellarLedgerFileProtocol.BuildUpload(title, filename, contentType, bytes, folderId, existingIds);

    public static bool IsFile(LedgerRecordItem item) => StellarLedgerFileProtocol.IsFile(item);

    public static string EncodeSingle(string name, string contentType, ReadOnlySpan<byte> bytes) =>
        StellarLedgerFileProtocol.EncodeSingle(name, contentType, bytes);

    public static byte[] DecodeFile(LedgerRecordItem item, IReadOnlyList<LedgerRecordItem> all) =>
        StellarLedgerFileProtocol.DecodeFile(item, all);

    public static string FileName(LedgerRecordItem item) => StellarLedgerFileProtocol.FileName(item);

    public static void ValidateName(string name) => StellarLedgerFileProtocol.ValidateName(name);
}
