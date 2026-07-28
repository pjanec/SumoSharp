using System.Threading;
using Sim.Core;

namespace Sim.Replication;

// docs/SUMOSHARP-PACKAGING-DESIGN.md P1 (D8) — a same-process, non-DDS binding of IReplicationSink /
// IReplicationSource, proving the contract in IReplication.cs is transport-neutral rather than an
// after-the-fact abstraction over DDS. No codec, no bytes: records are queued and handed straight
// across, since there is no wire to cross in-process. A publisher writes through Sink; a consumer reads
// through Source after calling Pump() to drain the queue -- mirroring DdsSubscriber's own Pump-then-read
// pattern so a caller coded against the interfaces cannot tell which binding it holds.
public sealed class InMemoryReplicationBus
{
    private const int HistoryCapacity = 8;

    // Discriminated queue entry -- Kind selects which payload field is meaningful. A plain struct (not a
    // closure/delegate) keeps this allocation-light per publish call.
    private enum EntryKind { Geometry, Lifecycle, Frame, TrafficLights }

    private readonly struct Entry
    {
        public Entry(IReadOnlyList<GeometryCodec.LaneGeo> lanes)
        { Kind = EntryKind.Geometry; Lanes = lanes; Lifecycle = default; Movers = default; Lights = default; Step = 0; Time = 0; MoverCount = 0; }

        public Entry(in LifecycleRecord lifecycle)
        { Kind = EntryKind.Lifecycle; Lanes = default; Lifecycle = lifecycle; Movers = default; Lights = default; Step = 0; Time = 0; MoverCount = 0; }

        // `movers` is a POOLED buffer that may be LONGER than the frame -- `MoverCount` is the authority
        // (see the pool's own remarks below). It is returned to the pool by PumpCore once consumed.
        public Entry(uint step, double time, VehicleRecord[] movers, int moverCount)
        { Kind = EntryKind.Frame; Lanes = default; Lifecycle = default; Movers = movers; Lights = default; Step = step; Time = time; MoverCount = moverCount; }

        public Entry(uint step, double time, IReadOnlyList<TlCodec.TlEntry> lights, bool isTl)
        { Kind = EntryKind.TrafficLights; Lanes = default; Lifecycle = default; Movers = default; Lights = lights; Step = step; Time = time; MoverCount = 0; }

        public EntryKind Kind { get; }
        public IReadOnlyList<GeometryCodec.LaneGeo>? Lanes { get; }
        public LifecycleRecord Lifecycle { get; }
        public VehicleRecord[]? Movers { get; }
        public int MoverCount { get; }
        public IReadOnlyList<TlCodec.TlEntry>? Lights { get; }
        public uint Step { get; }
        public double Time { get; }
    }

    // docs/LIVE-CITY-THREADED-TICK-DESIGN.md §4 hazard 1. This was a plain `Queue<Entry>`, so a threaded
    // producer (publishing from a sim thread) racing the consumer's `Pump()` would corrupt it. Concurrent
    // now, which is what makes the Stage-2 producer/consumer split legal: the producer only ever ENQUEUES
    // and the consumer only ever DEQUEUES, and every dictionary PumpCore mutates (`_history`, `_tlState`,
    // `_dims`, `_names`, `_geometry`) is touched exclusively on the consuming thread -- so this one queue
    // is the entire cross-thread surface.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _queue = new();

    // §3/§6 Stage 2: `PublishFrame` used to do `movers.ToArray()` -- a fresh heap array EVERY step, ~360 KB
    // at 5 000 cars. The records are consumed by PumpCore and then never referenced again (they are copied
    // into `_history`), so the buffer can simply be recycled. Concurrent because the producer rents and the
    // consumer returns. Buffers grow only while a frame exceeds every pooled capacity, i.e. during warmup
    // => ZERO steady-state allocation on the car handoff, which is the owner's stated constraint.
    //
    // A rented buffer may be LONGER than the frame, hence Entry.MoverCount rather than Movers.Length.
    private readonly System.Collections.Concurrent.ConcurrentQueue<VehicleRecord[]> _moverPool = new();

    // Diagnostics for a threaded host: how deep the handoff queue got, and how many buffers the pool had to
    // allocate. A steadily-growing `MoverBuffersAllocated` after warmup means the pool is being defeated
    // (consumer not pumping, so nothing is ever returned) -- worth seeing rather than guessing.
    private int _moverBuffersAllocated;
    public int MoverBuffersAllocated => Volatile.Read(ref _moverBuffersAllocated);
    public int PendingEntries => _queue.Count;

    private VehicleRecord[] RentMovers(int length)
    {
        // One probe, not a search: the pool is homogeneous in practice (the frame size barely changes step
        // to step), so a single dequeue either fits or is grown and re-pooled at the larger size.
        if (_moverPool.TryDequeue(out var buf))
        {
            if (buf.Length >= length)
            {
                return buf;
            }
        }

        Interlocked.Increment(ref _moverBuffersAllocated);
        return new VehicleRecord[Math.Max(length, 64)];
    }

    private void ReturnMovers(VehicleRecord[] buf)
    {
        // Bounded, so a consumer that stops pumping cannot make this the leak instead of the queue.
        if (_moverPool.Count < 8)
        {
            _moverPool.Enqueue(buf);
        }
    }

    private readonly Dictionary<int, GeometryCodec.LaneGeo> _geometry = new();
    private bool _geometryComplete;

    private readonly Dictionary<VehicleHandle, VehicleSampleHistory> _history = new();
    private readonly Dictionary<VehicleHandle, (float Length, float Width)> _dims = new();
    private readonly Dictionary<VehicleHandle, string> _names = new();
    private readonly Dictionary<int, byte> _tlState = new();
    private bool _pumpedAfterPublish;

