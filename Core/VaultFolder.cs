namespace StellarVaultPwa.Core;

/// <summary>
/// Folder metadata is vault content and must be encrypted before it is
/// persisted. Folder IDs, names and parent relationships must not be
/// written to public ManageData keys in plaintext.
/// </summary>
public sealed record VaultFolder(
    string Id,
    string Name,
    string? ParentId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public static class VaultFolderTree
{
    public const int MaximumDepth = 64;
    public const int MaximumNameLength = 128;

    public static void Validate(IReadOnlyCollection<VaultFolder> folders)
    {
        var byId = new Dictionary<string, VaultFolder>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Id) ||
                folder.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(folder.Name) ||
                folder.Name.Trim().Length > MaximumNameLength)
                throw new InvalidDataException("Invalid folder ID or name.");
            if (!byId.TryAdd(folder.Id, folder))
                throw new InvalidDataException("Duplicate folder ID.");
        }

        foreach (var folder in folders)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { folder.Id };
            var cursor = folder;
            var depth = 0;
            while (cursor.ParentId is not null)
            {
                if (!byId.TryGetValue(cursor.ParentId, out var parent))
                    throw new InvalidDataException("Folder parent does not exist.");
                if (!seen.Add(parent.Id))
                    throw new InvalidDataException("Folder hierarchy contains a cycle.");
                if (++depth > MaximumDepth)
                    throw new InvalidDataException("Folder nesting exceeds safety limit.");
                cursor = parent;
            }
        }
    }

    public static IReadOnlyList<string> Descendants(
        IReadOnlyCollection<VaultFolder> folders, string folderId)
    {
        Validate(folders);
        if (!folders.Any(folder => folder.Id == folderId))
            throw new KeyNotFoundException("Folder not found.");
        var descendants = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(folderId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in folders.Where(folder => folder.ParentId == parent))
            {
                descendants.Add(child.Id);
                queue.Enqueue(child.Id);
            }
        }
        return descendants;
    }
}
