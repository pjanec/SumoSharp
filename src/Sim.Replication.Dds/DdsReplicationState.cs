using Sim.Core;
using Sim.Replication;

namespace Sim.Replication.Dds;

// docs/HROT-INTEGRATION-DESIGN.md H8 "Reuse, not copy": the decode-and-accumulate half of the DDS read side,
// factored out of DdsSubscriber VERBATIM so a second reader (DdsMultiSourceSubscriber) can keep one of these
// per publisher instead of copying the logic. It owns everything a consumer reads through IReplicationSource
// (geometry, per-vehicle history, dims, names, tags, TL table) and knows nothing about DDS readers: the owner
// takes the samples and hands each one to the matching Apply* method. DdsSubscriber owns ONE of these and
// feeds it every sample unfiltered, so its behaviour is byte-for-byte what it was before this file existed.
internal sealed class DdsReplicationState : IReplicationSource
{
    private const int HistoryCap = 8;

    private readonly Dictionary<int, GeometryCodec.LaneGeo> _geometryByHandle = new();
    private readonly HashSet<int> _geometryChunksReceived = new();
    private int _geometryTotalChunks = -1;

    private readonly Dictionary<VehicleHandle, VehicleSampleHistory> _history = new();
    private readonly Dictionary<VehicleHandle, (float Length, float Width)> _dims = new();
    // docs/LIVE-CITY-VIEWERS-DESIGN.md §3.1, -TASKS.md E2: handle->SUMO-id, accumulated from the lifecycle
    // topic's spawn events (Name), removed on despawn -- the "dictionary table sent once" a consumer reads
    // to label a picked vehicle. Never touched by ApplyVehicleFrame (the per-frame path stays untouched).
    private readonly Dictionary<VehicleHandle, string> _names = new();
    // H7: handle->(ExternalType, Color), non-empty tags only, spawn-set / despawn-removed like _names.
    private readonly Dictionary<VehicleHandle, VehicleTag> _tags = new();
    private readonly Dictionary<int, byte> _tlState = new();

    private readonly HistoryView _historyView;

    private readonly byte[] _vehicleBytes = new byte[DdsWire.MaxPayload];
    private readonly VehicleRecord[] _vehicleRecs = new VehicleRecord[2048];
    private readonly byte[] _geometryBytes = new byte[DdsWire.MaxPayload];
    private readonly byte[] _tlBytes = new byte[DdsWire.MaxPayload];
    private readonly bool _stampWithSimTime;

    private readonly Action _pump;
    private readonly Func<bool> _connected;

    // `pump` / `connected` are the OWNER's: a state has no readers of its own, so IReplicationSource.Pump and
    // .Connected forward to whoever does.
    public DdsReplicationState(bool stampWithSimTime, Action pump, Func<bool> connected)
    {
        _stampWithSimTime = stampWithSimTime;
        _pump = pump ?? throw new ArgumentNullException(nameof(pump));
        _connected = connected ?? throw new ArgumentNullException(nameof(connected));
        _historyView = new HistoryView(_history);
    }

    public void Pump() => _pump();

    public bool Connected => _connected();

    public IReadOnlyDictionary<int, GeometryCodec.LaneGeo> Geometry => _geometryByHandle;

    public bool GeometryComplete => _geometryTotalChunks >= 0 && _geometryChunksReceived.Count >= _geometryTotalChunks;

    public IReadOnlyDictionary<VehicleHandle, IVehicleSampleHistory> History => _historyView;

    public IReadOnlyDictionary<VehicleHandle, (float Length, float Width)> Dims => _dims;

    public IReadOnlyDictionary<VehicleHandle, string> Names => _names;

    public IReadOnlyDictionary<VehicleHandle, VehicleTag> Tags => _tags;

    public IReadOnlyDictionary<int, byte> TlStateByLane => _tlState;

    public double? LatestVehicleSampleTime { get; private set; }

    public long TotalVehicleSamplesReceived { get; private set; }

