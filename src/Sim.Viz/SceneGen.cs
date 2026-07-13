using Sim.Core;
using Sim.Core.Mixed;
using Sim.Core.Orca;
using Sim.Ingest;
using static Sim.Viz.PayloadBuilder;

namespace Sim.Viz;

// Programmatic generation of the laneless-only showcase scenes (C/D/E) that have NO golden FCD
// input -- they are produced by running the engine's own open-space ORCA layer / cross-regime
// bridge here at export time (Sim.Viz links Sim.Core). This is a VISUALIZATION-only driver: it
// exercises the same public APIs the parity tests use (Engine, OrcaCrowd) and never touches the
// engine's committed inputs/goldens or the determinism hash.
internal static class SceneGen
{
    // Disc kinds understood by the front-end palette (template.js DISC_COLORS).
    private const int KindStreamA = 0;   // #38bdf8
    private const int KindStreamB = 1;   // #fb7185
    private const int KindPedestrian = 2; // #c084fc

    // ---------------------------------------------------------------------------------------
    // Scene C -- "Car avoids a pedestrian": the cross-regime bridge. A laneless-RVO lane vehicle
    // swerves around a person crossing the bridge lane. Driven end-to-end through the real Engine
    // (LanelessRvo + Engine.CrowdSource), exactly as OrcaCrossRegimeBridgeTests does.
    // ---------------------------------------------------------------------------------------
    internal static ScenePayload BuildCarAvoidsPedestrian(string bridgeScenarioDir)
    {
        var engine = new Engine { LanelessRvo = true };
        engine.LoadScenario(
            Path.Combine(bridgeScenarioDir, "net.net.xml"),
            Path.Combine(bridgeScenarioDir, "rou.rou.xml"),
            Path.Combine(bridgeScenarioDir, "config.sumocfg"));

        // ONE pedestrian crossing the lane (start below the carriageway, walk up across it).
        var crowd = new OrcaCrowd();
        var pedIdx = crowd.Add(new Vec2(34, -6.5), 0.35, maxSpeed: 0.55, goal: new Vec2(34, 1.0));
        engine.CrowdSource = crowd;

        // Reuse the real bridge net for the drawn network (one lane band, junctions, etc.).
        var network = BuildNetwork(NetworkParser.Parse(Path.Combine(bridgeScenarioDir, "net.net.xml")));

        // Fixed vehicle slots keyed by the stable handle index, so a slot is always the same car.
        var slotByHandle = new Dictionary<uint, int>();
        var frames = new List<FramePayload>();

        for (var step = 0; step < 26; step++)
        {
            engine.Step();     // engine reads the crowd footprint (ped) via CrowdSource
            crowd.Step(1.0);   // advance the pedestrian

            var handles = engine.VehicleHandles;
            var px = engine.PosX;
            var py = engine.PosY;
            var pa = engine.Angle;

            // Grow the slot table for any newly-seen vehicle.
            for (var i = 0; i < handles.Length; i++)
            {
                if (!slotByHandle.ContainsKey(handles[i].Index))
                {
                    slotByHandle[handles[i].Index] = slotByHandle.Count;
                }
            }

            var v = new double[slotByHandle.Count][];
            for (var i = 0; i < handles.Length; i++)
            {
                var slot = slotByHandle[handles[i].Index];
                v[slot] = new[] { R(px[i]), R(py[i]), R(pa[i]) };
            }

            var ped = crowd.Position(pedIdx);
            var d = new[] { new[] { R(ped.X), R(ped.Y), R(crowd.Radius(pedIdx)), (double)KindPedestrian } };

            frames.Add(new FramePayload(v, d));
        }

        // Backfill: every frame's V must be the final slot count (early frames were built when
        // fewer slots were known); short arrays are padded with nulls (vehicle-not-present).
        NormalizeVehicleSlots(frames, slotByHandle.Count);

        return new ScenePayload(
            "Car avoids a pedestrian",
            "Cross-regime bridge: a laneless-RVO lane vehicle swerves around a person crossing the lane. "
            + "Purple disc = pedestrian (open-space ORCA); box = the SUMO-lane vehicle it mutually avoids.",
            new double[] { 10, -9, 60, 3 },
            network,
            new double[] { 5.0, 1.8 },
            1.0,
            frames.ToArray());
    }

