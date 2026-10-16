using System.Buffers.Binary;

namespace Yap.LoadTest.Shared;

/// <summary>
/// A message's clientId carries its send time. Every virtual user on this machine shares one clock, so a receiver
/// measures fan-out latency from a clientId alone, with no dictionary shared across processes. Layout of the 16
/// bytes: unix ms (8), the marker "LT" (2), the worker id (2), random (4). A Guid round-trips the bytes exactly.
/// </summary>
public static class ClientIds
{
    private const ushort Marker = 0x4C54;

    public static Guid New(int worker)
    {
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(b, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt16LittleEndian(b[8..], Marker);
        BinaryPrimitives.WriteUInt16LittleEndian(b[10..], (ushort)worker);
        Random.Shared.NextBytes(b[12..]);
        return new Guid(b);
    }

    public static bool TryDecode(Guid id, out long sentUnixMs, out int worker)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b);
        sentUnixMs = BinaryPrimitives.ReadInt64LittleEndian(b);
        worker = BinaryPrimitives.ReadUInt16LittleEndian(b[10..]);
        return BinaryPrimitives.ReadUInt16LittleEndian(b[8..]) == Marker;
    }
}
