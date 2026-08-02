using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Sim.Core;
using Sim.Ingest;
using Sim.LiveCity;
using Sim.Replication;

namespace CityLib;

// docs/LIVE-CITY-VIEWERS-DESIGN.md §6, -TASKS.md Stage D (D1) -- the LOCAL, in-process LIVE-CITY data
// path: wraps a Sim.LiveCity.LiveCitySim (the shared coupled cars+peds+crossing-yield host,
// docs/LIVE-CITY-VIEWERS-DESIGN.md §1) and exposes exactly the read-back surface the Viewer glue needs.
// Mirrors SimSource.cs's shape one-for-one (Tick/Source/LocalLanes/Network/Time) plus the ped read-back
// SimSource has no analog for (Peds). Per the design's "key reuse insight": the CAR render path is
// UNCHANGED from SimSource's -- Source/LocalLanes feed the SAME CityLib.Reconstructor the --scenario path
// uses, so cars get identical DR/kinematic smoothing in live-city mode, no new car-rendering code at all.
public sealed class LiveCitySource : IDisposable
{
    private readonly LiveCitySim _sim;
    private readonly LiveCityConfig _cfg;

    // repoRoot-relative convenience constructor -- LiveCityConfig.ForRepoRoot resolves the pinned
    // scenarios/_ped/demo_city/box dataset dir + the LIVECITY_CARS/LCMIN/YIELD env-var overrides, exactly
    // as SumoSharp.LiveCity's own reference caller does.
    public LiveCitySource(string repoRoot)
        : this(LiveCityConfig.ForRepoRoot(repoRoot))
    {
    }

    public LiveCitySource(LiveCityConfig cfg)
    {
        _cfg = cfg;
        _sim = new LiveCitySim(cfg);

        // HIREALISM-PASSTHROUGH-GATE-DESIGN.md §3.3: in the 3D host the camera-driven LC-realism zone
        // ALSO forbids the ignore-junction-blocker drive-through inside itself (owner: no cars passing
        // through each other on camera, even at the cost of the junction staying blocked; waits keep
        // counting, so the recovery fires the moment the camera moves away). Default ON here -- this
        // ctor runs before the threaded producer starts, so the flag write is safe -- with
        // CITY3D_HIREALISM=0 as the A/B kill switch (docs/ENV-GATES.md).
        _sim.HighRealismFollowsZone = Environment.GetEnvironmentVariable("CITY3D_HIREALISM") != "0";

        // docs/EXTERNAL-NET-VIEWER-DESIGN.md §1 / -TASKS.md T1: the pinned X0..Y1 crop is the DEMO's
        // hero-block (a ~840x840 m window on a 4750 m synthetic net). An arbitrary net -- a SumoData cut
        // sub-area -- has no such window: the whole cut IS the playable area, and LiveCitySim itself
        // already bypasses the crop predicates in RouteGraph mode (`_cropEnabled = !routeGraphMode`).
        // Reporting the demo's pinned rect here anyway would make the viewer build road meshes and frame
        // its camera on a window that, on a Geneva box, is 90 km from any road at all. So in arbitrary-net
        // mode `Crop` is the net's own AABB, which is what "the whole net" means to every consumer of it.
        Crop = cfg.NavMode == PedNavMode.RouteGraph
            ? NetAabb(_sim.Network)
            : (cfg.X0, cfg.Y0, cfg.X1, cfg.Y1);
    }

