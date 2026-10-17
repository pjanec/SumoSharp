using CycloneDDS.Schema;

namespace Sim.Replication.Dds;

// docs/SUMOSHARP-NATIVE-VIEWER.md's "DDS topics" section — the two NEW low-rate topics for the native
// viewer's DDS data path (Phase 2a): durable lane geometry (sent once) and traffic-light state (low-rate).
// Both are opaque-blob topics carrying SumoSharp.Replication's GeometryCodec / TlCodec bytes, mirroring
// DdsWireFrame's shape exactly (`SetPayload`/`ReadPayload` bridging the codec) so the same "one blob format,
// any transport" pattern applies here too.

// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/geometry")]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public unsafe partial struct DdsNetworkGeometry
{
    // Geometry may span multiple chunks for a large net (GeometryCodec.PlanChunks); keyed by ChunkIndex so
    // a network's chunks are distinct instances (mirrors DdsWireFrame's (Kind, ChunkIndex) key — there is
    // only one "kind" here, so ChunkIndex alone is the key). TotalChunks lets a subscriber know when it has
    // received the whole network (== DdsNetworkGeometry samples with distinct ChunkIndex 0..TotalChunks-1).
    [DdsKey, DdsId(0)] public int ChunkIndex;
    [DdsId(1)] public int TotalChunks;
    [DdsId(2)] public uint Step;   // optional — geometry is published once, so this is typically 0
    [DdsId(3)] public float Time;  // optional — sim time at publish (typically 0.0)
    [DdsId(4)] public int ByteCount;
    [DdsId(5)] public fixed byte Payload[DdsWire.MaxPayload];
    // docs/HROT-INTEGRATION-DESIGN.md H8: the publisher (DdsReplicationSink's `source`); keyed with ChunkIndex
    // so two traffic nodes' networks are distinct instances. Appended.
    [DdsKey, DdsId(6)] public uint Source;

    public void SetPayload(int chunkIndex, int totalChunks, uint step, float time, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > DdsWire.MaxPayload)
        {
            throw new ArgumentException($"payload {bytes.Length} exceeds DdsWire.MaxPayload {DdsWire.MaxPayload}; chunk it.", nameof(bytes));
        }

        ChunkIndex = chunkIndex;
        TotalChunks = totalChunks;
        Step = step;
        Time = time;
        ByteCount = bytes.Length;
        fixed (byte* p = Payload)
        {
            bytes.CopyTo(new Span<byte>(p, DdsWire.MaxPayload));
        }
    }

    public readonly int ReadPayload(Span<byte> dst)
    {
        var n = ByteCount;
        fixed (byte* p = Payload)
        {
            new ReadOnlySpan<byte>(p, n).CopyTo(dst);
        }

        return n;
    }
}

// Low-rate traffic-light state (SUMOSHARP-DEADRECKONING.md §5.2): NOT on the high-rate vehicle packet by
// design (§4.3) — a separate topic, same blob pattern. Every committed scenario's TL state fits in one
// chunk (ChunkIndex kept for symmetry with the other blob topics / future-proofing a very large network).
// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/tl")]
[DdsQos(Reliability = DdsReliability.BestEffort, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public unsafe partial struct DdsTlState
{
    [DdsKey, DdsId(0)] public int ChunkIndex;
    [DdsId(1)] public uint Step;
    [DdsId(2)] public float Time;
    [DdsId(3)] public int ByteCount;
    [DdsId(4)] public fixed byte Payload[DdsWire.MaxPayload];
    // H8: the publisher, keyed with ChunkIndex. Appended.
    [DdsKey, DdsId(5)] public uint Source;

    public void SetPayload(uint step, float time, ReadOnlySpan<byte> bytes, int chunkIndex = 0)
    {
        if (bytes.Length > DdsWire.MaxPayload)
        {
            throw new ArgumentException($"payload {bytes.Length} exceeds DdsWire.MaxPayload {DdsWire.MaxPayload}; chunk it.", nameof(bytes));
        }

        ChunkIndex = chunkIndex;
        Step = step;
        Time = time;
        ByteCount = bytes.Length;
        fixed (byte* p = Payload)
        {
            bytes.CopyTo(new Span<byte>(p, DdsWire.MaxPayload));
        }
    }

    public readonly int ReadPayload(Span<byte> dst)
    {
        var n = ByteCount;
        fixed (byte* p = Payload)
        {
            new ReadOnlySpan<byte>(p, n).CopyTo(dst);
        }

        return n;
    }
}
