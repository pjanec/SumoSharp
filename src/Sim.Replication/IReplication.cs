using Sim.Core;

namespace Sim.Replication;

// docs/SUMOSHARP-PACKAGING-DESIGN.md P1 (D8) — the transport-neutral replication contract. DDS is one
// binding among several: DdsPublisher/DdsSubscriber (Sim.Viewer.Core) implement these two interfaces
// today, and InMemoryReplicationBus (this project) implements them for a same-process, non-DDS binding
// used by the hermetic test loop. The interfaces reference ONLY data-model types already defined in this
// project (VehicleRecord, GeometryCodec.LaneGeo, TlCodec.TlEntry, TimestampedSample,
// IVehicleSampleHistory, VehicleHandle) -- never a DDS type -- so a consumer coded against
// IReplicationSink/IReplicationSource never needs to know CycloneDDS exists.

// A vehicle spawn/despawn + physical dims announcement (durable, once per vehicle) -- the transport-
// neutral analog of DdsVehicleLifecycle (Sim.Replication.Dds), minus the wire-specific Index/Generation
// split (VehicleHandle already carries both).
//
// `Name` (docs/LIVE-CITY-VIEWERS-DESIGN.md §3.1, -TASKS.md E2): the real SUMO vehicle id, ADDITIVE and
// populated on SPAWN only (empty string on despawn -- a despawn tombstone needs no identity, the consumer
// already has it from the matching spawn). Travels ONCE per entity on this lifecycle event, never on the
// per-frame VehicleRecord/DdsWireFrame -- at 100k entities the hot per-step path pays nothing extra for it.
public readonly struct LifecycleRecord
{
    public LifecycleRecord(VehicleHandle handle, bool isSpawn, int vTypeId, float length, float width, string name = "")
        : this(handle, isSpawn, vTypeId, length, width, name, externalType: 0, color: 0)
    {
    }

    // docs/HROT-INTEGRATION-DESIGN.md H7: the same record carrying a host-defined external type and a colour
    // (0xRRGGBB, 0 = none). ADDITIVE: the 6-argument ctor above passes zeros, so every existing site is
    // unchanged. Like `Name`, set on SPAWN only (a despawn carries zeros).
    public LifecycleRecord(
        VehicleHandle handle, bool isSpawn, int vTypeId, float length, float width, string name, long externalType, uint color)
    {
        Handle = handle;
        IsSpawn = isSpawn;
        VTypeId = vTypeId;
        Length = length;
        Width = width;
        Name = name ?? "";
        ExternalType = externalType;
        Color = color;
    }

    public VehicleHandle Handle { get; }
    public bool IsSpawn { get; } // false = despawn
    public int VTypeId { get; }
    public float Length { get; }
    public float Width { get; }
    // The real SUMO vehicle id, set on spawn; "" on despawn (see class remark).
    public string Name { get; }

    // H7: a host-defined vehicle type (e.g. HROT's entity type), 0 = none. Spawn only.
    public long ExternalType { get; }

    // H7: the vehicle's colour as 0xRRGGBB, 0 = none. Spawn only.
    public uint Color { get; }

    // H7: the (ExternalType, Color) pair as the tag a source stores.
    public VehicleTag Tag => new(ExternalType, Color);
}

// docs/HROT-INTEGRATION-DESIGN.md H7: the per-vehicle external type + colour a source accumulates from the
// lifecycle stream (IReplicationSource.Tags). `IsEmpty` (both zero) is the default and is never stored.
public readonly struct VehicleTag : IEquatable<VehicleTag>
{
    public VehicleTag(long externalType, uint color)
    {
        ExternalType = externalType;
        Color = color;
    }

    public long ExternalType { get; }

    // 0xRRGGBB, 0 = none.
    public uint Color { get; }

    public bool IsEmpty => ExternalType == 0 && Color == 0;

    public bool Equals(VehicleTag other) => ExternalType == other.ExternalType && Color == other.Color;

    public override bool Equals(object? obj) => obj is VehicleTag t && Equals(t);

    public override int GetHashCode() => (ExternalType.GetHashCode() * 397) ^ (int)Color;

    public override string ToString() => $"VehicleTag({ExternalType}, 0x{Color:X6})";
}

// Transport-neutral SEND contract. DDS is one binding; in-memory (InMemoryReplicationBus) is another.
public interface IReplicationSink : IDisposable
{
    void PublishGeometry(IReadOnlyList<GeometryCodec.LaneGeo> lanes);                     // durable, once
    void PublishLifecycle(in LifecycleRecord record);                                     // durable, per spawn/despawn
    void PublishFrame(uint step, double time, ReadOnlySpan<VehicleRecord> movers);         // volatile, per frame
    void PublishTrafficLights(uint step, double time, IReadOnlyList<TlCodec.TlEntry> lights); // low-rate
}

// Transport-neutral RECEIVE contract. Mirrors DdsSubscriber's existing public surface exactly.
public interface IReplicationSource : IDisposable
{
    void Pump();
    IReadOnlyDictionary<int, GeometryCodec.LaneGeo> Geometry { get; }
    bool GeometryComplete { get; }
    IReadOnlyDictionary<VehicleHandle, IVehicleSampleHistory> History { get; }
    IReadOnlyDictionary<VehicleHandle, (float Length, float Width)> Dims { get; }
    // docs/LIVE-CITY-VIEWERS-DESIGN.md §3.1, -TASKS.md E2: the handle->SUMO-id table accumulated from
    // LifecycleRecord.Name on spawn (removed on despawn, mirroring Dims). Built purely from the lifecycle
    // stream, never the per-frame record, so every IReplicationSource binding (in-memory, DDS, file replay)
    // gets it "for free" the same way Dims already is.
    IReadOnlyDictionary<VehicleHandle, string> Names { get; }
    // docs/HROT-INTEGRATION-DESIGN.md H7: the handle->(ExternalType, Color) table from LifecycleRecord's tag on
    // spawn, removed on despawn like Names. Only NON-EMPTY tags are stored: absent means (0, 0).
    IReadOnlyDictionary<VehicleHandle, VehicleTag> Tags { get; }
    IReadOnlyDictionary<int, byte> TlStateByLane { get; }
    double? LatestVehicleSampleTime { get; }
    bool Connected { get; }
    void ResetVehicles();
    bool TryGetLatest(VehicleHandle handle, out TimestampedSample sample);
}
