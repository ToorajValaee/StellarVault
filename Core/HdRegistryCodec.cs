using System.Buffers.Binary;

namespace StellarVaultPwa.Core;

public static class HdRegistryCodec
{
    // Fixed 18-byte format fits a single Stellar ManageData value.
    // Authentication and replay protection are the responsibility of the caller.
    public const int Length = 18;

    public static byte[] Encode(HdVaultRegistry registry)
    {
        registry.Validate();
        var value = new byte[Length];
        value[0] = 0x53;
        value[1] = 0x02;
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(2, 4), registry.HighestAllocatedIndex);
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(6, 4), registry.ActiveWriteIndex);
        BinaryPrimitives.WriteInt64BigEndian(value.AsSpan(10, 8), registry.Revision);
        return value;
    }

    public static HdVaultRegistry Decode(ReadOnlySpan<byte> value)
    {
        if (value.Length != Length || value[0] != 0x53 || value[1] != 0x02)
            throw new InvalidDataException("Invalid HD registry encoding.");
        var registry = new HdVaultRegistry(
            2,
            BinaryPrimitives.ReadInt32BigEndian(value.Slice(2, 4)),
            BinaryPrimitives.ReadInt32BigEndian(value.Slice(6, 4)),
            BinaryPrimitives.ReadInt64BigEndian(value.Slice(10, 8)));
        registry.Validate();
        return registry;
    }
}
