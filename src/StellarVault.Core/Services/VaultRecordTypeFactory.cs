namespace StellarVault.Core.Services;

/// <summary>
/// A logical record type is independent of its MIME type, file extension, storage backend,
/// encryption format, and blockchain. Type identifiers are stable wire-facing names.
/// </summary>
public sealed record VaultRecordType(
    string Id,
    bool IsContainer,
    bool IsBinary,
    bool CanEditAsText);

public sealed record VaultRecordDefinition(
    string TypeId,
    string Name,
    string? ContentType,
    string? FileName = null);

/// <summary>Supplies application-level record definitions without creating plaintext or persisting anything.</summary>
public interface IVaultRecordTypeFactory
{
    VaultRecordDefinition Create(string typeId, string name, string? contentType = null, string? fileName = null);
    VaultRecordType Resolve(string typeId);
}

/// <summary>
/// Extensible record-type registry. Registrations must be explicit; unknown types are never
/// silently treated as editable text or executable/previewable content.
/// </summary>
public sealed class VaultRecordTypeFactory : IVaultRecordTypeFactory
{
    public const string Text = "text";
    public const string File = "file";
    public const string Folder = "folder";
    public const string Credential = "credential";
    public const string Unknown = "unknown";

    private readonly IReadOnlyDictionary<string, VaultRecordType> _types;

    public VaultRecordTypeFactory(IEnumerable<VaultRecordType>? additionalTypes = null)
    {
        var types = new Dictionary<string, VaultRecordType>(StringComparer.Ordinal)
        {
            [Text] = new(Text, false, false, true),
            [File] = new(File, false, true, false),
            [Folder] = new(Folder, true, false, false),
            [Credential] = new(Credential, false, false, false),
            [Unknown] = new(Unknown, false, true, false)
        };
        foreach (var type in additionalTypes ?? [])
        {
            ValidateTypeId(type.Id);
            if (!types.TryAdd(type.Id, type))
                throw new ArgumentException("Duplicate or reserved vault record type.");
        }
        _types = types;
    }

    public VaultRecordType Resolve(string typeId)
    {
        ValidateTypeId(typeId);
        return _types.TryGetValue(typeId, out var type) ? type : _types[Unknown];
    }

    public VaultRecordDefinition Create(string typeId, string name,
        string? contentType = null, string? fileName = null)
    {
        ValidateTypeId(typeId);
        if (!_types.ContainsKey(typeId))
            throw new NotSupportedException("Cannot create an unregistered vault record type.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Contains('\0'))
            throw new ArgumentException("Invalid vault record name.", nameof(name));
        if (contentType is not null &&
            (contentType.Length == 0 || contentType.Length > 128 || contentType.Contains('\0')))
            throw new ArgumentException("Invalid content type.", nameof(contentType));
        if (fileName is not null &&
            (fileName.Length == 0 || fileName.Length > 255 || fileName.Contains('\0') ||
             fileName.Contains('/') || fileName.Contains('\\')))
            throw new ArgumentException("Invalid file name.", nameof(fileName));
        if (typeId != File && fileName is not null)
            throw new ArgumentException("File names belong to file records.", nameof(fileName));
        if (typeId == Folder && contentType is not null)
            throw new ArgumentException("Folders do not have a MIME type.", nameof(contentType));
        if (typeId == Text)
        {
            if (contentType is not null && !IsLegacyText(contentType))
                throw new ArgumentException("Text records require text/plain.", nameof(contentType));
            contentType ??= "text/plain; charset=utf-8";
        }
        return new VaultRecordDefinition(typeId, name, contentType, fileName);
    }

    /// <summary>
    /// Compatibility discriminator for existing encrypted catalogs that only have ContentType.
    /// A null MIME type historically means text. Do not change this rule without a migration.
    /// </summary>
    public static bool IsLegacyText(string? contentType) =>
        contentType is null || contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
        contentType.StartsWith("text/plain;", StringComparison.OrdinalIgnoreCase);

    public static string ClassifyLegacy(string? contentType) =>
        IsLegacyText(contentType) ? Text : File;

    private static void ValidateTypeId(string typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId) || typeId.Length > 64 ||
            typeId.Any(c => !((c >= 'a' && c <= 'z') ||
                              (c >= '0' && c <= '9') || c is '-' or '.')))
            throw new ArgumentException("Record type IDs must use lower-case ASCII letters, digits, '-' or '.'.");
    }
}
