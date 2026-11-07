using System.Security.Cryptography;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Encrypted hybrid-queue staging for file uploads. The queue's host adapter
/// encrypts the complete intent (file bytes, metadata and stable record IDs)
/// at rest. This workspace never submits to Stellar.
/// </summary>
public sealed class VaultHybridFileWorkspace(
    VaultPendingMutationQueue queue,
    VaultSessionManager sessions,
    VaultDeviceRetentionGate retention,
    VaultNetworkSelection networks)
{
    private const int IntentVersion = 1;

    public sealed record FileUploadIntent(
        int Version, string Title, string FileName, string ContentType,
        string? FolderId, byte[] Content, string Fingerprint, string[] EntryIds);

    public sealed record FileDraftItem(
        string MutationId, string ManifestId, string FileName, string ContentType,
        string? FolderId, long Length, int LedgerEntries, string Fingerprint,
        VaultSyncState State, DateTimeOffset CreatedUtc,
        string[] DependsOn, bool IsCorrupt = false);

    public async Task<FileDraftItem> StageAsync(
        string fileName, string contentType, Stream content,
        string? folderId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("Readable file stream required.", nameof(content));
        var (account, scope) = RequireScope();
        StellarLedgerFileProtocol.ValidateName(fileName);
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128 ||
            contentType.Contains('\0'))
            throw new ArgumentException("Invalid file content type.", nameof(contentType));
        if (folderId is not null &&
            (folderId.Length != 16 || !folderId.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid parent folder ID.", nameof(folderId));

        byte[] bytes = await ReadBoundedAsync(content, ct);
        byte[]? payload = null;
        try
        {
            if (bytes.Length == 0) throw new InvalidDataException("Empty files are not supported.");
            var all = await queue.ReviewAllAsync(account, scope, ct);
            string[] dependencies;
            HashSet<string> occupied;
            try
            {
                dependencies = ParentDependencies(folderId, all);
                occupied = new HashSet<string>(
                    all.Select(x => x.EntityId), StringComparer.OrdinalIgnoreCase);
                foreach (var existingFile in all.Where(x => x.Kind == VaultMutationKind.UploadFile))
                {
                    var priorIntent = DeserializeAndValidate(existingFile);
                    try
                    {
                        // The last stable ID is the manifest and is already
                        // represented by the queue entry's EntityId above.
                        foreach (var id in priorIntent.EntryIds.Take(priorIntent.EntryIds.Length - 1))
                            if (!occupied.Add(id))
                                throw new InvalidDataException("Existing file upload IDs collide; investigate the queue before staging another file.");
                    }
                    finally { CryptographicOperations.ZeroMemory(priorIntent.Content); }
                }
            }
            finally
            {
                foreach (var queued in all)
                    CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
            }

            var displayTitle = fileName.Length <= 256 ? fileName : fileName[..256];
            var partCount = bytes.Length <= StellarLedgerFileProtocol.MaxChunkBytes
                ? 1
                : (bytes.Length + StellarLedgerFileProtocol.MaxChunkBytes - 1) /
                    StellarLedgerFileProtocol.MaxChunkBytes + 1;
            var ids = CreateUniqueIds(partCount, occupied);
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            var stagedBytes = bytes.ToArray();
            try
            {
                var intent = new FileUploadIntent(IntentVersion, displayTitle, fileName,
                    contentType, folderId, stagedBytes, fingerprint, ids);
                payload = JsonSerializer.SerializeToUtf8Bytes(intent);
            }
            finally { CryptographicOperations.ZeroMemory(stagedBytes); }
            if (payload.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("Encrypted file upload intent exceeds the queue size limit.");
            var mutation = VaultPendingMutation.Create(account, scope,
                VaultMutationKind.UploadFile, ids[^1], payload,
                dependsOn: dependencies);
            try
            {
                await queue.EnqueueAsync(mutation, ct);
                RequireSameScope(account, scope);
                return new(mutation.Id, mutation.EntityId, fileName, contentType,
                    folderId, bytes.LongLength, ids.Length, fingerprint,
                    VaultSyncState.Pending, mutation.CreatedUtc, dependencies);
            }
            finally { CryptographicOperations.ZeroMemory(mutation.EncryptedPayload); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task<IReadOnlyList<FileDraftItem>> ListAsync(CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        var result = new List<FileDraftItem>();
        foreach (var entry in entries.Where(x => x.Kind == VaultMutationKind.UploadFile))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var intent = DeserializeAndValidate(entry);
                try
                {
                    result.Add(new(entry.Id, entry.EntityId, intent.FileName,
                        intent.ContentType, intent.FolderId, intent.Content.LongLength,
                        intent.EntryIds.Length, intent.Fingerprint, entry.State, entry.CreatedUtc,
                        entry.DependsOn.ToArray()));
                }
                finally { CryptographicOperations.ZeroMemory(intent.Content); }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or
                                        ArgumentException or CryptographicException)
            {
                result.Add(new(entry.Id, entry.EntityId, "UNREADABLE FILE INTENT",
                    "application/octet-stream", null, 0, 0, string.Empty,
                    entry.State, entry.CreatedUtc, entry.DependsOn.ToArray(), true));
            }
            finally { CryptographicOperations.ZeroMemory(entry.EncryptedPayload); }
        }
        RequireSameScope(account, scope);
        return result.OrderBy(x => x.CreatedUtc).ToArray();
    }

    /// <summary>Reconstruct the immutable manifest-last plan for a future
    /// approved publisher. This method performs no network calls.</summary>
    public async Task<StellarLedgerUploadPlan> GetPlanAsync(
        string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        var entries = await queue.ReviewAllAsync(account, scope, ct);
        try
        {
            var entry = entries.SingleOrDefault(x =>
                x.Id == mutationId && x.Kind == VaultMutationKind.UploadFile);
            if (entry is null) throw new KeyNotFoundException("Staged file upload does not exist.");
            if (entry.State != VaultSyncState.Pending)
                throw new InvalidOperationException("Only an unsubmitted file upload can be opened for publication review.");
            var intent = DeserializeAndValidate(entry);
            try
            {
                var plan = new StellarLedgerUploadPlan(intent.Title, intent.FileName,
                    intent.ContentType, intent.Content, intent.FolderId, intent.EntryIds);
                RequireSameScope(account, scope);
                return plan;
            }
            finally { CryptographicOperations.ZeroMemory(intent.Content); }
        }
        finally
        {
            foreach (var queued in entries)
                CryptographicOperations.ZeroMemory(queued.EncryptedPayload);
        }
    }

    public async Task DiscardAsync(string mutationId, CancellationToken ct = default)
    {
        var (account, scope) = RequireScope();
        await queue.DiscardPendingAsync(account, scope, mutationId, ct);
        RequireSameScope(account, scope);
    }

    private FileUploadIntent DeserializeAndValidate(VaultPendingMutation entry)
    {
        var intent = JsonSerializer.Deserialize<FileUploadIntent>(entry.EncryptedPayload)
            ?? throw new InvalidDataException("Staged file intent is empty.");
        if (intent.Version != IntentVersion ||
            string.IsNullOrWhiteSpace(intent.Title) || intent.Title.Length > 256 ||
            intent.Content is not { Length: > 0 and <= StellarLedgerFileProtocol.MaxFileBytes } ||
            intent.EntryIds is not { Length: > 0 and <= StellarLedgerFileProtocol.MaxParts + 1 } ||
            intent.Fingerprint is not { Length: 64 } ||
            !intent.Fingerprint.All(Uri.IsHexDigit) ||
            intent.EntryIds.Any(x => x is null || x.Length != 16 || !x.All(Uri.IsHexDigit)) ||
            intent.EntryIds.Distinct(StringComparer.Ordinal).Count() != intent.EntryIds.Length ||
            (intent.FolderId is not null &&
                (intent.FolderId.Length != 16 || !intent.FolderId.All(Uri.IsHexDigit))))
        {
            if (intent.Content is not null) CryptographicOperations.ZeroMemory(intent.Content);
            throw new InvalidDataException("Staged file intent metadata is invalid.");
        }
        try
        {
            StellarLedgerFileProtocol.ValidateName(intent.FileName);
            if (string.IsNullOrWhiteSpace(intent.ContentType) ||
                intent.ContentType.Length > 128 || intent.ContentType.Contains('\0'))
                throw new InvalidDataException("Staged file content type is invalid.");
            var actual = Convert.ToHexString(SHA256.HashData(intent.Content));
            if (!string.Equals(actual, intent.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged file fingerprint does not match its bytes.");
            var expectedEntries = intent.Content.Length <= StellarLedgerFileProtocol.MaxChunkBytes
                ? 1
                : (intent.Content.Length + StellarLedgerFileProtocol.MaxChunkBytes - 1) /
                    StellarLedgerFileProtocol.MaxChunkBytes + 1;
            if (intent.EntryIds.Length != expectedEntries ||
                !string.Equals(intent.EntryIds[^1], entry.EntityId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged file part inventory or manifest identity differs from its queue entry.");
            return intent;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(intent.Content);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] block = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(block, ct);
                if (read == 0) break;
                if (buffer.Length + read > StellarLedgerFileProtocol.MaxFileBytes)
                    throw new InvalidDataException("File exceeds the 1 MiB hybrid upload limit.");
                await buffer.WriteAsync(block.AsMemory(0, read), ct);
            }
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(block);
            if (buffer.TryGetBuffer(out var segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array);
        }
    }

    private static string[] CreateUniqueIds(int count, HashSet<string> occupied)
    {
        var ids = new string[count];
        for (var index = 0; index < count; index++)
        {
            var allocated = false;
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var candidate = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                if (!occupied.Add(candidate)) continue;
                ids[index] = candidate;
                allocated = true;
                break;
            }
            if (!allocated)
                throw new InvalidOperationException("Could not allocate collision-free file chunk identifiers.");
        }
        return ids;
    }

    private static string[] ParentDependencies(
        string? folderId, IReadOnlyList<VaultPendingMutation> entries)
    {
        if (folderId is null) return [];
        var parents = entries.Where(x => x.EntityId.Equals(
            folderId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parents.Length > 1) throw new InvalidOperationException("Parent folder ID is ambiguous.");
        if (parents.Length == 0 || parents[0].State == VaultSyncState.Confirmed) return [];
        var parent = parents[0];
        if (parent.Kind != VaultMutationKind.CreateFolder ||
            parent.State != VaultSyncState.Pending || parent.TransactionHash is not null)
            throw new InvalidOperationException("Only a pending or confirmed folder can contain a staged file.");
        return [parent.Id];
    }

    private (string Account, string Network) RequireScope()
    {
        retention.RequireLocalPersistence();
        var account = sessions.ActiveAccountId
            ?? throw new InvalidOperationException("Unlock the encrypted local vault first.");
        if (!sessions.IsUnlocked)
            throw new InvalidOperationException("Unlock the encrypted local vault first.");
        return (account, networks.CredentialKey(account));
    }

    private void RequireSameScope(string account, string network)
    {
        if (!sessions.IsUnlocked || sessions.ActiveAccountId != account ||
            networks.CredentialKey(account) != network)
            throw new InvalidOperationException("Vault account or network changed during file staging.");
    }
}
