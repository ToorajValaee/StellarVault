namespace StellarVaultPwa.Core;

public static class StellarNetworkPassphrases
{
    public const string Testnet = "Test SDF Network ; September 2015";
    public const string Public = "Public Global Stellar Network ; September 2015";
}

public sealed record VaultNetworkSettings(
    string Name,
    string HorizonUrl,
    string Passphrase,
    string ChildStartingBalance,
    int NodeDataEntryLimit,
    uint BaseFeeStroops)
{
    public string NormalizedHorizonUrl => HorizonUrl.Trim().TrimEnd('/');
}

public sealed class VaultRecord
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string UserName { get; init; } = string.Empty;
    public required string Secret { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string[] Tags { get; init; } = [];
    public required DateTimeOffset CreatedUtc { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record VaultRecordLocation(
    VaultRecord Record,
    string NodeAccountId,
    int ChunkCount,
    int EncryptedByteLength);

public sealed record VaultNodeSnapshot(
    string AccountId,
    int Position,
    int SubentryCount,
    IReadOnlyDictionary<string, byte[]> Data,
    string? NextAccountId);

public sealed record VaultReadResult(
    IReadOnlyList<VaultRecordLocation> Records,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<VaultNodeSnapshot> Nodes);

public sealed record VaultMutationResult(
    string TransactionHash,
    string? Warning = null);

internal sealed record RecordManifest(
    byte FormatVersion,
    ushort ChunkCount,
    int EncryptedLength,
    byte[] CiphertextHash,
    long UpdatedUnixSeconds,
    string? Generation);
