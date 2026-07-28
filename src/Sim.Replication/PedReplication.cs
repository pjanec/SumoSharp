using System.Buffers.Binary;
using Sim.Core;

namespace Sim.Replication;

// P3-1 (docs/PEDESTRIAN-TASKS.md; docs/PEDESTRIAN-DESIGN.md §7) -- the transport-neutral pedestrian
// replication surface, mirroring IReplication.cs's vehicle IReplicationSink/IReplicationSource pair
// exactly, so a caller coded against IPedReplicationSink/IPedReplicationSource never needs to know
// whether it holds DDS or an in-process binding. Stays FREE of Sim.Pedestrians: every type referenced
// here is already defined in this project (VehicleHandle from Sim.Core; PathArcRecord and
// PedFreeKinematicRecord from Records.cs; FrameCodec for the byte-level packing). The bridge that maps
// Sim.Pedestrians.Lod.PedEvent onto/from this surface lives in Sim.Pedestrians
// (PedReplicationPublisher.cs / PedReplicationReceiver.cs) -- the one place allowed to reference both
// projects.

// A ped spawn/despawn/DR-model-switch broadcast (durable, keyed by Handle) -- the transport-neutral
// analog of Sim.Pedestrians.Lod.DrSwitchEvent, plus the spawn/despawn cases the same lifecycle topic
// also carries per docs/PEDESTRIAN-DESIGN.md §7 ("Regime transitions = lifecycle events"). Kept
// deliberately small (CLAUDE.md P3-1 task note): DemoteToActivityTimeline also covers the ONE-TIME
// PathArc -> ActivityTimeline switch a lively ped's spawn emits (PedLodManager.AddPedLively) -- there is
// no separate "PromoteToActivityTimeline" kind because that switch is never itself a demotion FROM
// FreeKinematic; consumers reconstruct purely from the switch's `To` model (see
// Sim.Pedestrians.Lod.HeadlessIg.Apply(DrSwitchEvent), which never reads `From` either).
public enum PedLifecycleKind
{
    Spawn,
    Despawn,
    PromoteToFreeKinematic,
    DemoteToPathArc,
    DemoteToActivityTimeline,
}

public readonly struct PedLifecycleRecord
{
    public PedLifecycleRecord(VehicleHandle handle, PedLifecycleKind kind, double time)
    {
        Handle = handle;
        Kind = kind;
        Time = time;
    }

    public VehicleHandle Handle { get; }
    public PedLifecycleKind Kind { get; }
    public double Time { get; }
}

// Transport-neutral SEND contract for the pedestrian stream (mirrors IReplicationSink).
public interface IPedReplicationSink : IDisposable
{
    // Volatile, per-step: the high-power (FreeKinematic) population's positions/velocities, quantized
    // int32-cm on the wire via FrameCodec.WritePedFreeKinematicFrame.
    void PublishCrowdFrame(uint step, float time, ReadOnlySpan<PedFreeKinematicRecord> records);

    // Durable/transient-local, sent ONCE per PathArc leg (spawn + every demotion): serialized via
    // FrameCodec.WritePathArcFrame.
    void PublishPathArc(in PathArcRecord record);

    // Durable/transient-local, sent ONCE per ActivityTimeline leg: OPAQUE bytes (already encoded by
    // Sim.Pedestrians.Lod.ActivityTimelineWire.Encode) -- Sim.Replication never interprets them, only
    // tags them with the owning ped's handle.
    void PublishActivityTimeline(VehicleHandle handle, ReadOnlySpan<byte> timelineBytes);

    // Durable/keyed: spawn/despawn + DR-model-switch lifecycle events.
    void PublishPedLifecycle(in PedLifecycleRecord record);
}

// Transport-neutral RECEIVE contract (mirrors IReplicationSource's Pump-then-read discipline).
public interface IPedReplicationSource : IDisposable
{
    void Pump();

    // The most recently DECODED crowd frame only -- a receiver reconstructing a FreeKinematic ped needs
    // just the newest sample plus its own last-applied state (HeadlessIg.Reconstruct's linear
    // extrapolation), exactly like the vehicle stack's DR model, so older frames are not retained.
    uint LatestCrowdStep { get; }
    float LatestCrowdTime { get; }
    IReadOnlyList<PedFreeKinematicRecord> LatestCrowdFrame { get; }

    // Every PathArcRecord decoded so far, in arrival order (a ped demoting mid-run adds a second entry
    // for the same handle with a freshly re-routed path).
    IReadOnlyList<PathArcRecord> PathArcs { get; }

    // Every ActivityTimeline blob decoded so far, in arrival order -- still opaque bytes; only the
    // Sim.Pedestrians bridge (which owns ActivityTimelineWire) can decode them into an ActivityTimeline.
    IReadOnlyList<(VehicleHandle Handle, byte[] TimelineBytes)> ActivityTimelines { get; }

    // Every lifecycle record decoded so far, in arrival order.
    IReadOnlyList<PedLifecycleRecord> Lifecycles { get; }
}