    // ---------------------------------------------------------------------------------------
    // Scene D -- "Open-space crowd (counter-flow)": two head-on pedestrian streams that must
    // interleave. Pure OrcaCrowd, NO network. Self-organising lanes emerge.
    // ---------------------------------------------------------------------------------------
    internal static ScenePayload BuildCounterFlow()
    {
        const int perStream = 7;
        var crowd = new OrcaCrowd(2 * perStream) { SymmetryBreak = 0.05 };
        var kinds = new List<int>();

        for (var i = 0; i < perStream; i++)
        {
            crowd.Add(new Vec2(-14, i * 1.5), 0.5, 1.5, goal: new Vec2(14, i * 1.5));           // -> stream A
            kinds.Add(KindStreamA);
        }

        for (var i = 0; i < perStream; i++)
        {
            crowd.Add(new Vec2(14, i * 1.5 + 0.75), 0.5, 1.5, goal: new Vec2(-14, i * 1.5 + 0.75)); // <- stream B
            kinds.Add(KindStreamB);
        }

        var frames = RunCrowd(crowd, kinds, dt: 0.25, maxSteps: 260, arrivalEps: 0.4, decimate: 2);

        return new ScenePayload(
            "Open-space crowd (counter-flow)",
            "Two pedestrian streams walk head-on through each other (no lanes). ORCA reciprocal avoidance "
            + "self-organises passing lanes. Blue = rightbound stream, red = leftbound stream.",
            new double[] { -15, -2, 15, 12 },
            null,
            new double[] { 0, 0 },
            0.25 * 2, // decimated: 2 sim steps per stored frame
            frames);
    }

    // ---------------------------------------------------------------------------------------
    // Scene E -- "Open-space crowd (90 crossing)": two streams crossing at right angles. Pure
    // OrcaCrowd, NO network.
    // ---------------------------------------------------------------------------------------
    internal static ScenePayload BuildCrossing()
    {
        const int perStream = 6;
        var crowd = new OrcaCrowd(2 * perStream) { SymmetryBreak = 0.05 };
        var kinds = new List<int>();

        for (var i = 0; i < perStream; i++)
        {
            crowd.Add(new Vec2(-14, i * 1.4 - 3.5), 0.5, 1.5, goal: new Vec2(14, i * 1.4 - 3.5)); // -> stream A (W->E)
            kinds.Add(KindStreamA);
            crowd.Add(new Vec2(i * 1.4 - 3.5, -14), 0.5, 1.5, goal: new Vec2(i * 1.4 - 3.5, 14)); // ^ stream B (S->N)
            kinds.Add(KindStreamB);
        }

        var frames = RunCrowd(crowd, kinds, dt: 0.25, maxSteps: 260, arrivalEps: 0.5, decimate: 2);

        return new ScenePayload(
            "Open-space crowd (90 crossing)",
            "Two pedestrian streams cross at right angles in open space (no lanes). ORCA negotiates the "
            + "conflict at the centre. Blue = W->E stream, red = S->N stream.",
            new double[] { -15, -15, 15, 15 },
            null,
            new double[] { 0, 0 },
            0.25 * 2, // decimated
            frames);
    }

