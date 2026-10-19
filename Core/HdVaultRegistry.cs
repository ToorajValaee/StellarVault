using System.Text.Json;

namespace StellarVaultPwa.Core;

public sealed record HdVaultRegistry(int Version, int HighestAllocatedIndex, int ActiveWriteIndex, long Revision)
{
    public const int CurrentVersion = 2;

    public static HdVaultRegistry NewVault() => new(CurrentVersion, 0, 0, 0);

    public HdVaultRegistry AllocateNext()
    {
        Validate();
        var next = HdNodeAllocation.NextIndex(HighestAllocatedIndex);
        return this with { HighestAllocatedIndex = next, ActiveWriteIndex = next, Revision = checked(Revision + 1) };
    }

    public void Validate()
    {
        if (Version != CurrentVersion || HighestAllocatedIndex < 0 ||
            ActiveWriteIndex < 0 || ActiveWriteIndex > HighestAllocatedIndex || Revision < 0)
            throw new InvalidDataException("Invalid HD vault registry.");
    }

    public byte[] Serialize()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(this);
    }

    public static HdVaultRegistry Parse(ReadOnlySpan<byte> encoded)
    {
        var value = JsonSerializer.Deserialize<HdVaultRegistry>(encoded)
            ?? throw new InvalidDataException("Empty HD vault registry.");
        value.Validate();
        return value;
    }
}