// Byte-loopback InMemory binding (P3-1). UNLIKE InMemoryReplicationBus (the vehicle analog in
// InMemoryReplication.cs), which queues plain structs straight across with no codec at all (there is no
// wire to cross in-process), this bus genuinely serializes every publish call to a byte[] tagged with a
// topic and DESERIALIZES it back on Pump() -- so the hermetic round-trip test actually exercises the
// wire codecs (int32-cm quantization via FrameCodec, the ActivityTimeline double-precision format) end
// to end, proving server==IG survives serialization rather than merely an in-process struct hand-off.
public sealed class InMemoryPedReplicationBus
{
    private enum Topic
    {
        CrowdFrame,
        PathArc,
        ActivityTimeline,
        Lifecycle,
    }

    private readonly struct Entry
    {
        // `bytes` is a POOLED buffer that may be LONGER than the payload -- `Length` is the authority. Only
        // the ActivityTimeline branch actually needs it (its payload is sliced by length rather than read
        // through a self-describing header), but carrying it makes every branch's intent explicit.
        public Entry(Topic topic, byte[] bytes, int length)
        {
            Topic = topic;
            Bytes = bytes;
            Length = length;
        }

        public Topic Topic { get; }
        public byte[] Bytes { get; }
        public int Length { get; }
    }

    // docs/LIVE-CITY-THREADED-TICK-DESIGN.md §4 hazard 1 / §6 Stage 3. Two changes, same motivation as the
    // vehicle bus in InMemoryReplication.cs:
    //
    //   THREAD SAFETY -- this was a plain `Queue<Entry>`, so once the sim ticks on a producer thread while
    //   the render thread Pumps, it would corrupt. Concurrent now. Everything PumpCore mutates
    //   (`_latestCrowdFrame`, `_pathArcs`, `_timelines`, `_lifecycles`) is consumer-thread-only, so this
    //   queue is the whole cross-thread surface.
    //
    //   ALLOCATION -- every publish allocated a fresh `byte[]`, once per ped batch per step. This bus
    //   deliberately round-trips through the real wire codecs (that is its stated purpose, see the type's
    //   own remarks), so the bytes stay; they are just RECYCLED. PumpCore decodes each payload into retained
    //   objects and then has no further use for the buffer, so it returns it.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _queue = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _bufferPool = new();

    // Diagnostics: a count that keeps climbing after warmup means the pool is being defeated (nothing is
    // pumping, so nothing is returned) -- visible rather than guessed at.
    private int _buffersAllocated;
    public int BuffersAllocated => System.Threading.Volatile.Read(ref _buffersAllocated);
    public int PendingEntries => _queue.Count;

    private byte[] Rent(int length)
    {
        if (_bufferPool.TryDequeue(out var buf) && buf.Length >= length)
        {
            return buf;
        }

        System.Threading.Interlocked.Increment(ref _buffersAllocated);
        return new byte[Math.Max(length, 256)];
    }

    private void Return(byte[] buf)
    {
        // Bounded, so a consumer that stops pumping cannot turn the pool into the leak.
        if (_bufferPool.Count < 16)
        {
            _bufferPool.Enqueue(buf);
        }
    }

    private uint _latestCrowdStep;
    private float _latestCrowdTime;
    private PedFreeKinematicRecord[] _latestCrowdFrame = Array.Empty<PedFreeKinematicRecord>();
    private readonly List<PathArcRecord> _pathArcs = new();
    private readonly List<(VehicleHandle Handle, byte[] TimelineBytes)> _timelines = new();
    private readonly List<PedLifecycleRecord> _lifecycles = new();

    public InMemoryPedReplicationBus()
    {
        Sink = new SinkImpl(this);
        Source = new SourceImpl(this);
    }

    public IPedReplicationSink Sink { get; }
    public IPedReplicationSource Source { get; }

    private sealed class SinkImpl : IPedReplicationSink
    {
        private readonly InMemoryPedReplicationBus _bus;
        public SinkImpl(InMemoryPedReplicationBus bus) => _bus = bus;

        public void PublishCrowdFrame(uint step, float time, ReadOnlySpan<PedFreeKinematicRecord> records)
        {
            var size = FrameCodec.PedFreeKinematicFrameSize(records.Length);
            var bytes = _bus.Rent(size);
            FrameCodec.WritePedFreeKinematicFrame(bytes, step, time, records);
            _bus._queue.Enqueue(new Entry(Topic.CrowdFrame, bytes, size));
        }

        public void PublishPathArc(in PathArcRecord record)
        {
            var recs = new[] { record };
            var size = FrameCodec.PathArcFrameSize(recs);
            var bytes = _bus.Rent(size);
            FrameCodec.WritePathArcFrame(bytes, step: 0, time: 0f, recs);
            _bus._queue.Enqueue(new Entry(Topic.PathArc, bytes, size));
        }

