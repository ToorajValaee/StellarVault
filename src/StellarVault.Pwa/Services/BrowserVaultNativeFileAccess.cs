using System.Security.Cryptography;
using Microsoft.JSInterop;
using VaultEngine.Stellar.Services;

namespace StellarVault.Pwa.Services;

/// <summary>
/// Browser file picker/export adapter. File bytes are transferred directly
/// through JS interop and are never written to localStorage or browser caches.
/// </summary>
public sealed class BrowserVaultNativeFileAccess(IJSRuntime js) : IVaultNativeFileAccess, IVaultDeferredFileSaveAccess
{
    public sealed record BrowserPick(string? FileName, string? ContentType, string? ContentBase64);

    public async Task<VaultNativePickedFile?> PickAsync()
    {
        var picked = await js.InvokeAsync<BrowserPick?>("stellarVaultV2Download.pick");
        if (picked is null) return null;
        if (string.IsNullOrWhiteSpace(picked.FileName) ||
            string.IsNullOrEmpty(picked.ContentBase64))
            throw new InvalidDataException("Browser file picker returned incomplete metadata.");
        StellarLedgerFileProtocol.ValidateName(picked.FileName);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(picked.ContentBase64); }
        catch (FormatException ex) { throw new InvalidDataException("Browser file bytes were malformed.", ex); }
        // This adapter also backs portable record import. Authenticated
        // SVRB backups are slightly larger than their 1 MiB JSON plaintext
        // due to version, salt, nonce and GCM tag. The caller still enforces
        // its own format-specific limit before parsing or staging.
        if (bytes.Length <= 0 ||
            bytes.Length > Math.Max(StellarLedgerFileProtocol.MaxFileBytes,
                VaultEncryptedRecordBackup.MaximumBackupBytes))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException(
                "Selected file exceeds the browser picker size limit.");
        }
        return new VaultNativePickedFile(picked.FileName,
            new ZeroingMemoryStream(bytes),
            string.IsNullOrWhiteSpace(picked.ContentType)
                ? "application/octet-stream" : picked.ContentType);
    }

    public async Task<bool> SaveAsync(string fileName, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
            throw new ArgumentException("Readable export stream required.", nameof(content));
        StellarLedgerFileProtocol.ValidateName(fileName);
        // Browser-side saveStream uses showSaveFilePicker + ReadableStream,
        // preserving backpressure and aborting an unverified partial export.
        // Unlike the legacy byte[] interop, the file is never fully buffered.
        using var reference = new DotNetStreamReference(content, leaveOpen: true);
        await js.InvokeVoidAsync("stellarVaultV2Download.saveStream",
            CancellationToken.None, fileName, reference);
        return true;
    }

    public async Task<IVaultPendingFileSave?> SelectSaveDestinationAsync(string fileName)
    {
        StellarLedgerFileProtocol.ValidateName(fileName);
        // The JS function calls showSaveFilePicker synchronously, before
        // any .NET ledger read, encryption or other asynchronous continuation.
        var handle = await js.InvokeAsync<IJSObjectReference>(
            "stellarVaultV2Download.selectSaveDestination", fileName);
        return new BrowserPendingFileSave(handle);
    }

    private sealed class BrowserPendingFileSave(IJSObjectReference handle)
        : IVaultPendingFileSave
    {
        private bool _used;
        public async Task SaveAsync(Stream source)
        {
            if (_used) throw new InvalidOperationException("Export destination already used.");
            _used = true;
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead) throw new ArgumentException("Readable export required.", nameof(source));
            using var reference = new DotNetStreamReference(source, leaveOpen: true);
            await handle.InvokeVoidAsync("writeStream", reference);
        }

        public ValueTask DisposeAsync() => handle.DisposeAsync();
    }

    private sealed class ZeroingMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private byte[]? _owned = bytes;
        protected override void Dispose(bool disposing)
        {
            if (_owned is { } owned) CryptographicOperations.ZeroMemory(owned);
            _owned = null;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