    // ---------------------------------------------------------------------------------------
    // Scene F -- "Uncontrolled junction (dense)": the Egypt/India-style chaotic intersection. Four
    // dense streams of mixed-size movers (cars/tuk-tuks/bikes) approach a shared crossroads from
    // N/S/E/W with NO lanes and NO signals, and negotiate through the packed centre by reciprocal
    // avoidance. Pure OrcaCrowd at scale, using the Q2/Q3 machinery so a heavily-loaded junction keeps
    // FLOWING instead of gridlocking: nearest-k neighbour culling (MaxNeighbours) + a deterministic
    // symmetry break so the 4-way conflict resolves, removal-on-arrival so movers that clear the box
    // leave (draining the queue), and the shared spatial hash (UseSpatialHash) so ~90 agents stay cheap.
    // Movers are wave-spawned each few steps to sustain the congestion. Colour = travel axis
    // (blue E<->W, red N<->S); sizes cycle across vehicle classes.
    // ---------------------------------------------------------------------------------------
    internal static ScenePayload BuildDenseJunction()
    {
        const double half = 24.0;    // field half-extent (world units ~= metres)
        const double roadHalf = 7.0; // half-width of each carriageway (14 m road)

        var crowd = new OrcaCrowd(400)
        {
            SymmetryBreak = 0.12,     // break the 4-way symmetry so the centre doesn't lock
            MaxNeighbours = 10,       // nearest-k cull (Q2) -- desymmetrises + bounds work
            RemoveOnArrival = true,   // movers that clear the far side leave (drains the queue)
            ArrivalRadius = 1.0,
            UseSpatialHash = true,    // Q3 -- keep the crowd cheap at scale
            NeighbourDist = 8.0,
            TimeHorizonObst = 8.0,    // react to the building walls early (more clearance margin)
        };

        // Confine movers to the CROSS-shaped carriageway: the four corner "buildings" are STATIC ORCA
        // obstacles (the Q1 obstacle-line port), so a mover cannot cut a corner into a building -- it is
        // bounded within the road along the arms and only mixes freely in the central box. Wound CCW
        // (agents stay outside), extended well past the exit goals so the arm walls confine movers the
        // whole way in and out (no lateral drift even off-screen).
        const double b = roadHalf, outer = 60.0;
        void Building(double x0, double y0, double x1, double y1) =>
            crowd.AddObstacle(new[] { new Vec2(x0, y0), new Vec2(x1, y0), new Vec2(x1, y1), new Vec2(x0, y1) });
        Building(-outer, b, -b, outer);       // NW
        Building(b, b, outer, outer);         // NE
        Building(-outer, -outer, -b, -b);     // SW
        Building(b, -outer, outer, -b);       // SE

        var kinds = new List<int>();      // colour: 0 = E<->W (blue), 1 = N<->S (red)
        var shapes = new List<int>();     // 0 = rectangle (car / tuk-tuk), 1 = hexagon (motorcycle)
        var headings = new List<double>();
        var rows = new[] { 1.6, 3.6, 5.6 };  // 3 sub-streams per direction (keep-right side)
        var spawn = 0;
        var vt = 0;

        // Deterministic vehicle mix -- mostly motorcycles + cars, some tuk-tuks (the Egypt/India blend).
        static (double Radius, int Shape) VType(int i) => (i % 6) switch
        {
            0 => (0.95, 0),   // car (rectangle)
            1 => (0.48, 1),   // motorcycle (hexagon)
            2 => (0.72, 0),   // tuk-tuk (rectangle)
            3 => (0.50, 1),   // motorcycle
            4 => (0.90, 0),   // car
            _ => (0.52, 1),   // motorcycle
        };

        void TryAdd(Vec2 start, Vec2 goal, int kind)
        {
            if (crowd.Count >= 340)
            {
                return;
            }

            var (r, shape) = VType(vt++);
            crowd.Add(start, r, maxSpeed: 2.6, goal: goal);
            kinds.Add(kind);
            shapes.Add(shape);
            var dir = goal - start;
            headings.Add(Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI);   // initial facing = toward goal
        }

        void SpawnWave()
        {
            var row = rows[(spawn / 4) % rows.Length];
            TryAdd(new Vec2(-half, -row), new Vec2(half + 12, -row), 0);  // W->E (right side = -y)
            TryAdd(new Vec2(half, row), new Vec2(-half - 12, row), 0);    // E->W (+y)
            TryAdd(new Vec2(row, -half), new Vec2(row, half + 12), 1);    // S->N (+x)
            TryAdd(new Vec2(-row, half), new Vec2(-row, -half - 12), 1);  // N->S (-x)
            spawn++;
        }

        var snapshots = new List<double[][]>();
        void Snapshot()
        {
            var d = new double[crowd.Count][];
            for (var i = 0; i < crowd.Count; i++)
            {
                var p = crowd.Position(i);
                var vel = crowd.Velocity(i);
                if (vel.Abs > 0.3)   // update facing only when actually moving (hold it steady while jammed)
                {
                    headings[i] = Math.Atan2(vel.Y, vel.X) * 180.0 / Math.PI;
                }

                d[i] = new[]
                {
                    R(p.X), R(p.Y), R(crowd.Radius(i)), (double)kinds[i], R(headings[i]), (double)shapes[i],
                };
            }

            snapshots.Add(d);
        }

        const int steps = 340;
        const int stopSpawnAt = 300;   // keep the box busy nearly the whole clip, then let it drain
        for (var step = 0; step < steps; step++)
        {
            if (step < stopSpawnAt && step % 5 == 0)
            {
                SpawnWave();
            }

            crowd.Step(0.25);
            Snapshot();
        }

        // Keep only frames where the junction is actually populated (trim any empty lead-in / drained
        // tail), so the clip is wall-to-wall traffic. "On screen" = within the drawn field.
        int OnScreen(double[][] d)
        {
            var n = 0;
            foreach (var a in d)
            {
                if (Math.Abs(a[0]) <= half && Math.Abs(a[1]) <= half)
                {
                    n++;
                }
            }

            return n;
        }

        var lastBusy = snapshots.Count - 1;
        while (lastBusy > 0 && OnScreen(snapshots[lastBusy]) < 6)
        {
            lastBusy--;
        }

        var frames = new List<FramePayload>();
        var noVehicles = Array.Empty<double[]?>();
        for (var i = 0; i <= lastBusy; i += 2)   // decimate ~2x
        {
            frames.Add(new FramePayload(noVehicles, snapshots[i]));
        }

        // Two wide crossing carriageways as the drawn network (hand-built -- no SUMO net needed).
        var hRoad = new LanePayload("EW", "EW", 0, 2 * roadHalf, new double[] { -half - 6, 0, half + 6, 0 });
        var vRoad = new LanePayload("NS", "NS", 0, 2 * roadHalf, new double[] { 0, -half - 6, 0, half + 6 });
        var network = new NetworkPayload(
            new[] { hRoad, vRoad }, Array.Empty<JunctionPayload>(),
            Array.Empty<TlLogicPayload>(), Array.Empty<SignalHeadPayload>());

        return new ScenePayload(
            "Uncontrolled junction (dense)",
            "No lanes, no signals: dense mixed traffic (cars, tuk-tuks, bikes) streams into a shared "
            + "crossroads from all four directions and negotiates through the packed centre by reciprocal "
            + "avoidance -- the laneless model at scale. Blue = east<->west, red = north<->south.",
            new double[] { -half, -half, half, half },
            network,
            new double[] { 0, 0 },
            0.25 * 2,
            frames.ToArray());
    }