        public void PublishActivityTimeline(VehicleHandle handle, ReadOnlySpan<byte> timelineBytes)
        {
            // Sim.Replication has no dedicated ActivityTimeline wire header (the payload IS already the
            // ActivityTimelineWire-encoded blob) -- this bus only needs to know WHICH ped it belongs to,
            // so it prefixes the opaque payload with the handle (index + generation), mirroring the
            // handle-first layout every other wire record in FrameCodec.cs uses.
            var size = 6 + timelineBytes.Length;
            var bytes = _bus.Rent(size);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), handle.Index);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), handle.Generation);
            timelineBytes.CopyTo(bytes.AsSpan(6, timelineBytes.Length));
            _bus._queue.Enqueue(new Entry(Topic.ActivityTimeline, bytes, size));
        }

        public void PublishPedLifecycle(in PedLifecycleRecord record)
        {
            // index(4) + generation(2) + kind(1) + time(8), little-endian -- mirrors the vehicle stack's
            // LifecycleRecord in spirit (IReplication.cs), which similarly has no FrameCodec entry of its
            // own since it is a low-rate keyed event, not a per-frame mover record.
            var bytes = _bus.Rent(15);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), record.Handle.Index);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), record.Handle.Generation);
            bytes[6] = (byte)record.Kind;
            WriteF64(bytes.AsSpan(7, 8), record.Time);
            _bus._queue.Enqueue(new Entry(Topic.Lifecycle, bytes, 15));
        }

        public void Dispose()
        {
        }
    }

    private sealed class SourceImpl : IPedReplicationSource
    {
        private readonly InMemoryPedReplicationBus _bus;
        public SourceImpl(InMemoryPedReplicationBus bus) => _bus = bus;

        public void Pump() => _bus.PumpCore();

        public uint LatestCrowdStep => _bus._latestCrowdStep;
        public float LatestCrowdTime => _bus._latestCrowdTime;
        public IReadOnlyList<PedFreeKinematicRecord> LatestCrowdFrame => _bus._latestCrowdFrame;
        public IReadOnlyList<PathArcRecord> PathArcs => _bus._pathArcs;
        public IReadOnlyList<(VehicleHandle Handle, byte[] TimelineBytes)> ActivityTimelines => _bus._timelines;
        public IReadOnlyList<PedLifecycleRecord> Lifecycles => _bus._lifecycles;

        public void Dispose()
        {
        }
    }

    private void PumpCore()
    {
        while (_queue.TryDequeue(out var e))
        {
            switch (e.Topic)
            {
                case Topic.CrowdFrame:
                    var header = FrameCodec.ReadHeader(e.Bytes);
                    var frame = new PedFreeKinematicRecord[header.Count];
                    FrameCodec.ReadPedFreeKinematicFrame(e.Bytes, frame);
                    _latestCrowdStep = header.Step;
                    _latestCrowdTime = header.Time;
                    _latestCrowdFrame = frame;
                    break;

                case Topic.PathArc:
                    var recs = FrameCodec.ReadPathArcFrame(e.Bytes);
                    _pathArcs.AddRange(recs);
                    break;

                case Topic.ActivityTimeline:
                    var index = BinaryPrimitives.ReadUInt32LittleEndian(e.Bytes.AsSpan(0, 4));
                    var gen = BinaryPrimitives.ReadUInt16LittleEndian(e.Bytes.AsSpan(4, 2));
                    // Sliced by the entry's own LENGTH, not to the end of the buffer: a pooled buffer can be
                    // longer than the payload, and the trailing slack is stale bytes from a previous publish.
                    // This copy is retained by `_timelines`, so it must be exact -- and because it IS a copy,
                    // the pooled buffer is free to be recycled below.
                    var payload = e.Bytes.AsSpan(6, e.Length - 6).ToArray();
                    _timelines.Add((new VehicleHandle(index, gen), payload));
                    break;

                case Topic.Lifecycle:
                    var lcIndex = BinaryPrimitives.ReadUInt32LittleEndian(e.Bytes.AsSpan(0, 4));
                    var lcGen = BinaryPrimitives.ReadUInt16LittleEndian(e.Bytes.AsSpan(4, 2));
                    var kind = (PedLifecycleKind)e.Bytes[6];
                    var time = ReadF64(e.Bytes.AsSpan(7, 8));
                    _lifecycles.Add(new PedLifecycleRecord(new VehicleHandle(lcIndex, lcGen), kind, time));
                    break;
            }

            // Every branch above decoded the payload into retained OBJECTS (records, or an exact-length copy
            // for the timeline blob), so nothing references the buffer any more -- recycle it.
            Return(e.Bytes);
        }
    }

    // double <-> LE bytes via long bits (BinaryPrimitives.Write/ReadDoubleLittleEndian is net5+, absent
    // on netstandard2.1 -- same reasoning as FrameCodec's WriteF32/ReadF32).
    private static void WriteF64(Span<byte> dst, double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(dst, BitConverter.DoubleToInt64Bits(value));

    private static double ReadF64(ReadOnlySpan<byte> src) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(src));
}
