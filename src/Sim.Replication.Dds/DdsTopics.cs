using CycloneDDS.Schema;

namespace Sim.Replication.Dds;

// SUMOSHARP-DEADRECKONING.md §4.3 — the DDS topic types for dead-reckoning replication over CycloneDDS.NET.
// The high-rate path is the CANONICAL opaque blob (DdsWireFrame): it carries the exact bytes
// SumoSharp.Replication.FrameCodec produces, so one codec/wire format serves DDS + TCP + UDP, and 10k+
// movers ride ONE batched sample (per chunk) instead of one keyed sample per mover (which DDS per-sample
// overhead makes untenable). Keyed by (Kind, ChunkIndex) so a tick's chunks are distinct instances.
//
// Publish/read is a thin host wrapper (see README): a DdsWriter<DdsWireFrame> written at <=10 Hz, a
// DdsReader taking the latest; QoS BEST_EFFORT/KEEP_LAST for state, TRANSIENT_LOCAL for lifecycle/geometry.
// Kept OUT of the [DdsTopic] struct: the CycloneDDS code generator enumerates public members of a topic as
// serializable fields, so a public const there is mis-generated as an instance field.
public static unsafe class DdsWire
{
    // 64 KiB caps a chunk at ~1360 vehicle records (48 B) / ~2000 crowd records (32 B); chunk beyond that.
    public const int MaxPayload = 64 * 1024;

    /// <summary>Copies a codec-written blob into a topic's fixed payload; returns its length. ⛔ Throws past <see cref="MaxPayload"/>: chunk it.</summary>
    public static unsafe int CopyIn(ReadOnlySpan<byte> bytes, byte* payload)
    {
        if (bytes.Length > MaxPayload)
            throw new ArgumentException($"payload {bytes.Length} exceeds DdsWire.MaxPayload {MaxPayload}; chunk it.", nameof(bytes));
        bytes.CopyTo(new Span<byte>(payload, MaxPayload));
        return bytes.Length;
    }

    /// <summary>Copies a topic payload's valid prefix into <paramref name="dst"/>; returns its length.</summary>
    public static unsafe int CopyOut(byte* payload, int byteCount, Span<byte> dst)
    {
        new ReadOnlySpan<byte>(payload, byteCount).CopyTo(dst);
        return byteCount;
    }
}

// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/vehicles")]
[DdsQos(Reliability = DdsReliability.BestEffort, Durability = DdsDurability.Volatile, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 64)]
public unsafe partial struct DdsWireFrame
{
    [DdsKey, DdsId(0)] public byte Kind;        // FrameCodec.KindVehicle / KindCrowd (+ a geometry kind)
    [DdsKey, DdsId(1)] public int ChunkIndex;   // 0-based chunk within this tick
    [DdsId(2)] public uint Step;
    [DdsId(3)] public float Time;
    [DdsId(4)] public int ByteCount;            // valid prefix length of Payload
    [DdsId(5)] public fixed byte Payload[DdsWire.MaxPayload];
    // docs/HROT-INTEGRATION-DESIGN.md H8: the PUBLISHER this frame belongs to (DdsReplicationSink's `source`,
    // 0 by default), a further key member so two publishers' chunks are distinct instances. Appended.
    [DdsKey, DdsId(6)] public uint Source;

    // Blob copy helpers (pure; no DDS runtime dependency) so a publisher fills the sample straight from a
    // FrameCodec-written buffer, and a reader hands the valid prefix back to FrameCodec.
    public void SetPayload(byte kind, uint step, float time, ReadOnlySpan<byte> bytes, int chunkIndex = 0)
    {
        if (bytes.Length > DdsWire.MaxPayload)
        {
            throw new ArgumentException($"payload {bytes.Length} exceeds DdsWire.MaxPayload {DdsWire.MaxPayload}; chunk it.", nameof(bytes));
        }

        Kind = kind;
        ChunkIndex = chunkIndex;
        Step = step;
        Time = time;
        ByteCount = bytes.Length;
        fixed (byte* p = Payload)
        {
            bytes.CopyTo(new Span<byte>(p, DdsWire.MaxPayload));
        }
    }

    // Copy the valid payload prefix into `dst`; returns its length. Feed dst[..len] to FrameCodec.Read*.
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

// Low-rate, KEYED (by vehicle index) lifecycle/registry topic — DDS's "track many objects" sweet spot,
// affordable because it is low-rate. Spawn carries the immutable dims the renderer needs (the high-rate
// state topic then never repeats them); despawn tombstones the instance. TRANSIENT_LOCAL so late joiners
// get the current fleet.
//
// `Name` (docs/LIVE-CITY-VIEWERS-DESIGN.md §3.1, -TASKS.md E2): the real SUMO vehicle id, ADDITIVE. Uses
// CycloneDDS.NET's zero-alloc `FixedString64` (README "High-Performance Schema") rather than a plain
// `string` -- this topic's other fields are all unmanaged/fixed-size (no `[DdsManaged]` opt-in anywhere in
// this project), and FixedString64 keeps that zero-GC-allocation write/read path while still mapping to an
// IDL bounded string for wire interop. 64 chars comfortably covers SUMO's `id` attribute (route/flow-
// generated ids like "veh12.7" or "flow_0.3"); empty ("") on despawn, matching LifecycleRecord.Name.
// ⭐ HROT H11: the topic NAME and QoS live on the type, so any CycloneDDS.NET tool (ddsmonitor) discovers and decodes it, and every
// writer/reader uses the 1-arg ctor. The name equals DdsTopicNames.X (a rail pins the two).
[DdsTopic("sumo/lifecycle")]
[DdsQos(Reliability = DdsReliability.Reliable, Durability = DdsDurability.TransientLocal, HistoryKind = DdsHistoryKind.KeepLast, HistoryDepth = 1)]
public partial struct DdsVehicleLifecycle
{
    [DdsKey, DdsId(0)] public uint Index;
    [DdsId(1)] public ushort Generation;
    [DdsId(2)] public byte Event;      // 0 = spawned, 1 = despawned
    [DdsId(3)] public int VTypeId;
    [DdsId(4)] public float Length;
    [DdsId(5)] public float Width;
    [DdsId(6)] public FixedString64 Name;
    // docs/HROT-INTEGRATION-DESIGN.md H7: host-defined external type and colour (0xRRGGBB, 0 = none), spawn
    // only, zero on despawn -- appended, mirroring LifecycleRecord.ExternalType/Color.
    [DdsId(7)] public long ExternalType;
    [DdsId(8)] public uint Color;
    // H8: the publisher (see DdsWireFrame.Source) -- keyed with Index, so publisher B's car #5 never replaces
    // publisher A's car #5 in the durable "latest" a late joiner receives.
    [DdsKey, DdsId(9)] public uint Source;
}