    // AABB over every parsed lane shape point -- the same definition LiveCitySim.ComputeNetAabbCentre
    // uses for its own realism-pocket default, so the viewer's extent and the sim's centre agree.
    private static (double X0, double Y0, double X1, double Y1) NetAabb(NetworkModel network)
    {
        var x0 = double.PositiveInfinity;
        var y0 = double.PositiveInfinity;
        var x1 = double.NegativeInfinity;
        var y1 = double.NegativeInfinity;

        foreach (var lane in network.LanesById.Values)
        {
            foreach (var (x, y) in lane.Shape)
            {
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }

        return x0 <= x1 ? (x0, y0, x1, y1) : (0.0, 0.0, 0.0, 0.0);
    }

    // The X0/Y0/X1/Y1 crop rectangle LiveCityConfig steps cars/peds within (SUMO metres). `Network`
    // (below) is the FULL parsed net.xml -- e.g. scenarios/_ped/demo_city/box's net.xml spans
    // 4750x4750m, of which the crop is only ~840x840m -- so a caller that wants a legible scene (road
    // meshes + camera framing) must filter/frame to THIS rect, not Network's own (whole-net) bounding box.
    public (double X0, double Y0, double X1, double Y1) Crop { get; }

    public NetworkModel Network => _sim.Network;

    // High-realism (ORCA-promotion) pocket, for the viewer to render (SUMO world coords + radii).
    public double HighRealismPocketX => _sim.HighRealismPocketX;
    public double HighRealismPocketY => _sim.HighRealismPocketY;
    public double HighRealismPromoteRadius => _sim.HighRealismPromoteRadius;
    public double HighRealismDemoteRadius => _sim.HighRealismDemoteRadius;

    // #15 camera-driven LC-realism zone (docs/LIVE-CITY-CAMERA-REALISM-ZONE-DESIGN.md): the live zone the
    // per-area lane-change realism gate tests against. The Viewer pushes SetLcRealismZone once per frame
    // (Follow/Locked modes) and renders the highlight ring at LcZone{X,Y,Radius}. Central mode leaves it
    // on the static pocket (== prior behaviour). SUMO world coords.
    public double LcZoneX => _sim.LcZoneX;
    public double LcZoneY => _sim.LcZoneY;
    public double LcZoneRadius => _sim.LcZoneRadius;
    //
    // THREADED MODE (§4 hazard 3): the render thread must not call this -- `LiveCitySim.SetLcRealismZone`
    // rebuilds the ORCA interest source, so a camera-driven push landing mid-step corrupts it. Once the
    // producer is running, every render->sim write below routes through `LiveCitySim.Request*`, which parks
    // the value (last writer wins) for the producer to apply at the top of its next step. One method per
    // knob, one branch each, so a caller cannot pick the wrong one by forgetting which mode it is in.
    public void SetLcRealismZone(double centreX, double centreY, double radius)
    {
        if (_producer is not null)
        {
            _sim.RequestLcRealismZone(centreX, centreY, radius);
            return;
        }

        _sim.SetLcRealismZone(centreX, centreY, radius);
    }

    // docs/LIVE-CITY-VISUALS-NOTES.md "Shared foundation": the static world-overlay scene (zones/
    // buildings/pois, all optional) LiveCitySim already loaded once in its own ctor -- exposed here so the
    // Viewer's zone-ground (and later building/POI) layers read it without a second parse.
    public LiveCityScene Scene => _sim.Scene;

    // The LOCAL, Z-aware lane source (Lane.ShapeZ-carrying) -- same type SimSource.LocalLanes exposes, so
    // RoadMeshBuilder/Reconstructor honor the net's elevation on the live-city path exactly as they do on
    // the --scenario path (docs/LIVE-CITY-VIEWERS-TASKS.md D2).
    public NetworkLaneSource LocalLanes => _sim.LocalLanes;

    // The transport-neutral car read side -- IReplicationSource, exactly what SimSource.Source exposes, so
    // the SAME CityLib.Reconstructor + KinematicReconstructor render live-city cars unchanged.
    public IReplicationSource Source => _sim.VehicleSource;

    public double Time => _sim.Time;

    // docs/LIVE-CITY-VISUALS-NOTES.md-adjacent fix (ped smoothing pivot): the SAME in-memory replication
    // wire (byte-loopback, LiveCitySim's own PedReplicationPublisher -> InMemoryPedReplicationBus) the
    // remote/DDS ped path reconstructs from -- exposed so the LOCAL live-city viewer can reconstruct peds
    // via CityLib.PedReconstructor (Sim.Pedestrians.Lod.PedRemoteReconstructor's continuous
    // HeadlessIg.ReconstructSample playout) instead of interpolating discrete per-tick position snapshots.
    // This is the "server==IG" pipeline the remote path already proves out; the local path had simply never
    // been wired through it (it read LiveCitySim.Sample()'s ground-truth positions directly, stepped once
    // per Dt tick, hence the visible ~1Hz jerk).
    public IPedReplicationSource PedSource => _sim.PedSource;

    // The ped read-back for one render frame -- cars+peds are sampled together off LiveCitySim's last
    // stepped frame (LiveCitySim "does not render, only steps and samples", design §1); the Viewer maps
    // each ped's (X,Y,Z) through CoordinateTransform.SumoToGodot itself (no CityLib ped-transform reuse
    // needed -- LiveCityPed's PedRegime differs from CityLib.PedRegime by design, see PedRegime's doc
    // comment in Sim.LiveCity/LiveCitySnapshot.cs).
    public IReadOnlyList<LiveCityPed> Peds => Sample().Peds;

    // docs/LIVE-CITY-VIEWERS-TASKS.md D4 -- the full coupled-scene sample (cars incl. Name + peds) for one
    // render frame, in ONE call (so a caller building the D4 Handle->Name table doesn't have to sample
    // twice per frame the way `Peds` alone would if a caller also wanted `Cars`). `Peds` above is kept as
    // the pre-D4 convenience accessor; callers that need both cars and peds this frame should prefer this.
    public LiveCitySnapshot Sample()
    {
        ThrowIfThreaded(nameof(Sample));
        return _sim.Sample();
    }

    // Cars-only readback into a reused buffer -- for the per-frame vehicle name/pose table without paying
    // to materialise the whole ped crowd every frame (the GC-pressure fix at large LIVECITY_PEDS).
    public IReadOnlyList<LiveCityCar> SampleCars()
    {
        ThrowIfThreaded(nameof(SampleCars));
        return _sim.SampleCars();
    }

    // docs/LIVE-CITY-PED-CROSSING-SIGNALS-DESIGN.md T1: passthrough to LiveCitySim.SampleCrossingSignals()
    // for the viewer's mini pedestrian-crossing signal heads (T2, owned separately).
    public IReadOnlyList<(int LaneHandle, char State)> SampleCrossingSignals()
    {
        ThrowIfThreaded(nameof(SampleCrossingSignals));
        return _sim.SampleCrossingSignals();
    }

    // §4 hazard 3: these three read LIVE sim state, and two of them (`Sample`, `SampleCars`) hand back
    // LiveCitySim's own REUSED scratch buffer -- so calling them from the render thread while the producer
    // is mid-step is a data race whose symptom is garbled or torn car/ped data, not a crash. Threaded
    // consumers read `Published` and `CopyCrossingSignals` instead. Throwing here makes the mistake
    // impossible to make quietly; there is no correct silent fallback to offer.
    private void ThrowIfThreaded(string member)
    {
        if (_producer is not null)
        {
            throw new InvalidOperationException(
                $"LiveCitySource.{member}() reads live sim state and must not be called while the producer "
                + "thread is running (StartThreadedTick). Use Published / CopyCrossingSignals instead.");
        }
    }

    // Advances the coupled sim one Dt=0.5s tick (LiveCityConfig.Dt) and publishes the resulting frame onto
    // the car wire (LiveCitySim.Step()'s own responsibility) -- mirrors SimSource.Tick()'s one-line shape.
    //
    // SYNCHRONOUS. In threaded mode (`StartThreadedTick`) the producer thread owns this and a caller must
    // not invoke it -- hence the guard, which turns "two threads stepping the same sim" from a subtle
    // corruption into an immediate, named failure.
    public void Tick()
    {
        if (_producer is not null)
        {
            throw new InvalidOperationException(
                "LiveCitySource.Tick() must not be called once StartThreadedTick() has run -- the producer "
                + "thread owns the sim. Read the published frame via TryGetPublished instead.");
        }

        _sim.Step();
        _stepIndex++;
    }

    // ================= docs/LIVE-CITY-THREADED-TICK-DESIGN.md §5/§6 Stage 2 =========================
    //
    // THE PROBLEM. `Tick()` -> `LiveCitySim.Step()` ran synchronously inside the Godot `_Process` body, so
    // every rendered frame that crossed a tick boundary blocked for a whole engine step -- measured by the
    // owner as a 100-200 ms hiccup ~110x/minute at 4 000 cars + 8 000 peds, i.e. exactly the 2 Hz tick.
    // Worse, the caller's `while (accumulator >= dt)` ran SEVERAL steps in one frame when behind, so
    // falling behind compounded.
    //
    // THE FIX. One producer thread runs `Step()` in a paced loop. The render thread never touches sim state:
    // it reads the published car wire (`Source`, now thread-safe -- see InMemoryReplicationBus's §4 hazard-1
    // note), the published ped wire (`PedSource`), and the `Published` snapshot below.
    //
    // THE HANDOFF is described at `_publishLock` below -- §5 proposed a lock-free triple buffer, and the
    // reason this is a lock instead is recorded there rather than glossed over.
    //
    // DEVIATION FROM THE DESIGN, recorded deliberately: §5 proposed triple-buffering the VEHICLE RECORDS
    // too. They are not here, because the car path already flows through `InMemoryReplicationBus` +
    // `Reconstructor`, and making that bus concurrent + pooling its mover buffers achieves the same success
    // condition (thread-safe, zero steady-state allocation, consumer never reads a buffer the producer can
    // overwrite) while reusing the reconstruction path that is already tested. What IS triple-buffered here
    // is everything the render thread used to read by reaching INTO the sim: sim time, step index, live
    // counts, the crossing-signal states, and the live LC zone.
    public readonly record struct PublishedFrame(
        double SimTime,
        long StepIndex,
        int Cars,
        int Peds,
        double LcZoneX,
        double LcZoneY,
        double LcZoneRadius,
        long PublishTimestamp,
        double AchievedSimHz,
        bool Valid);

    private sealed class Slot
    {
        public double SimTime;
        public long StepIndex;
        public int Cars;
        public int Peds;
        public double LcZoneX, LcZoneY, LcZoneRadius;
        public long PublishTimestamp;
        public double AchievedSimHz;
        public bool Valid;

        // Crossing-signal states, copied per publish. `SampleCrossingSignals()` reads live engine columns,
        // so the render thread must never call it in threaded mode -- it gets this copy instead. Grows only
        // when a net has more controlled crossings than the last high-water mark (i.e. once).
        public int[] SignalLanes = Array.Empty<int>();
        public char[] SignalStates = Array.Empty<char>();
        public int SignalCount;
    }

    // THE HANDOFF. §5 proposed a lock-free triple buffer and this was one; the test
    // `PublishedFramesAreMonotonic_AndNeverTornAcrossFields` killed it, and the bug is worth recording
    // because it is the kind that looks fine in a design sketch:
    //
    //   With three slots and a single `_ready` handoff cell, the consumer's claim is
    //   `_read = Exchange(ref _ready, _read)`. When the consumer polls FASTER than the producer publishes --
    //   which is the normal case, a 60 Hz frame loop against a 2 Hz tick -- the second claim swaps the slot
    //   it just handed back and returns a STALE one. Observed directly: "step index went backwards: 0 after
    //   1". A plain triple buffer needs a validity sentinel and a slot-ownership dance to avoid that.
    //
    // So this is a LOCK, for the same reason the request slots are (§4 hazard 3): it is taken once per
    // rendered frame and once per step, always uncontended, and it guards a ~100-byte copy against a ~100 ms
    // step. There is no performance to win and correctness is now obvious rather than argued. The published
    // payload is small BY DESIGN (§3: the car records go over the already-thread-safe replication bus, not
    // through here), which is exactly what makes a lock the right call for what remains.
    private readonly object _publishLock = new();
    private readonly Slot _publishedSlot = new();

    // The consumer's own copy of the crossing signals, filled under the lock by `Published` and read by
    // `CopyCrossingSignals` -- so the consumer never touches producer-owned memory.
    private int[] _consumerSignalLanes = Array.Empty<int>();
    private char[] _consumerSignalStates = Array.Empty<char>();
    private int _consumerSignalCount;

    private Thread? _producer;
    private volatile bool _stopRequested;
    private long _stepIndex;

    // Achieved-rate measurement, producer-side: steps completed and wall elapsed over a rolling window, so
    // the HUD can show REQUESTED vs ACHIEVED Hz and never claim a rate that is not being met (Stage 1b's
    // rule, which threading makes load-bearing -- the producer, not the frame loop, now sets the ceiling).
    private const double AchievedWindowSeconds = 1.0;
    private long _windowStartTicks;
    private int _windowSteps;
    private double _achievedHz;

    /// True once the tick runs on its own thread. `Tick()` throws from then on.
    public bool IsThreaded => _producer is not null;

    /// Start the producer thread. Idempotent-by-throw: calling it twice is a bug, not a no-op.
    ///
    /// The thread is a BACKGROUND thread on purpose -- if the host process exits without disposing this
    /// source, the runtime must not be held open by a sim loop. `Dispose` still joins it properly.
    public void StartThreadedTick()
    {
        if (_producer is not null)
        {
            throw new InvalidOperationException("LiveCitySource.StartThreadedTick() called twice.");
        }

        // §4 hazard 1: from here on Step() runs on the producer thread while the consumer enumerates the
        // vehicle bus's history on the render thread, so the sim must STOP draining that bus itself -- the
        // consumer's own per-frame pump (inside CityLib.Reconstructor.Reconstruct) is the only legal one.
        // Without this the two threads insert into / enumerate the same Dictionary: measured on GPU as 13 x
        // "Collection was modified" per run at 10 000 cars, each aborting that frame's car pass.
        _sim.SelfPumpVehicleBus = false;

        // Publish an initial frame so the consumer has a valid snapshot before the first step completes --
        // otherwise the render clock has nothing to anchor to on frame 1.
        PublishSlot();

        _windowStartTicks = Stopwatch.GetTimestamp();
        _producer = new Thread(ProducerLoop)
        {
            IsBackground = true,
            Name = "LiveCitySim tick",
        };
        _producer.Start();
    }

    private void ProducerLoop()
    {
        while (!_stopRequested)
        {
            var periodTicks = (long)(Math.Max(_sim.Dt, 1e-3) * Stopwatch.Frequency);
            var started = Stopwatch.GetTimestamp();

            _sim.Step();
            _stepIndex++;
            MeasureAchievedRate();
            PublishSlot();

            // Pace to the configured tick rate. If the step ALREADY overran the period there is nothing to
            // wait for -- run flat out and let the achieved-Hz figure report the shortfall honestly rather
            // than pretending to hit a rate we cannot. `Thread.Sleep(1)` granularity is ~1-15 ms depending
            // on the platform timer, which is well inside a tick period at any rate this host runs.
            var remaining = periodTicks - (Stopwatch.GetTimestamp() - started);
            while (remaining > 0 && !_stopRequested)
            {
                var ms = (int)(remaining * 1000L / Stopwatch.Frequency);
                if (ms <= 0)
                {
                    break; // sub-millisecond remainder: not worth a syscall, and spinning would burn a core
                }

                Thread.Sleep(Math.Min(ms, 20)); // capped so a stop request is noticed promptly
                remaining = periodTicks - (Stopwatch.GetTimestamp() - started);
            }
        }
    }

    private void MeasureAchievedRate()
    {
        _windowSteps++;
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        if (elapsed >= AchievedWindowSeconds)
        {
            _achievedHz = _windowSteps / elapsed;
            _windowSteps = 0;
            _windowStartTicks = now;
        }
    }

    // Fill the published slot. Producer thread only. Everything the render thread will read is written here,
    // under the lock, in one go -- so a consumer can never see two fields from different steps.
    private void PublishSlot()
    {
        var signals = _sim.SampleCrossingSignals();

        // Sampled BEFORE taking the lock: `SampleCrossingSignals` reads engine state, which is the
        // producer's own, and holding the lock across it would needlessly widen the critical section.
        lock (_publishLock)
        {
            var s = _publishedSlot;
            s.SimTime = _sim.Time;
            s.StepIndex = _stepIndex;
            s.Cars = _sim.CurrentCars;
            s.Peds = _sim.CurrentPeds;
            s.LcZoneX = _sim.LcZoneX;
            s.LcZoneY = _sim.LcZoneY;
            s.LcZoneRadius = _sim.LcZoneRadius;
            s.AchievedSimHz = _achievedHz;
            s.PublishTimestamp = Stopwatch.GetTimestamp();
            s.Valid = true;

            if (s.SignalLanes.Length < signals.Count)
            {
                s.SignalLanes = new int[Math.Max(signals.Count, 16)];
                s.SignalStates = new char[Math.Max(signals.Count, 16)];
            }

            for (var i = 0; i < signals.Count; i++)
            {
                s.SignalLanes[i] = signals[i].LaneHandle;
                s.SignalStates[i] = signals[i].State;
            }

            s.SignalCount = signals.Count;
        }
    }

    /// The newest published frame (consumer thread). Polling faster than the tick rate simply returns the
    /// same frame again -- `StepIndex` is monotonic and every field comes from one publish. Also refreshes
    /// the consumer's crossing-signal copy, which `CopyCrossingSignals` then reads without a lock.
    ///
    /// In non-threaded mode this reads the live sim, so a caller can use ONE code path for both.
    public PublishedFrame Published
    {
        get
        {
            if (_producer is null)
            {
                return new PublishedFrame(
                    _sim.Time, _stepIndex, _sim.CurrentCars, _sim.CurrentPeds,
                    _sim.LcZoneX, _sim.LcZoneY, _sim.LcZoneRadius,
                    Stopwatch.GetTimestamp(), 0.0, Valid: true);
            }

            lock (_publishLock)
            {
                var s = _publishedSlot;

                if (_consumerSignalLanes.Length < s.SignalCount)
                {
                    _consumerSignalLanes = new int[Math.Max(s.SignalCount, 16)];
                    _consumerSignalStates = new char[Math.Max(s.SignalCount, 16)];
                }

                Array.Copy(s.SignalLanes, _consumerSignalLanes, s.SignalCount);
                Array.Copy(s.SignalStates, _consumerSignalStates, s.SignalCount);
                _consumerSignalCount = s.SignalCount;

                return new PublishedFrame(
                    s.SimTime, s.StepIndex, s.Cars, s.Peds,
                    s.LcZoneX, s.LcZoneY, s.LcZoneRadius,
                    s.PublishTimestamp, s.AchievedSimHz, s.Valid);
            }
        }
    }

    /// The crossing-signal states from the frame `Published` last returned, appended into a caller-owned
    /// (reused) list. Call `Published` first in the same frame; this reads the consumer-side copy that call
    /// refreshed, so it takes no lock and touches no producer memory. In non-threaded mode it reads the sim.
    public void CopyCrossingSignals(List<(int LaneHandle, char State)> into)
    {
        into.Clear();

        if (_producer is null)
        {
            foreach (var entry in _sim.SampleCrossingSignals())
            {
                into.Add(entry);
            }

            return;
        }

        for (var i = 0; i < _consumerSignalCount; i++)
        {
            into.Add((_consumerSignalLanes[i], _consumerSignalStates[i]));
        }
    }

    /// Wall-clock seconds since the held frame was published. The render clock's extrapolation term.
    public double SecondsSincePublish(in PublishedFrame frame)
        => Math.Max(0.0, (Stopwatch.GetTimestamp() - frame.PublishTimestamp) / (double)Stopwatch.Frequency);

    // docs/EXTERNAL-NET-VIEWER-DESIGN.md §3 (C3), -TASKS.md T3: the LIVE density knobs a viewer slider
    // drives. Both poke the very objects the running sim holds -- the by-reference LiveCityConfig for
    // cars, the live PedDemand for peds -- so a change is felt on the NEXT Tick() with no rebuild of the
    // sim, the scene meshes, or this source.
    public void SetCarTarget(int targetConcurrent, int? spawnPerStep = null)
    {
        if (_producer is not null)
        {
            _sim.RequestCarDensity(targetConcurrent, spawnPerStep);
            return;
        }

        _sim.SetCarDensity(targetConcurrent, spawnPerStep);
    }

    public void SetPedDensity(int populationCap, double spawnRatePerSecond)
    {
        if (_producer is not null)
        {
            _sim.RequestPedDensity(populationCap, spawnRatePerSecond);
            return;
        }

        _sim.SetPedDensity(populationCap, spawnRatePerSecond);
    }

    // docs/LIVE-CITY-THREADED-TICK-DESIGN.md §6 Stage 1b: the live tick-rate knob. Forwards straight
    // through to LiveCitySim.Dt (itself a thin wrapper over the by-reference LiveCityConfig.Dt), so
    // setting it here is felt on the very next Tick() -- no sim rebuild, mirroring SetCarTarget/
    // SetPedDensity's own "poke the live cfg" idiom. `SimHz` is the Hz-flavored view a slider naturally
    // wants (Hz = 1/Dt); `Dt` is exposed too since Tick()'s caller (Main.ProcessLiveCity) needs the raw
    // seconds value for its own accumulator.
    public double Dt
    {
        get => _sim.Dt;
        set
        {
            if (_producer is not null)
            {
                _sim.RequestDt(value);
                return;
            }

            _sim.Dt = value;
        }
    }

    // The rate the producer is ACTUALLY achieving (rolling 1 s window), or 0 before the first window closes
    // / when not threaded. `SimHz` above is the REQUESTED rate; a HUD must show both, because at 5 000 cars
    // + 20 000 peds a ~114 ms step caps the honest ceiling near 8.8 Hz however high the slider goes.
    public double AchievedSimHz => Published.AchievedSimHz;

    public double SimHz
    {
        get => Dt > 0.0 ? 1.0 / Dt : 0.0;
        set { if (value > 0.0) Dt = 1.0 / value; }
    }

    // The live values a slider should initialise itself from (rather than assuming the defaults).
    public int CarTarget => _cfg.CarTargetConcurrent;
    public int PedCap => _sim.PedDemand?.PopulationCap ?? 0;
    public double PedSpawnRate => _sim.PedDemand?.SpawnRatePerSecond ?? 0.0;

    // Live counts, for a slider label to show what the dial actually achieved.
    public int CurrentCars => _sim.CurrentCars;
    public int CurrentPeds => _sim.CurrentPeds;

    // True when this net has pedestrian infrastructure at all -- a viewer can grey out its ped slider
    // rather than offering a dial that cannot move (LiveCitySim.SetPedDensity is a no-op there).
    public bool PedestriansEnabled => _sim.PedestriansEnabled;

    // Stop the producer BEFORE disposing the sim, or the loop steps a disposed sim. The join is bounded so a
    // wedged step cannot hang the host's shutdown; the thread is a background thread, so even a timeout
    // cannot keep the process alive.
    public void Dispose()
    {
        _stopRequested = true;
        var producer = _producer;
        _producer = null;
        producer?.Join(TimeSpan.FromSeconds(5));
        _sim.Dispose();
    }
}
