using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.JSInterop;

namespace StellarVaultPwa.Core;

public sealed class VaultCrypto
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IJSRuntime _js;
    private readonly string _sessionId;

    public VaultCrypto(IJSRuntime js, string sessionId)
    {
        _js = js;
        _sessionId = sessionId;
    }

    public async Task<byte[]> EncryptAsync(VaultRecord record)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        try
        {
            return await _js.InvokeAsync<byte[]>(
                "stellarVault.stellar.encrypt",
                _sessionId,
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
        var plaintext = await _js.InvokeAsync<byte[]>(
            "stellarVault.stellar.decrypt",
            _sessionId,
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

    public async Task<byte[]> EncryptHdAsync(
        VaultRecord record, int nodeIndex, string generation)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        try
        {
            return await _js.InvokeAsync<byte[]>(
                "stellarVault.stellar.encryptHdRecord",
                _sessionId, nodeIndex, record.Id,
                Convert.ToUInt32(generation, 16), plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<VaultRecord> DecryptHdAsync(
        string recordId, int nodeIndex, string generation, byte[] envelope)
    {
        var plaintext = await _js.InvokeAsync<byte[]>(
            "stellarVault.stellar.decryptHdRecord",
            _sessionId, nodeIndex, recordId,
            Convert.ToUInt32(generation, 16), envelope);
        try
        {
            return JsonSerializer.Deserialize<VaultRecord>(plaintext, JsonOptions)
                   ?? throw new InvalidDataException("The decrypted HD record is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public Task<byte[]> HashAsync(byte[] value) =>
        _js.InvokeAsync<byte[]>("stellarVault.stellar.sha256", value).AsTask();
}