    private sealed class HistoryView : IReadOnlyDictionary<VehicleHandle, IVehicleSampleHistory>
    {
        private readonly Dictionary<VehicleHandle, VehicleSampleHistory> _inner;

        public HistoryView(Dictionary<VehicleHandle, VehicleSampleHistory> inner) => _inner = inner;

        public IVehicleSampleHistory this[VehicleHandle key] => _inner[key];
        public IEnumerable<VehicleHandle> Keys => _inner.Keys;
        public IEnumerable<IVehicleSampleHistory> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(VehicleHandle key) => _inner.ContainsKey(key);

        public bool TryGetValue(VehicleHandle key, out IVehicleSampleHistory value)
        {
            if (_inner.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<VehicleHandle, IVehicleSampleHistory>> GetEnumerator()
        {
            foreach (var kv in _inner)
            {
                yield return new KeyValuePair<VehicleHandle, IVehicleSampleHistory>(kv.Key, kv.Value);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private readonly HistoryView _historyView;

    public InMemoryReplicationBus()
    {
        _historyView = new HistoryView(_history);
        Sink = new SinkImpl(this);
        Source = new SourceImpl(this);
    }

    public IReplicationSink Sink { get; }
    public IReplicationSource Source { get; }

    private sealed class SinkImpl : IReplicationSink
    {
        private readonly InMemoryReplicationBus _bus;
        public SinkImpl(InMemoryReplicationBus bus) => _bus = bus;

        public void PublishGeometry(IReadOnlyList<GeometryCodec.LaneGeo> lanes) =>
            _bus._queue.Enqueue(new Entry(lanes));

        public void PublishLifecycle(in LifecycleRecord record) =>
            _bus._queue.Enqueue(new Entry(record));

        public void PublishFrame(uint step, double time, ReadOnlySpan<VehicleRecord> movers)
        {
            var buf = _bus.RentMovers(movers.Length);
            movers.CopyTo(buf);
            _bus._queue.Enqueue(new Entry(step, time, buf, movers.Length));
        }

        public void PublishTrafficLights(uint step, double time, IReadOnlyList<TlCodec.TlEntry> lights) =>
            _bus._queue.Enqueue(new Entry(step, time, lights, isTl: true));

        public void Dispose() { }
    }

    private sealed class SourceImpl : IReplicationSource
    {
        private readonly InMemoryReplicationBus _bus;
        public SourceImpl(InMemoryReplicationBus bus) => _bus = bus;

        public void Pump() => _bus.PumpCore();

        public IReadOnlyDictionary<int, GeometryCodec.LaneGeo> Geometry => _bus._geometry;
        public bool GeometryComplete => _bus._geometryComplete;
        public IReadOnlyDictionary<VehicleHandle, IVehicleSampleHistory> History => _bus._historyView;
        public IReadOnlyDictionary<VehicleHandle, (float Length, float Width)> Dims => _bus._dims;
        public IReadOnlyDictionary<VehicleHandle, string> Names => _bus._names;
        public IReadOnlyDictionary<int, byte> TlStateByLane => _bus._tlState;
        public double? LatestVehicleSampleTime { get; internal set; }
        public bool Connected => _bus._pumpedAfterPublish;

        public void ResetVehicles()
        {
            _bus._history.Clear();
            _bus._dims.Clear();
            _bus._names.Clear();
            LatestVehicleSampleTime = null;
        }

        public bool TryGetLatest(VehicleHandle handle, out TimestampedSample sample)
        {
            if (_bus._history.TryGetValue(handle, out var hist) && hist.Count > 0)
            {
                sample = hist[hist.Count - 1];
                return true;
            }

            sample = default;
            return false;
        }

        public void Dispose() { }
    }

    private void PumpCore()
    {
        var sawAny = false;
        while (_queue.TryDequeue(out var e))
        {
            sawAny = true;
            switch (e.Kind)
            {
                case EntryKind.Geometry:
                    foreach (var lane in e.Lanes!)
                    {
                        _geometry[lane.Handle] = lane;
                    }

                    _geometryComplete = true;
                    break;

                case EntryKind.Lifecycle:
                    var lc = e.Lifecycle;
                    if (lc.IsSpawn)
                    {
                        _dims[lc.Handle] = (lc.Length, lc.Width);
                        _names[lc.Handle] = lc.Name;
                    }
                    else
                    {
                        _dims.Remove(lc.Handle);
                        _names.Remove(lc.Handle);
                        _history.Remove(lc.Handle);
                    }

                    break;

                case EntryKind.Frame:
                    var movers = e.Movers!;
                    for (var i = 0; i < e.MoverCount; i++)
                    {
                        var rec = movers[i];
                        if (!_history.TryGetValue(rec.Handle, out var hist))
                        {
                            hist = new VehicleSampleHistory(HistoryCapacity);
                            _history[rec.Handle] = hist;
                        }

                        hist.Append(new TimestampedSample(e.Time, rec));
                    }

                    var srcImpl = (SourceImpl)Source;
                    if (srcImpl.LatestVehicleSampleTime is null || e.Time > srcImpl.LatestVehicleSampleTime.Value)
                    {
                        srcImpl.LatestVehicleSampleTime = e.Time;
                    }

                    ReturnMovers(movers); // consumed -- nothing retains it, so recycle it for the producer
                    break;

                case EntryKind.TrafficLights:
                    foreach (var entry in e.Lights!)
                    {
                        _tlState[entry.LaneHandle] = entry.Signal;
                    }

                    break;
            }
        }

        if (sawAny)
        {
            _pumpedAfterPublish = true;
        }
    }
}
