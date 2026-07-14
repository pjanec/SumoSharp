using Sim.Core;
using Sim.Ingest;

namespace Sim.Viewer.Core;

// docs/SUMOSHARP-NATIVE-VIEWER.md P0: the render-agnostic engine driver for the native viewer, lifted
// from Sim.LiveHost/SimHost.cs (SUMOSHARP-API.md §11) with the web/JSON/ImGui plumbing stripped out. Owns
// one Engine driven by a SimulationRunner, and the parsed NetworkModel the renderer needs for road
// geometry. Two modes, auto-selected from the input path exactly like SimHost:
//   * SCENARIO mode -- the input dir has a *.rou.xml AND a *.sumocfg (a committed scenario dir):
//     Engine.LoadScenario drives the scenario's OWN demand.
//   * SANDBOX mode -- a bare net.net.xml (no demand): Engine.LoadNetwork + a runtime random-traffic
//     spawner keeps the roads busy so a bare net still shows traffic.
public sealed class EngineHost : IDisposable
{
    private readonly string _netPath;
    private readonly string? _rouPath;
    private readonly string? _cfgPath;
    private readonly bool _scenarioMode;
    private readonly string[] _normalEdges;

    // Native-viewer perf pass (docs/SUMOSHARP-NATIVE-VIEWER-TESTING.md TASK 1): the sandbox random-traffic
    // spawner's concurrency ceiling. Was a hardcoded 80 (a demo-sized fleet); now the `--fleet N` CLI raises
    // it so a large grid net (e.g. scenarios/_bench/city-15000) can be filled to ~10k for the 60 fps pass.
    private readonly int _spawnCap;
    // Per-timer-fire spawn batch: 1 preserves the original demo cadence for small fleets; a large fleet needs
    // a batch so a warm-up (and replenishment of despawned vehicles) reaches the cap in seconds, not minutes.
    private readonly int _spawnBatch;

    // P1: the runner is rebuilt in place on Restart() (SimHost's BuildSim pattern), so every
    // cross-thread read/rebuild of _engine/_runner is guarded by this lock exactly like SimHost.
    private readonly object _lock = new();
    private Engine _engine = null!;
    private SimulationRunner _runner = null!;
    private VTypeHandle _vType;
    private VTypeHandle _truckType;
    private Random _rng = new(12345);

    // P1: injected obstacle world-points, for the renderer's red-X marker -- mirrors SimHost's
    // _obsLock/_obstacles split (obstacle bookkeeping is independent of the engine rebuild lock).
    private readonly object _obsLock = new();
    private readonly List<(double X, double Y)> _obstacles = new();

    private Timer? _spawnTimer;
    private volatile bool _randomTraffic;

    // Live sim-rate knob (native-viewer controls panel). The SimulationRunner ticks at BaseTickHz wall-clock
    // and each tick Steps one step-length (1s) of sim time, so "sim steps per second" == effective ticks/s ==
    // BaseTickHz * SpeedMultiplier. We drive SpeedMultiplier (a public, per-tick-read runner knob) rather than
    // restarting the runner, so the rate changes live without resetting the sim. Remembered here so Restart()
    // (which rebuilds the runner) can re-apply it.
    private const double BaseTickHz = 10.0;
    private double _simStepsPerSecond = BaseTickHz;

