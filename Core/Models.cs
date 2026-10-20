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
    uint BaseFeeStroops,
    long BaseReserveStroops = 5_000_000)
{
    public string NormalizedHorizonUrl => HorizonUrl.Trim().TrimEnd('/');
}

public sealed class HorizonNetworkInfo
{
    public string NetworkPassphrase { get; set; } = string.Empty;
    public long BaseReserveStroops { get; set; }
    public uint MinimumBaseFeeStroops { get; set; }
    public uint RecommendedBaseFeeStroops { get; set; }
    public string HorizonVersion { get; set; } = string.Empty;
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
    public string? FolderId { get; init; }
    public string Kind { get; init; } = "record";
    public bool IsFolder => Kind == "folder";
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

public sealed record VaultCostEstimate(
    int OperationCount,
    int TransactionCount,
    int ChunkCount,
    int DataEntriesAdded,
    int DataEntriesRemoved,
    decimal NetworkFeeNative,
    decimal PeakReserveIncreaseNative,
    decimal NetReserveChangeNative,
    decimal ChildFundingTransferNative,
    bool CreatesChildNode);

public sealed record RecordManifest(
    byte FormatVersion,
    ushort ChunkCount,
    int EncryptedLength,
    byte[] CiphertextHash,
    long UpdatedUnixSeconds,
    string? Generation);

public sealed class RootTreasurySnapshot
{
    public string BalanceStroops { get; set; } = "0";
    public string MinimumBalanceStroops { get; set; } = "0";
    public string SpendableStroops { get; set; } = "0";
    public string AvailableAfterPlanStroops { get; set; } = "0";
    public bool CanAffordPlan { get; set; }
    public string SponsoredReserveUnits { get; set; } = "0";
    public string SponsoredReserveXlm { get; set; } = "0";
    public string BalanceXlm { get; set; } = "0";
    public string SpendableXlm { get; set; } = "0";
}
