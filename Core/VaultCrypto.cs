using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.JSInterop;
using StellarDotnetSdk.Accounts;

namespace StellarVaultPwa.Core;

public sealed class VaultCrypto : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static long _contextCounter;

    private readonly IJSRuntime _js;
    private readonly KeyPair _rootKeyPair;
    private readonly VaultNetworkSettings _settings;
    private readonly string _contextId;
    private bool _initialized;
    private bool _disposed;

    public VaultCrypto(IJSRuntime js, KeyPair rootKeyPair, VaultNetworkSettings settings)
    {
        _js = js;
        _rootKeyPair = rootKeyPair;
        _settings = settings;
        _contextId = $"sv-{Interlocked.Increment(ref _contextCounter):x}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}";
    }

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        if (_initialized)
            return;

        var sourceSeed = _rootKeyPair.SeedBytes
            ?? throw new ArgumentException("The root key pair must contain a secret seed.", nameof(_rootKeyPair));
        var seedCopy = sourceSeed.ToArray();

        try
        {
            await _js.InvokeVoidAsync(
                "stellarVault.crypto.createContext",
                _contextId,
                seedCopy,
                _settings.Passphrase,
                _rootKeyPair.AccountId);
            _initialized = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedCopy);
        }
    }

    public async Task<byte[]> EncryptAsync(VaultRecord record)
    {
        ThrowIfNotReady();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);

        try
        {
            return await _js.InvokeAsync<byte[]>(
                "stellarVault.crypto.encrypt",
                _contextId,
                record.Id,
                plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<VaultRecord> DecryptAsync(string recordId, byte[] envelope)
    {
        ThrowIfNotReady();
        var plaintext = await _js.InvokeAsync<byte[]>(
            "stellarVault.crypto.decrypt",
            _contextId,
            recordId,
            envelope);

        try
        {
            return JsonSerializer.Deserialize<VaultRecord>(plaintext, JsonOptions)
                   ?? throw new InvalidDataException("The decrypted record is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<byte[]> HashAsync(byte[] value)
    {
        ThrowIfNotReady();
        return await _js.InvokeAsync<byte[]>("stellarVault.crypto.sha256", value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (_initialized)
        {
            try
            {
                await _js.InvokeVoidAsync("stellarVault.crypto.deleteContext", _contextId);
            }
            catch (JSDisconnectedException)
            {
                // The browser page is already unloading; its JS memory is being destroyed.
            }
        }

        _initialized = false;
        _disposed = true;
    }

    private void ThrowIfNotReady()
    {
        ThrowIfDisposed();
        if (!_initialized)
            throw new InvalidOperationException("The browser encryption context is not initialized.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