    public NetworkModel Network { get; }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }

    public bool ScenarioMode => _scenarioMode;

    public SimulationSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _runner.Snapshot;
            }
        }
    }

    // The two latest published frames as an ATOMIC pair (both read under one lock), for render-behind
    // interpolation in the viewer -- reading Snapshot and PreviousSnapshot separately could straddle a tick
    // and pair frames from different moments. Cur is the newest; Prev is the one before (== Cur on the very
    // first frame, before two exist). The pool is sized (EnableSnapshotPool below) so both stay valid for a
    // render frame even if the sim ticks once meanwhile.
    public (SimulationSnapshot Cur, SimulationSnapshot Prev) SnapshotPair
    {
        get
        {
            lock (_lock)
            {
                return (_runner.Snapshot, _runner.PreviousSnapshot);
            }
        }
    }

    // Current state of the runtime random-traffic spawner, for the ImGui checkbox binding.
    public bool RandomTraffic => _randomTraffic;

    // Turn the runtime random-traffic spawner on/off (independent of mode) -- SimHost's SetRandomTraffic.
    public void SetRandomTraffic(bool on) => _randomTraffic = on;

    // Current live sim rate in Steps (== 1s of sim time each) per wall-clock second, for the UI slider.
    public double SimStepsPerSecond => _simStepsPerSecond;

    // Set the live sim rate (Steps per wall-clock second). Applied immediately to the running runner via
    // SpeedMultiplier and remembered so a Restart()'s fresh runner picks up the same rate. Clamped to a sane
    // positive range so the slider can't stall (0) or ask for an absurd catch-up.
    public void SetSimStepsPerSecond(double stepsPerSecond)
    {
        _simStepsPerSecond = Math.Clamp(stepsPerSecond, 0.5, 60.0);
        lock (_lock)
        {
            _runner.SpeedMultiplier = _simStepsPerSecond / BaseTickHz;
        }
    }

    // Thread-safe snapshot of injected obstacle world-points, for the renderer's red-X marker.
    public IReadOnlyList<(double X, double Y)> ObstaclePoints
    {
        get
        {
            lock (_obsLock)
            {
                return _obstacles.ToArray();
            }
        }
    }

    // `spawnCap` is the sandbox random-traffic concurrency ceiling (default 80 = the original demo fleet;
    // `--fleet N` passes a large value for the 10k perf pass). `forceSandbox` ignores an adjacent
    // rou.rou.xml/.sumocfg and drives the net as a random-traffic sandbox even inside a committed scenario
    // dir -- the perf pass points at scenarios/_bench/city-15000 (a large grid) purely for its geometry and
    // fills it with a controllable `--fleet` count rather than replaying that scenario's fixed demand.
    public EngineHost(string netPath, int spawnCap = 80, bool forceSandbox = false)
    {
        _netPath = netPath;
        Network = NetworkParser.Parse(netPath);
        _normalEdges = Network.EdgesById.Keys.Where(e => !e.StartsWith(':')).ToArray();

        // Scenario mode iff the net sits beside a rou.rou.xml AND a .sumocfg (a committed scenario dir) --
        // same detection SimHost uses. `forceSandbox` overrides this to sandbox regardless (see ctor doc).
        var dir = Path.GetDirectoryName(Path.GetFullPath(netPath));
        if (dir is not null && !forceSandbox)
        {
            _rouPath = Directory.EnumerateFiles(dir, "*.rou.xml").FirstOrDefault();
            _cfgPath = Directory.EnumerateFiles(dir, "*.sumocfg").FirstOrDefault();
        }

        _scenarioMode = _rouPath is not null && _cfgPath is not null;
        _randomTraffic = !_scenarioMode; // sandbox: random traffic is the traffic; scenario: off by default

        _spawnCap = spawnCap;
        // A big fleet needs a batched replenish (spawnCap/50, min 1) so warm-up/top-up reaches the cap in a
        // few timer fires; the default 80-vehicle demo keeps the original one-at-a-time cadence.
        _spawnBatch = spawnCap > 80 ? Math.Max(1, spawnCap / 50) : 1;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var lane in Network.LanesByHandle)
        {
            foreach (var (x, y) in lane.Shape)
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;

        BuildSim();

        // Front-load a burst of spawn attempts in sandbox mode so the network already has traffic on it
        // from the very first published Snapshot, instead of relying solely on the periodic wall-clock
        // Timer below. SimHost's browser demo runs for minutes, so waiting out the timer's 500ms dueTime
        // is invisible there; a short-lived headless run (e.g. the P0 Xvfb screenshot recipe, a few
        // hundred milliseconds to a few seconds of real wall time) can otherwise race the Timer and finish
        // before it has fired even once, or fire only once or twice, leaving the roads empty or sparse.
        // SpawnOne() itself gates on `_randomTraffic` (false in scenario mode, so this is a no-op there)
        // and each call is independently queued via SimulationRunner.Post, applied at the next Tick
        // boundary exactly like the timer's own calls -- purely additive, same code path.
        if (_randomTraffic)
        {
            // Front-load ~one burst per cap slot. All these posts queue before the first tick advances the
            // count, so the cap gate can't yet throttle them -- posting exactly `_spawnCap` (not more) keeps
            // the initial fill from overshooting the target fleet; routing failures leave it slightly under
            // and the batched timer below tops it up to the cap.
            var burst = _spawnCap > 80 ? _spawnCap : 60;
            for (var i = 0; i < burst; i++)
            {
                SpawnOne();
            }
        }

        // Keep replenishing traffic thereafter (vehicles that reach their destination despawn). Fires a
        // batch of `_spawnBatch` attempts (1 for the small demo, spawnCap/50 for a large fleet).
        _spawnTimer = new Timer(
            _ => { for (var i = 0; i < _spawnBatch; i++) SpawnOne(); },
            null, dueTime: 500, period: 900);
    }

    // (Re)build the engine + runner from scratch, at t=0 -- SimHost's BuildSim pattern. Under _lock so
    // no frame/spawn/obstacle-inject races the swap; the old runner is disposed once swapped out.
    private void BuildSim()
    {
        lock (_lock)
        {
            var old = _runner;

            var engine = new Engine();
            if (_scenarioMode)
            {
                engine.LoadScenario(_netPath, _rouPath!, _cfgPath!); // drives the scenario's OWN demand
            }
            else
            {
                engine.LoadNetwork(_netPath);
            }

            _vType = engine.DefaultVType;
            // A long vehicle so the random spawner can show swept-path off-tracking ("swing wide") on turns.
            _truckType = engine.DefineVType(new VTypeParams { VClass = "truck", Length = 12.0 });

            var runner = new SimulationRunner(engine);
            // Capacity 4 (was 3): the viewer holds BOTH the newest and previous frame across a render frame
            // for interpolation (SnapshotPair), so the pool needs enough spare buffers that a sim tick (or
            // two, at a high sim rate + a slow render frame) can't recycle the buffer we're still reading.
            runner.EnableSnapshotPool(capacity: 4);
            // Local mode has no dead reckoning to smooth over sparse updates, so a higher rate than the web
            // demo's 2 Hz is fine -- the renderer draws the authoritative Snapshot every frame. The live
            // sim-rate slider drives SpeedMultiplier on top of this base (re-applied here so a Restart keeps
            // the user's chosen rate).
            runner.Start(targetHz: BaseTickHz);
            runner.SpeedMultiplier = _simStepsPerSecond / BaseTickHz;

            _engine = engine;
            _runner = runner;
            _rng = new Random(12345);

            old?.Dispose();
        }

        lock (_obsLock)
        {
            _obstacles.Clear();
        }
    }

    // Rebuild the sim from t=0 (re-queues the scenario demand / empties the sandbox). Obstacles cleared.
    public void Restart() => BuildSim();

    // A world-point click (already WORLD coordinates) -> project to the nearest lane and inject a
    // full-lane obstacle; vehicles queue behind it. Ignored if the click is far from any lane. Ported
    // from Sim.LiveHost/SimHost.cs's InjectObstacleAtWorld.
    public void InjectObstacleAtWorld(double wx, double wy)
    {
        if (!TryProjectToLane(wx, wy, out var laneId, out var pos, out var sx, out var sy, out var dist)
            || dist > 15.0)
        {
            return;
        }

        SimulationRunner runner;
        lock (_lock)
        {
            runner = _runner;
        }

        try
        {
            runner.Invoke(e => e.AddObstacle(e.GetLane(laneId), frontPos: pos, length: 2.0));
        }
        catch
        {
            return; // runner disposed mid-restart -> drop this click
        }

        lock (_obsLock)
        {
            _obstacles.Add((sx, sy));
        }
    }

    public void ClearObstacles()
    {
        SimulationRunner runner;
        lock (_lock)
        {
            runner = _runner;
        }

        try
        {
            runner.Invoke<object?>(e => { e.ClearObstacles(); return null; });
        }
        catch
        {
            // runner disposed mid-restart -> the rebuild already cleared obstacles
        }

        lock (_obsLock)
        {
            _obstacles.Clear();
        }
    }

    // Nearest lane to a world point, plus the along-lane position and the projected point. Ported from
    // Sim.LiveHost/SimHost.cs's TryProjectToLane; Network is parsed once and never mutated across a
    // restart, so this needs no lock.
    private bool TryProjectToLane(double wx, double wy,
        out string laneId, out double pos, out double sx, out double sy, out double dist)
    {
        laneId = string.Empty;
        pos = 0.0;
        sx = 0.0;
        sy = 0.0;
        dist = double.PositiveInfinity;
        var bestD2 = double.PositiveInfinity;

        foreach (var lane in Network.LanesByHandle)
        {
            var shape = lane.Shape;
            if (shape.Count < 2)
            {
                continue;
            }

            var acc = 0.0;
            for (var i = 0; i < shape.Count - 1; i++)
            {
                var (px, py) = shape[i];
                var (qx, qy) = shape[i + 1];
                var dx = qx - px;
                var dy = qy - py;
                var segLen2 = dx * dx + dy * dy;
                var t = segLen2 > 0 ? Math.Clamp(((wx - px) * dx + (wy - py) * dy) / segLen2, 0.0, 1.0) : 0.0;
                var cx = px + dx * t;
                var cy = py + dy * t;
                var d2 = (wx - cx) * (wx - cx) + (wy - cy) * (wy - cy);
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    laneId = lane.Id;
                    pos = acc + t * Math.Sqrt(segLen2);
                    sx = cx;
                    sy = cy;
                }

                acc += Math.Sqrt(segLen2);
            }
        }

        dist = Math.Sqrt(bestD2);
        return !double.IsPositiveInfinity(bestD2);
    }

    private void SpawnOne()
    {
        if (!_randomTraffic || _normalEdges.Length < 2)
        {
            return;
        }

        lock (_lock)
        {
            if (_runner.Snapshot.Count > _spawnCap)
            {
                return;
            }

            var from = _normalEdges[_rng.Next(_normalEdges.Length)];
            var to = _normalEdges[_rng.Next(_normalEdges.Length)];
            if (from == to)
            {
                return;
            }

            var vt = _rng.Next(3) == 0 ? _truckType : _vType; // ~1/3 trucks, to show off-tracking on turns
            _runner.Post(e =>
            {
                try
                {
                    e.SpawnVehicle(vt, from, to, departPos: 0.0, departSpeed: 0.0, departLane: 0);
                }
                catch
                {
                    // No route between this random pair -> skip (the next tick tries a fresh pair).
                }
            });
        }
    }

    public void Dispose()
    {
        _spawnTimer?.Dispose();
        lock (_lock)
        {
            _runner?.Dispose();
        }
    }
}