    // ---------------------------------------------------------------------------------------
    // Scene -- "Indian junction (shaped, soft priority)": the believable mixed-traffic module
    // (docs/INDIA-TRAFFIC.md). Unlike the dense-junction disc scene, vehicles here are ANISOTROPIC
    // oriented footprints (buses long, motorcycles compact) avoided by the shaped velocity obstacle
    // (ShapedVoSolver), and priority is SOFT: the east-west "main road" runs an assertive fleet
    // (buses/cars) while the north-south cross road runs a yielding one (motorcycles/auto-rickshaws),
    // so a dominant stream emerges with no signals. Driven by MixedTrafficCrowd at export time.
    // ---------------------------------------------------------------------------------------
    internal static ScenePayload BuildIndianJunction()
    {
        const double half = 32.0;    // drawn field half-extent
        const double roadHalf = 9.0; // half-width of each 18 m carriageway

        var crowd = new MixedTrafficCrowd(400)
        {
            Nonholonomic = true,     // car-like steering: no reverse, bounded turn, no pivot-in-place
            SafetyMargin = 0.6,      // NH-ORCA tracking-error margin: real bodies stay apart despite steer lag
            SymmetryBreak = 0.03,    // gentler jitter -- the steering filter smooths motion anyway
            MaxNeighbours = 10,      // RVO2-ish; fewer simultaneous constraints -> rarer infeasibility
            RemoveOnArrival = true,
            ArrivalRadius = 3.0,
            NeighbourDist = 18.0,
            TimeHorizon = 3.0,       // longer look-ahead: bounded steering must start avoiding EARLY
        };

        // Four corner buildings confine the movers to the cross-shaped carriageway (their road-facing
        // faces sit at +/-roadHalf). Tiled into local wall segments by AddBlock -> robust confinement.
        const double b = roadHalf, outer = 80.0, thick = 1.5;
        crowd.AddBlock(-outer, b, -b, outer, thick);       // NW
        crowd.AddBlock(b, b, outer, outer, thick);         // NE
        crowd.AddBlock(-outer, -outer, -b, -b, thick);     // SW
        crowd.AddBlock(b, -outer, outer, -b, thick);       // SE

        // Assertive main-road fleet (E<->W) vs yielding cross-road fleet (N<->S). The class carries
        // the shape + assertiveness; the mix is what makes the E<->W stream dominate.
        static VehicleClass MainClass(int i) => (i % 6) switch
        {
            0 => VehicleClass.Bus,          // one bus per six -- present but not so dense it jams
            1 => VehicleClass.Car,
            2 => VehicleClass.Car,
            3 => VehicleClass.AutoRickshaw,
            4 => VehicleClass.Car,
            _ => VehicleClass.Car,
        };

        static VehicleClass CrossClass(int i) => (i % 6) switch
        {
            0 => VehicleClass.Motorcycle,
            1 => VehicleClass.AutoRickshaw,
            2 => VehicleClass.Motorcycle,
            3 => VehicleClass.Car,
            4 => VehicleClass.Motorcycle,
            _ => VehicleClass.AutoRickshaw,
        };

        var headings = new List<double>();
        var mainRows = new[] { -5.0, -1.8 };   // W->E keeps to y<0 ; E->W to y>0 (loose, Indian-style)
        var mainRows2 = new[] { 1.8, 5.0 };
        var crossRows = new[] { 1.8, 5.0 };    // S->N keeps to x>0 ; N->S to x<0
        var crossRows2 = new[] { -5.0, -1.8 };
        var spawn = 0;
        var vtMain = 0;
        var vtCross = 0;

        // Cap the number of SIMULTANEOUSLY-active movers (not the lifetime count) so the shared box
        // stays busy but not so packed that big footprints drive the LP infeasible.
        const int liveCap = 110;
        int Live()
        {
            var n = 0;
            for (var i = 0; i < crowd.Count; i++)
            {
                if (crowd.IsActive(i))
                {
                    n++;
                }
            }

            return n;
        }

        // A vehicle only enters when its spawn point is clear -- a real approach queues at the mouth
        // rather than materialising on top of the car ahead. This matters under non-holonomic steering
        // (vehicles accelerate gently and linger near the entry), and it naturally meters inflow to
        // what the junction can absorb instead of forcing an overlap.
        bool SpawnClear(Vec2 p)
        {
            for (var i = 0; i < crowd.Count; i++)
            {
                if (crowd.IsActive(i) && (crowd.Position(i) - p).Abs < 7.0)
                {
                    return false;
                }
            }

            return true;
        }

        void TryAddMain(Vec2 start, Vec2 goal)
        {
            if (crowd.Count >= 380 || Live() >= liveCap || !SpawnClear(start))
            {
                return;
            }

            var cls = MainClass(vtMain++);
            var dir = goal - start;
            crowd.Add(cls, start, goal, maxSpeedOverride: cls.MaxSpeed * 0.4);
            headings.Add(Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI);
        }

        void TryAddCross(Vec2 start, Vec2 goal)
        {
            if (crowd.Count >= 380 || Live() >= liveCap || !SpawnClear(start))
            {
                return;
            }

            var cls = CrossClass(vtCross++);
            var dir = goal - start;
            crowd.Add(cls, start, goal, maxSpeedOverride: cls.MaxSpeed * 0.4);
            headings.Add(Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI);
        }

        void SpawnWave()
        {
            var m1 = mainRows[(spawn / 2) % mainRows.Length];
            var m2 = mainRows2[(spawn / 2) % mainRows2.Length];
            var c1 = crossRows[(spawn / 2) % crossRows.Length];
            var c2 = crossRows2[(spawn / 2) % crossRows2.Length];
            TryAddMain(new Vec2(-half, m1), new Vec2(half + 16, m1));    // W->E
            TryAddMain(new Vec2(half, m2), new Vec2(-half - 16, m2));    // E->W
            TryAddCross(new Vec2(c1, -half), new Vec2(c1, half + 16));   // S->N
            TryAddCross(new Vec2(c2, half), new Vec2(c2, -half - 16));   // N->S
            spawn++;
        }

        var snapshots = new List<double[][]>();
        void Snapshot()
        {
            var d = new double[crowd.Count][];
            for (var i = 0; i < crowd.Count; i++)
            {
                var p = crowd.Position(i);
                headings[i] = crowd.Heading(i) * 180.0 / Math.PI;   // crowd tracks heading (held when slow)
                var cls = crowd.Class(i);
                d[i] = new[]
                {
                    R(p.X), R(p.Y), R(cls.Shape().CircumRadius()), (double)cls.VizColorKind,
                    R(headings[i]), (double)cls.VizShape, R(cls.Length * 0.5), R(cls.Width * 0.5),
                };
            }

            snapshots.Add(d);
        }

        const int steps = 360;
        const int stopSpawnAt = 315;
        for (var step = 0; step < steps; step++)
        {
            if (step < stopSpawnAt && step % 2 == 0)
            {
                SpawnWave();
            }

            crowd.Step(0.2);

            // Despawn any mover that has left the field -- normal exits past the arm goals, and the
            // rare bus a dense jam shoves backward out of the domain (holonomic limit). Keeps the clip
            // clean and frees the space (a departed vehicle should not keep constraining the box).
            const double bound = half + 12.0;
            for (var i = 0; i < crowd.Count; i++)
            {
                if (crowd.IsActive(i))
                {
                    var p = crowd.Position(i);
                    if (Math.Abs(p.X) > bound || Math.Abs(p.Y) > bound)
                    {
                        crowd.Deactivate(i);
                    }
                }
            }

            Snapshot();
        }

        int OnScreen(double[][] d)
        {
            var n = 0;
            foreach (var a in d)
            {
                if (Math.Abs(a[0]) <= half && Math.Abs(a[1]) <= half)
                {
                    n++;
                }
            }

            return n;
        }

        var lastBusy = snapshots.Count - 1;
        while (lastBusy > 0 && OnScreen(snapshots[lastBusy]) < 8)
        {
            lastBusy--;
        }

        var frames = new List<FramePayload>();
        var noVehicles = Array.Empty<double[]?>();
        for (var i = 0; i <= lastBusy; i += 2)
        {
            frames.Add(new FramePayload(noVehicles, snapshots[i]));
        }

        var hRoad = new LanePayload("EW", "EW", 0, 2 * roadHalf, new double[] { -half - 6, 0, half + 6, 0 });
        var vRoad = new LanePayload("NS", "NS", 0, 2 * roadHalf, new double[] { 0, -half - 6, 0, half + 6 });
        var network = new NetworkPayload(
            new[] { hRoad, vRoad }, Array.Empty<JunctionPayload>(),
            Array.Empty<TlLogicPayload>(), Array.Empty<SignalHeadPayload>());

        return new ScenePayload(
            "Indian junction (shaped, soft priority)",
            "Believable mixed traffic: SHAPED vehicles (long buses, compact motorcycles) negotiate an "
            + "uncontrolled crossroads by anisotropic reciprocal avoidance. Priority is SOFT -- the "
            + "east-west main road runs assertive buses/cars and largely holds its line, while the "
            + "north-south motorcycles/auto-rickshaws weave and yield through the gaps. No lanes, no "
            + "signals. Blue=car, pink=motorcycle, purple=auto-rickshaw, amber=bus.",
            new double[] { -half, -half, half, half },
            network,
            new double[] { 0, 0 },
            0.2 * 2,
            frames.ToArray(),
            new[] { "car", "motorcycle", "auto-rickshaw", "bus" });
    }

