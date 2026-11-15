using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VaultEngine.Core;

namespace StellarVault.Core.Services;

/// <summary>
/// First real provider seam for the chain-neutral object engine. Reuses the
/// existing authenticated Stellar ledger readers and encrypted local draft
/// queues. Only ordinary UTF-8 records/folders are supported in this phase.
/// File streaming, network fee quoting, revision-pinned edits/deletes and
/// single-account capacity enforcement intentionally fail closed until their
/// existing verified planners are connected.
/// </summary>
public sealed class StellarAccountObjectStoreProvider(
    IStellarLedgerRecords hdLedger,
    IStellarSecretKeyLedger linkedLedger,
    VaultHybridDraftWorkspace hdDrafts,
    VaultLinkedDraftWorkspace linkedDrafts,
    VaultNetworkSelection networks,
    IVaultRecordTypeFactory recordTypes) : IAccountObjectStoreProvider
{
    public string ProviderId => "stellar";
    private const int MaxOrdinaryContentBytes = 64 * 1024;

    private sealed record ScopeCheckpoint(
        string Root, string Network, string Strategy, long Epoch);

    private ScopeCheckpoint RequireScope(ObjectStoreScope scope, bool requireLedger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ProviderId != ProviderId ||
            scope.AccountStrategyId is not
                (VaultMutationStrategyPolicy.Hd or VaultMutationStrategyPolicy.Linked))
            throw new NotSupportedException("Unsupported Stellar object storage strategy.");
        StellarAccountAddress.Validate(scope.StoreId);
        var network = networks.CredentialKey(scope.StoreId);
        if (scope.NetworkId != network)
            throw new UnauthorizedAccessException(
                "The selected Stellar account/network does not match the object store.");
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
        {
            if (requireLedger && (!linkedLedger.IsConnected ||
                linkedLedger.RootAccountId != scope.StoreId))
                throw new UnauthorizedAccessException("The requested linked signer is unavailable.");
            if (linkedLedger.IsConnected)
            {
                if (linkedLedger.RootAccountId != scope.StoreId)
                    throw new UnauthorizedAccessException("A different linked signer is connected.");
                var requiredPassphrase = networks.Network switch
                {
                    "testnet" => "Test SDF Network ; September 2015",
                    "public" => "Public Global Stellar Network ; September 2015",
                    "custom" => networks.CustomPassphrase,
                    _ => null
                };
                if (requiredPassphrase is null ||
                    linkedLedger.ConnectedNetworkPassphrase != requiredPassphrase)
                    throw new UnauthorizedAccessException(
                        "The connected linked signer uses a different Stellar network.");
            }
            return new(scope.StoreId, network, scope.AccountStrategyId,
                linkedLedger.ConnectionEpoch);
        }
        if (requireLedger && !hdLedger.IsConnected)
            throw new UnauthorizedAccessException("The requested HD ledger is unavailable.");
        return new(scope.StoreId, network, scope.AccountStrategyId, 0);
    }

    private async Task RequireSameScopeAsync(ObjectStoreScope scope, ScopeCheckpoint before,
        bool requireLedger)
    {
        var now = RequireScope(scope, requireLedger);
        if (now != before)
            throw new InvalidOperationException("Stellar account, network or signer changed.");
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Hd && requireLedger)
        {
            var account = await hdLedger.InspectAccountInfoAsync();
            if (account.RootAccountId != scope.StoreId ||
                RequireScope(scope, true) != before)
                throw new InvalidOperationException("HD ledger root changed.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("Readable object stream required.", nameof(source));
        using var data = new MemoryStream();
        byte[] buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (data.Length + read > MaxOrdinaryContentBytes)
                    throw new NotSupportedException(
                        "Large or binary objects require the verified streaming file provider.");
                await data.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return data.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (data.TryGetBuffer(out var segment) && segment.Array is not null)
                CryptographicOperations.ZeroMemory(segment.Array);
        }
    }

    private LedgerRecordDraft BuildDraft(ObjectDescriptor descriptor, string text)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.TypeId is not
            (VaultRecordTypeFactory.Text or VaultRecordTypeFactory.Credential or
             VaultRecordTypeFactory.Folder))
            throw new NotSupportedException(
                "This Stellar provider phase supports only ordinary text, credentials and folders.");
        recordTypes.Create(descriptor.TypeId, descriptor.Name, descriptor.ContentType);
        if (descriptor.ParentId is { } parent &&
            (parent.Length != 16 || !parent.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid Stellar parent folder ID.");
        if (text.Length == 0 || !VaultRecordInterchange.IsOrdinaryContent(text))
            throw new InvalidDataException("Empty or reserved file-protocol content is not an ordinary record.");
        return new LedgerRecordDraft(descriptor.Name, text, descriptor.ParentId,
            IsFolder: descriptor.TypeId == VaultRecordTypeFactory.Folder);
    }

    private static void RequireSupportedCreate(ObjectWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExpectedRevision is not null)
            throw new NotSupportedException("Revision-pinned updates need the existing remote-mutation journal adapter.");
        if (options.CapacityPolicy != ObjectCapacityPolicy.ExpandWithExplicitApproval)
            throw new NotSupportedException(
                "Single-account capacity enforcement is not yet wired into the encrypted publisher.");
    }

    public Task<ObjectStorageQuote> EstimatePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
        ObjectWriteOptions options, CancellationToken ct = default)
    {
        // A streamed file requires a multi-node planner; a local draft does not
        // carry a binding fee quote. Never fabricate a monetary estimate.
        _ = RequireScope(scope, true);
        throw new NotSupportedException(
            "Object quotes must use the existing Stellar fee/reserve planner until the quote adapter is wired.");
    }

    public async Task<ObjectMutationIntent> PreparePutAsync(
        ObjectStoreScope scope, ObjectDescriptor descriptor, Stream source,
        ObjectWriteOptions options, CancellationToken ct = default)
    {
        RequireSupportedCreate(options);
        if (descriptor is null || descriptor.ObjectId != "new")
            throw new ArgumentException(
                "Create requests must use ObjectId='new'; the encrypted queue allocates the durable record ID.",
                nameof(descriptor));
        var checkpoint = RequireScope(scope, false);
        byte[] bytes = await ReadBoundedAsync(source, ct);
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            var draft = BuildDraft(descriptor, text);
            await RequireSameScopeAsync(scope, checkpoint, false);
            // Durable encrypted local mutation only, never a network write.
            string mutationId;
            string recordId;
            if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            {
                var queued = await linkedDrafts.CreateAsync(draft, ct);
                mutationId = queued.MutationId;
                recordId = queued.EntityId;
            }
            else
            {
                var queued = await hdDrafts.CreateAsync(draft, ct);
                mutationId = queued.Id;
                recordId = queued.EntityId;
            }
            await RequireSameScopeAsync(scope, checkpoint, false);
            return new ObjectMutationIntent(mutationId,
                ObjectMutationState.Prepared,
                new ObjectStorageQuote(ProviderId, scope.NetworkId, "XLM",
                    null, null, bytes.Length, false), null, recordId);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<LedgerRecordItem?> ReadOrdinaryAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct)
    {
        if (objectId.Length != 16 || !objectId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Stellar record ID.", nameof(objectId));
        var checkpoint = RequireScope(scope, true);
        IReadOnlyList<LedgerRecordItem> records;
        if (scope.AccountStrategyId == VaultMutationStrategyPolicy.Linked)
            records = await linkedLedger.ReadAsync(ct);
        else
        {
            var before = await hdLedger.InspectAccountInfoAsync();
            if (before.RootAccountId != scope.StoreId)
                throw new UnauthorizedAccessException("HD ledger root does not match object scope.");
            records = await hdLedger.ReadAsync();
        }
        await RequireSameScopeAsync(scope, checkpoint, true);
        var found = records.SingleOrDefault(x => x.Id == objectId);
        if (found is null) return null;
        if (!VaultRecordInterchange.IsOrdinary(found))
            throw new NotSupportedException(
                "Binary files must be opened through the existing verified file-tree reader.");
        return found;
    }

    public async Task<StoredObject?> GetAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct = default)
    {
        var record = await ReadOrdinaryAsync(scope, objectId, ct);
        if (record is null) return null;
        // Snapshot fingerprint is NOT a signed remote mutation revision and
        // MUST NOT be used as the authorization to overwrite or delete.
        byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(record);
        try
        {
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(snapshot));
            return new StoredObject(
                new ObjectDescriptor(record.Id,
                    record.IsFolder ? VaultRecordTypeFactory.Folder : VaultRecordTypeFactory.Credential,
                    record.Title, "text/plain", record.FolderId),
                "snapshot-sha256:" + fingerprint,
                Encoding.UTF8.GetByteCount(record.Content), fingerprint);
        }
        finally { CryptographicOperations.ZeroMemory(snapshot); }
    }

    public async Task<Stream> OpenReadAsync(
        ObjectStoreScope scope, string objectId, CancellationToken ct = default)
    {
        var record = await ReadOrdinaryAsync(scope, objectId, ct)
            ?? throw new FileNotFoundException("Stellar record not found.");
        // Caller owns plaintext and must dispose the stream. This simple text
        // reader is not the streaming SVTREE3 file reader.
        return new MemoryStream(Encoding.UTF8.GetBytes(record.Content), writable: false);
    }

    public Task<ObjectMutationIntent> PrepareDeleteAsync(
        ObjectStoreScope scope, string objectId, string expectedRevision,
        CancellationToken ct = default)
    {
        _ = RequireScope(scope, true);
        throw new NotSupportedException(
            "Deletion requires an authenticated, revision-pinned remote mutation journal.");
    }
}
