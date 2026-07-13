using Sim.Core;
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