    // Run a pure crowd to convergence, snapshotting disc positions each step and keeping every
    // `decimate`-th snapshot (the frames are dense, so linear interpolation on the front end is
    // ample). Disc kind is per-agent (its stream), captured at Add time.
    private static FramePayload[] RunCrowd(OrcaCrowd crowd, List<int> kinds, double dt, int maxSteps, double arrivalEps, int decimate)
    {
        var snapshots = new List<double[][]>();

        void Snapshot()
        {
            var d = new double[crowd.Count][];
            for (var i = 0; i < crowd.Count; i++)
            {
                var p = crowd.Position(i);
                d[i] = new[] { R(p.X), R(p.Y), R(crowd.Radius(i)), (double)kinds[i] };
            }

            snapshots.Add(d);
        }

        Snapshot(); // initial state = frame 0
        for (var step = 0; step < maxSteps && !crowd.AllArrived(arrivalEps); step++)
        {
            crowd.Step(dt);
            Snapshot();
        }

        var frames = new List<FramePayload>();
        var noVehicles = Array.Empty<double[]?>();
        for (var i = 0; i < snapshots.Count; i += decimate)
        {
            frames.Add(new FramePayload(noVehicles, snapshots[i]));
        }

        return frames.ToArray();
    }

    // Pad every frame's vehicle array up to `slotCount` with null (absent) slots, so all frames
    // share the same fixed-slot vehicle indexing the front end relies on.
    private static void NormalizeVehicleSlots(List<FramePayload> frames, int slotCount)
    {
        for (var f = 0; f < frames.Count; f++)
        {
            var v = frames[f].V;
            if (v.Length == slotCount)
            {
                continue;
            }

            var grown = new double[slotCount][];
            Array.Copy(v, grown, v.Length);
            frames[f] = frames[f] with { V = grown };
        }
    }
}
