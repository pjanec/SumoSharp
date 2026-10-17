using CycloneDDS.Schema;

namespace Sim.Replication.Dds;

// docs/PEDESTRIAN-DDS-TRANSPORT-DESIGN.md §4/§5 -- the NEW DDS topic types for the live pedestrian wire, the
// ped mirror of the shipped vehicle binding (DdsTopics.cs / GeometryTlTopics.cs). The high-rate crowd frame
// REUSES the canonical DdsWireFrame (Kind = FrameCodec.KindPedFreeKinematic) -- it is exactly the same
// "opaque FrameCodec blob" pattern -- so only the two DURABLE, per-ped, keyed topics need new types here:
//
//  * DdsPedLeg      -- one keyed opaque blob per ped, sent once per leg. Carries EITHER the FrameCodec
//                      PathArc frame bytes (sumo/ped-patharc) OR the ActivityTimelineWire blob
//                      (sumo/ped-activity); the two topics share this one type (the "one struct, several
//                      topic names" pattern DdsWireFrame itself uses). Keyed by the ped handle Index so a
//                      late-joining IG gets the latest leg per ped under TRANSIENT_LOCAL durability.
//  * DdsPedLifecycle -- the low-rate keyed spawn/despawn + DR-model-switch event, the ped analog of
//                      DdsVehicleLifecycle.
//
// CRITICAL (see README.md): every writer/reader on these topics MUST be constructed with an explicit
// DdsTopicNames.Ped* string (the [DdsTopic] attribute's own name is NOT usable through CycloneDDS.NET
// 0.3.2's generator), and a writer/reader pair MUST use the identical string.

// Durable, keyed, per-ped opaque leg blob (PathArc frame OR ActivityTimeline blob). Same
// SetPayload/ReadPayload bridge as DdsNetworkGeometry; keyed by the ped handle Index (the Generation is
// carried too so the source can present a full VehicleHandle, though the current receiver reads only Index).
// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/ped-patharc")]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
public unsafe partial struct DdsPedLeg
{
    [DdsKey, DdsId(0)] public uint Index;      // ped handle index -- the DDS instance key (latest leg per ped)
    [DdsId(1)] public ushort Generation;       // ped handle generation (faithful; receiver currently reads only Index)
    [DdsId(2)] public int ByteCount;           // valid prefix length of Payload
    [DdsId(3)] public fixed byte Payload[DdsWire.MaxPayload];

    public void SetPayload(uint index, ushort generation, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > DdsWire.MaxPayload)
        {
            throw new ArgumentException($"payload {bytes.Length} exceeds DdsWire.MaxPayload {DdsWire.MaxPayload}; chunk it.", nameof(bytes));
        }

        Index = index;
        Generation = generation;
        ByteCount = bytes.Length;
        fixed (byte* p = Payload)
        {
            bytes.CopyTo(new Span<byte>(p, DdsWire.MaxPayload));
        }
    }

    // Copy the valid payload prefix into `dst`; returns its length. Feed dst[..len] to the matching decoder
    // (FrameCodec.ReadPathArcFrame for a patharc leg; ActivityTimelineWire.Decode -- in Sim.Pedestrians --
    // for an activity leg).
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

// Low-rate, KEYED (by ped index) lifecycle topic -- spawn/despawn + DR-model switch, the ped analog of
// DdsVehicleLifecycle. TRANSIENT_LOCAL so a late-joining IG gets each known ped's latest DR-model state.
// `Kind` is the byte-backed Sim.Replication.PedLifecycleKind; `Time` is the event's sim time.
// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/ped-lifecycle")]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
public partial struct DdsPedLifecycle
{
    [DdsKey, DdsId(0)] public uint Index;
    [DdsId(1)] public ushort Generation;
    [DdsId(2)] public byte Kind;       // Sim.Replication.PedLifecycleKind
    [DdsId(3)] public double Time;
}

// ⭐ HROT H11 — the crowd stream's OWN type (it shared DdsWireFrame with sumo/vehicles, and a type carries ONE topic name). Same
// layout and keys as DdsWireFrame; the copy logic is DdsWire's, not repeated.
// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/ped-crowd")]
[DdsQos(Reliability = DdsReliability.BestEffort, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public unsafe partial struct DdsPedCrowdFrame
{
    [DdsKey, DdsId(0)] public byte Kind;        // FrameCodec.KindPedFreeKinematic
    [DdsKey, DdsId(1)] public int ChunkIndex;
    [DdsId(2)] public uint Step;
    [DdsId(3)] public float Time;
    [DdsId(4)] public int ByteCount;
    [DdsId(5)] public fixed byte Payload[DdsWire.MaxPayload];
    [DdsKey, DdsId(6)] public uint Source;

    public void SetPayload(byte kind, uint step, float time, ReadOnlySpan<byte> bytes, int chunkIndex = 0)
    {
        Kind = kind; ChunkIndex = chunkIndex; Step = step; Time = time;
        fixed (byte* p = Payload) ByteCount = DdsWire.CopyIn(bytes, p);
    }

    public readonly int ReadPayload(Span<byte> dst)
    {
        fixed (byte* p = Payload) return DdsWire.CopyOut(p, ByteCount, dst);
    }
}

// ⭐ HROT H11 — the activity timeline's OWN type (it shared DdsPedLeg with sumo/ped-patharc). Same layout and key as DdsPedLeg.
// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/ped-activity")]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 16)]
public unsafe partial struct DdsPedActivityLeg
{
    [DdsKey, DdsId(0)] public uint Index;
    [DdsId(1)] public ushort Generation;
    [DdsId(2)] public int ByteCount;
    [DdsId(3)] public fixed byte Payload[DdsWire.MaxPayload];

    public void SetPayload(uint index, ushort generation, ReadOnlySpan<byte> bytes)
    {
        Index = index; Generation = generation;
        fixed (byte* p = Payload) ByteCount = DdsWire.CopyIn(bytes, p);
    }

    // Feed dst[..len] to ActivityTimelineWire.Decode (Sim.Pedestrians).
    public readonly int ReadPayload(Span<byte> dst)
    {
        fixed (byte* p = Payload) return DdsWire.CopyOut(p, ByteCount, dst);
    }
}
