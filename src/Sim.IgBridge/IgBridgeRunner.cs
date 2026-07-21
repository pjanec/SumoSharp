using System.Globalization;
using Sim.Core;
using Sim.Ingest;
using Sim.Replication;

namespace Sim.IgBridge;

// Immutable run configuration. Deterministic by construction: the emitted motion is a pure function of
// (net, demand, step length, seed) -- no wall clock, no System.Random (the engine's dawdle RNG is
// per-entity seeded off Engine.Seed).
public sealed class IgBridgeConfig
{
    public IgBridgeConfig(string netXmlPath, string rouXmlPath)
    {
        NetXmlPath = netXmlPath;
        RouXmlPath = rouXmlPath;
    }

    public string NetXmlPath { get; }
    public string RouXmlPath { get; }
    public double StepLength { get; init; } = 0.1;   // 10 Hz core (docs/IGBRIDGE-DECISIONS.md §1 Q4/R1)
    public int Seed { get; init; } = 42;
    public int HistoryCapacity { get; init; } = 8;    // per-vehicle DR ring depth
}

public readonly struct SpawnInfo
{
    public SpawnInfo(VehicleHandle handle, string id, IgEntityModel model)
    {
        Handle = handle;
        Id = id;
        Model = model;
    }

    public VehicleHandle Handle { get; }
    public string Id { get; }
    public IgEntityModel Model { get; }
}

public readonly struct DespawnInfo
{
    public DespawnInfo(VehicleHandle handle, string id)
    {
        Handle = handle;
        Id = id;
    }

    public VehicleHandle Handle { get; }
    public string Id { get; }
}

// Stage [1]/[2] of the pipeline (docs/IGBRIDGE-DECISIONS.md §2): the fixed-10 Hz core loop + per-entity
// ring buffers. Loads the box network demand-less (Sim.Ingest.DemandParser rejects the box demand's
// departPos="base"/parking stops -- see RouteDemand), replays the real routes via explicit-edge
// SpawnVehicle, and each 0.1 s Tick() appends one TimestampedSample per active vehicle to that vehicle's
// VehicleSampleHistory. Vehicle lifecycle (spawn/despawn) is detected by first/last presence in the
// engine's active set and surfaced for the emit stage (T1.2). Reconstruction/emit are NOT here -- this
// stage only produces the buffered sample source DrClock.ResolveAt consumes.
public sealed class IgBridgeRunner
{
    private readonly Engine _engine;
    private readonly VTypeHandle _vtype;
    private readonly IReadOnlyList<RouteDemandEntry> _demand;
    private readonly int _historyCapacity;

    private readonly Dictionary<VehicleHandle, VehicleSampleHistory> _histories = new();
    private readonly Dictionary<VehicleHandle, string> _idByHandle = new();
    private readonly List<SpawnInfo> _spawnedThisTick = new();
    private readonly List<DespawnInfo> _despawnedThisTick = new();
    private HashSet<VehicleHandle> _live = new();
    private int _cursor;

    public IgBridgeRunner(IgBridgeConfig config)
    {
        Network = NetworkParser.Parse(config.NetXmlPath);
        Lanes = new NetworkLaneSource(Network);
        _historyCapacity = config.HistoryCapacity;

        _engine = new Engine { Seed = (ulong)config.Seed };
        // StepLength ONLY comes from the config's <step-length>; teleport OFF and sigma=0 for a clean,
        // deterministic motion source (matches the --live-city golden's demand-less setup).
        var step = config.StepLength.ToString(CultureInfo.InvariantCulture);
        var xml =
            "<configuration>"
            + "<time><begin value=\"0\"/><end value=\"1000000000\"/><step-length value=\"" + step + "\"/></time>"
            + "<processing><time-to-teleport value=\"-1\"/><default.speeddev value=\"0.0\"/></processing>"
            + "</configuration>";
        _engine.LoadNetwork(config.NetXmlPath, ScenarioConfigParser.ParseXml(xml));
        _vtype = _engine.DefineVType(new VTypeParams { VClass = "passenger", Sigma = 0.0 });
        _demand = RouteDemand.Parse(config.RouXmlPath);
    }

    public NetworkModel Network { get; }
    public NetworkLaneSource Lanes { get; }
    public double SimTime => _engine.CurrentTime;
    public int StepCount => _engine.StepCount;
    public int PendingDemand => _demand.Count - _cursor;

    public IReadOnlyDictionary<VehicleHandle, VehicleSampleHistory> VehicleHistories => _histories;
    public IReadOnlyCollection<VehicleHandle> LiveVehicles => _live;
    public IReadOnlyList<SpawnInfo> SpawnedThisTick => _spawnedThisTick;
    public IReadOnlyList<DespawnInfo> DespawnedThisTick => _despawnedThisTick;

    public string IdOf(VehicleHandle handle)
        => _idByHandle.TryGetValue(handle, out var id) ? id : handle.ToString();

    // Advance exactly one fixed 0.1 s tick: spawn everything due, Step() once, append per-vehicle
    // samples, and diff the active set for spawn/despawn lifecycle. Order matches the golden loop
    // (spawn-before-step); the sim clock advances by exactly StepLength (no wall-clock coupling).
    public void Tick()
    {
        _spawnedThisTick.Clear();
        _despawnedThisTick.Clear();

        // 1) spawn all vehicles whose depart time has arrived (demand is depart-sorted).
        var now = _engine.CurrentTime;
        while (_cursor < _demand.Count && _demand[_cursor].Depart <= now)
        {
            var entry = _demand[_cursor++];
            VehicleHandle handle;
            try
            {
                handle = _engine.SpawnVehicle(_vtype, entry.Edges, departPos: 0.0, departSpeed: 0.0,
                    departBestLane: true);
            }
            catch (Exception)
            {
                // Unroutable/invalid edge in the demand -> skip (the scenario runs with
                // ignore-route-errors; IgBridge mirrors that leniency rather than aborting the run).
                continue;
            }

            _idByHandle[handle] = "v" + entry.Id;
        }

        // 2) one fixed tick.
        _engine.Step();

        // 3) append one sample per ACTIVE vehicle; first appearance == spawn.
        var current = new HashSet<VehicleHandle>();
        var handles = _engine.VehicleHandles;
        var laneHandles = _engine.LaneHandles;
        var pos = _engine.Pos;
        var posLat = _engine.PosLat;
        var speed = _engine.Speed;
        var accel = _engine.Acceleration;
        var drModels = _engine.DrModels;
        var t = _engine.CurrentTime;
        Span<int> upcoming = stackalloc int[UpcomingLanes.Count];

        for (var i = 0; i < handles.Length; i++)
        {
            var handle = handles[i];
            current.Add(handle);

            if (!_histories.TryGetValue(handle, out var history))
            {
                history = new VehicleSampleHistory(_historyCapacity);
                _histories[handle] = history;
                _spawnedThisTick.Add(new SpawnInfo(handle, IdOf(handle), IgEntityModel.Car));
            }

            var n = _engine.GetUpcomingLanes(handle, upcoming);
            var record = new VehicleRecord(
                handle, (DrModel)drModels[i], laneHandles[i], pos[i], posLat[i], speed[i], accel[i],
                latSpeed: 0.0, new UpcomingLanes(upcoming[..n]));
            history.Append(new TimestampedSample(t, record));
        }

        // 4) despawn: present last tick, gone now.
        foreach (var handle in _live)
        {
            if (!current.Contains(handle))
            {
                _despawnedThisTick.Add(new DespawnInfo(handle, IdOf(handle)));
            }
        }

        _live = current;
    }
}