    public void ResetVehicles()
    {
        _history.Clear();
        _dims.Clear();
        _names.Clear();
        _tags.Clear();
        LatestVehicleSampleTime = null;
    }

    public bool TryGetLatest(VehicleHandle handle, out TimestampedSample sample)
    {
        if (_history.TryGetValue(handle, out var hist) && hist.Count > 0)
        {
            sample = hist[^1];
            return true;
        }

        sample = default;
        return false;
    }

    // Reused across calls (the pre-factoring code allocated one per Pump; the contents are cleared per chunk
    // either way, so the decoded lanes are identical).
    private readonly List<GeometryCodec.LaneGeo> _decodedLanes = new();

    public void ApplyGeometry(in DdsNetworkGeometry f)
    {
        var n = f.ReadPayload(_geometryBytes);
        _decodedLanes.Clear();
        GeometryCodec.ReadGeometry(_geometryBytes.AsSpan(0, n), _decodedLanes);
        foreach (var lane in _decodedLanes)
        {
            _geometryByHandle[lane.Handle] = lane;
        }

        _geometryTotalChunks = f.TotalChunks;
        _geometryChunksReceived.Add(f.ChunkIndex);
    }

    public void ApplyLifecycle(in DdsVehicleLifecycle d)
    {
        var handle = new VehicleHandle(d.Index, d.Generation);
        if (d.Event == 0)
        {
            _dims[handle] = (d.Length, d.Width);
            _names[handle] = d.Name.ToString();
            var tag = new VehicleTag(d.ExternalType, d.Color);
            if (!tag.IsEmpty)
            {
                _tags[handle] = tag;
            }
            else
            {
                _tags.Remove(handle);
            }
        }
        else
        {
            _dims.Remove(handle);
            _names.Remove(handle);
            _tags.Remove(handle);
            _history.Remove(handle);
        }
    }

    // `sourceTimestampNs` is the DDS sample's SourceTimestamp (ns since the Unix epoch); see DdsSubscriber's
    // field remark for why wall-clock arrival is the default stamp and H3 for the sim-time alternative.
    public void ApplyVehicleFrame(in DdsWireFrame f, long sourceTimestampNs)
    {
        var n = f.ReadPayload(_vehicleBytes);
        var count = FrameCodec.ReadVehicleFrame(_vehicleBytes.AsSpan(0, n), _vehicleRecs);

        var ts = sourceTimestampNs;
        var timestampSeconds = _stampWithSimTime ? (double)f.Time : ts > 0 ? ts / 1_000_000_000.0 : (double)f.Time;

        if (LatestVehicleSampleTime is null || timestampSeconds > LatestVehicleSampleTime.Value)
        {
            LatestVehicleSampleTime = timestampSeconds;
        }

        for (var i = 0; i < count; i++)
        {
            var rec = _vehicleRecs[i];
            if (!_history.TryGetValue(rec.Handle, out var hist))
            {
                hist = new VehicleSampleHistory(HistoryCap);
                _history[rec.Handle] = hist;
            }

            hist.Append(new TimestampedSample(timestampSeconds, rec));
        }

        TotalVehicleSamplesReceived += count;
    }

    public void ApplyTl(in DdsTlState f)
    {
        var n = f.ReadPayload(_tlBytes);
        var maxEntries = Math.Max(1, (n - TlCodec.HeaderSize) / TlCodec.EntrySize);
        var entries = new TlCodec.TlEntry[maxEntries];
        var count = TlCodec.ReadTl(_tlBytes.AsSpan(0, n), entries);
        for (var i = 0; i < count; i++)
        {
            _tlState[entries[i].LaneHandle] = entries[i].Signal;
        }
    }

    // The state owns no native resource; its owner disposes the readers.
    public void Dispose()
    {
    }

    // Read-only adapter presenting `_history` (concrete VehicleSampleHistory values, needed internally for
    // Append) as the transport-neutral IReadOnlyDictionary<VehicleHandle, IVehicleSampleHistory> a consumer
    // (DrClock, LoopbackSelfTest) sees via `History`. Thin forwarding wrapper -- no copying.
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
}
