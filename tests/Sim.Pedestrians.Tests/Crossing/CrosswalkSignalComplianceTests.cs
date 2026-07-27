using System.Linq;
using System.Reflection;
using Sim.Core;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Crossing;
using Sim.Pedestrians.Demand;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Crossing;

// Phase 2b (docs/LIVE-CITY-CROSSWALK-SIGNAL-DESIGN.md §5 success condition 1): a low-power pedestrian
// routed across a SIGNALIZED crossing must not step onto it against its ped signal -- it waits at the
// kerb on red and its on-crossing interval lies entirely within a walk (green) window. Exercised against
// the REAL POC-0 net (the same fixture the rest of the ped demand suite uses), whose junction "c" is a
// static TL with four signalized crossings :c_c0..:c_c3 (cycle 90 s). The north crossing :c_c0
// (linkIndex 20) is green only during phase 3 -> [45, 82) s of every cycle; a W<->E north-arm route
// crosses exactly it, so a compliant ped is on :c_c0 only inside that window.
//
// The check is OBSERVABLE and independent of the production code: it samples PositionOf every tick and,
// whenever a ped is inside a crossing polygon, evaluates that crossing's TL state directly from the net
// (CrossingTlReader) at that sim time and asserts it shows walk. It does NOT trust the timeline's own
// structure -- it verifies the emergent behaviour.
public class CrosswalkSignalComplianceTests
{
    private readonly ITestOutputHelper _output;

    public CrosswalkSignalComplianceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const double MaxSpeed = 1.4;      // m/s
    private const double Radius = 0.3;        // m
    private const double ArriveRadius = 0.3;  // m (PedLodManager waypoint-arrival radius)
    private const double ArrivalRadius = 0.5; // m (PedDemand OD-arrival radius)
    private const double Dt = 0.1;            // s
    private const double DwellSeconds = 0.5;  // s

    // North-arm sidewalk endpoints (west and east of the north-arm carriageway): a route between them
    // crosses the north crossing :c_c0. Same points PedDemandLivelinessTests uses.
    private static readonly Vec2 WestNorthArm = new(112.6, 140.0);
    private static readonly Vec2 EastNorthArm = new(127.4, 140.0);

    private static string NetPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "net.net.xml");

    private static PedNetwork LoadPoc0Network() => PedNetworkParser.Load(
        NetPath, Path.Combine(AppContext.BaseDirectory, "Fixtures", "walkable.add.xml"));

    private static (SumoNavMesh Nav, IReadOnlyList<BakedPolygon> Polys) BuildNav()
    {
        var polygons = WalkablePolygonBaker.Bake(LoadPoc0Network());
        return (new SumoNavMesh(polygons, new SumoWalkableSpace(polygons)), polygons);
    }

    private static PedLivelinessConfig Lively() => new()
    {
        PauseProbability = 0.5,
        MinPauseSeconds = 1.0,
        MaxPauseSeconds = 3.0,
        MaxPausesPerTrip = 1,
        PauseAnimTag = "sip",
    };

    private static PedDemandConfig BuildConfig(ulong seed, CrosswalkSignals? signals, double waitSpreadRadius = 0.0) => new()
    {
        Origins = new[] { WestNorthArm, EastNorthArm },
        Destinations = new[] { WestNorthArm, EastNorthArm },
        SpawnRatePerSecond = 1.5,
        PopulationCap = 6,
        Seed = seed,
        MaxSpeed = MaxSpeed,
        Radius = Radius,
        ArrivalRadius = ArrivalRadius,
        Liveliness = Lively(),
        CrosswalkSignals = signals,
        CrosswalkWaitSpreadRadius = waitSpreadRadius,
    };

    // One sampled (ped, time, position); plus the count of samples that were on a crossing while it was
    // showing red (the violation metric) and while green (the non-vacuity metric).
    private (int OnCrossing, int OnRed, int OnGreen, List<(int Id, double Time, Vec2 Pos)> Traj) Run(
        PedDemandConfig config, int steps, IReadOnlyList<BakedPolygon> polys, SumoNavMesh nav)
    {
        var publisher = new PedPublisher();
        var manager = new PedLodManager(nav, publisher, ArriveRadius, DwellSeconds);
        var demand = new PedDemand(config, nav, manager);
        var field = new InterestField();
        var noEntities = System.Array.Empty<WorldDisc>();

        // The crossing polygons + their tl linkIndex, for the ground-truth signal check.
        var crossings = BuildCrossingGroundTruth(polys);

        var traj = new List<(int, double, Vec2)>();
        int onCrossing = 0, onRed = 0, onGreen = 0;
        var now = 0.0;
        for (var i = 0; i < steps; i++)
        {
            demand.Step(now, Dt, field, noEntities);
            now += Dt;

            foreach (var id in demand.LiveIds)
            {
                var pos = manager.PositionOf(id, now);
                traj.Add((id, now, pos));

                foreach (var c in crossings)
                {
                    if (PointInPolygon(pos, c.Verts))
                    {
                        onCrossing++;
                        if (IsWalk(c, now))
                        {
                            onGreen++;
                        }
                        else
                        {
                            onRed++;
                        }

                        break; // a point is on at most one crossing
                    }
                }
            }
        }

        return (onCrossing, onRed, onGreen, traj);
    }

    // ---- Gate 1: with signals ON, no ped is ever on a crossing while it shows red ------------------
    [Fact]
    public void SignalsOn_NoPedIsEverOnACrossingDuringRed()
    {
        var (nav, polys) = BuildNav();
        var signals = CrosswalkSignals.FromNet(NetPath, polys);
        Assert.True(signals.SignalizedCount >= 4, $"expected >=4 signalized crossings, got {signals.SignalizedCount}");

        var run = Run(BuildConfig(seed: 4242UL, signals), steps: 2500, polys, nav);

        Assert.True(run.OnCrossing > 0, "no ped ever set foot on a crossing -- test is vacuous (route/geometry wrong)");
        Assert.True(run.OnGreen > 0, "no ped was ever seen crossing during green -- test is vacuous");
        Assert.Equal(0, run.OnRed);

        _output.WriteLine($"[P2b-T2] signals ON: {run.OnCrossing} on-crossing samples, {run.OnGreen} during green, "
            + $"{run.OnRed} during red (must be 0).");
    }

    // ---- Gate 2: the check is meaningful -- WITHOUT signals, peds DO reach a crossing on red -------
    //
    // Proves the compliance in Gate 1 is not luck-of-timing: the same demand, differing only in that the
    // signal awareness is removed (CrosswalkSignals = null), lands peds on a crossing while it is red.
    [Fact]
    public void SignalsOff_PedsDoStepOntoACrossingDuringRed_SoGate1IsMeaningful()
    {
        var (nav, polys) = BuildNav();
        var run = Run(BuildConfig(seed: 4242UL, signals: null), steps: 2500, polys, nav);

        Assert.True(run.OnCrossing > 0, "no ped ever set foot on a crossing -- test is vacuous");
        Assert.True(run.OnRed > 0,
            "without signal awareness peds never happened to cross on red -- Gate 1 would be vacuous for this seed");

        _output.WriteLine($"[P2b-T2] signals OFF: {run.OnCrossing} on-crossing samples, {run.OnGreen} green, "
            + $"{run.OnRed} RED (the artifact Gate 1 removes).");
    }

    // ---- Gate 3: determinism -- two signals-ON runs are byte-identical -----------------------------
    [Fact]
    public void SignalsOn_TwoRuns_AreBitIdentical()
    {
        var (navA, polysA) = BuildNav();
        var (navB, polysB) = BuildNav();
        var runA = Run(BuildConfig(seed: 909UL, CrosswalkSignals.FromNet(NetPath, polysA)), steps: 1800, polysA, navA);
        var runB = Run(BuildConfig(seed: 909UL, CrosswalkSignals.FromNet(NetPath, polysB)), steps: 1800, polysB, navB);

        Assert.Equal(runA.Traj.Count, runB.Traj.Count);
        Assert.True(runA.Traj.Count > 0);
        for (var i = 0; i < runA.Traj.Count; i++)
        {
            Assert.Equal(runA.Traj[i].Id, runB.Traj[i].Id);
            Assert.Equal(runA.Traj[i].Time, runB.Traj[i].Time, precision: 12);
            Assert.Equal(runA.Traj[i].Pos.X, runB.Traj[i].Pos.X, precision: 12);
            Assert.Equal(runA.Traj[i].Pos.Y, runB.Traj[i].Pos.Y, precision: 12);
        }

        _output.WriteLine($"[P2b-T2] signals ON double-run: {runA.Traj.Count} samples, bit-identical.");
    }

    // ---- Bug #6 (crosswalk-wait kerb clustering) -----------------------------------------------------
    //
    // Gate 4: determinism holds with the spread ON -- two runs with the SAME seed and
    // CrosswalkWaitSpreadRadius=2.0 produce byte-identical trajectories, exactly like Gate 3 above but
    // with the new knob turned on (proves the new per-ped WaitJitterSalt stream doesn't introduce any
    // nondeterminism -- still a pure function of (seed, id, now)).
    [Fact]
    public void CrosswalkWaitSpreadRadius_TwoRuns_AreBitIdentical()
    {
        var (navA, polysA) = BuildNav();
        var (navB, polysB) = BuildNav();
        var runA = Run(BuildConfig(seed: 777UL, CrosswalkSignals.FromNet(NetPath, polysA), waitSpreadRadius: 2.0),
            steps: 1800, polysA, navA);
        var runB = Run(BuildConfig(seed: 777UL, CrosswalkSignals.FromNet(NetPath, polysB), waitSpreadRadius: 2.0),
            steps: 1800, polysB, navB);

        Assert.Equal(runA.Traj.Count, runB.Traj.Count);
        Assert.True(runA.Traj.Count > 0);
        for (var i = 0; i < runA.Traj.Count; i++)
        {
            Assert.Equal(runA.Traj[i].Id, runB.Traj[i].Id);
            Assert.Equal(runA.Traj[i].Time, runB.Traj[i].Time, precision: 12);
            Assert.Equal(runA.Traj[i].Pos.X, runB.Traj[i].Pos.X, precision: 12);
            Assert.Equal(runA.Traj[i].Pos.Y, runB.Traj[i].Pos.Y, precision: 12);
        }

        _output.WriteLine($"[Bug#6] spread ON double-run: {runA.Traj.Count} samples, bit-identical.");
    }

    // Gate 5: the OFF path (radius=0, the default) is the exact pre-change single-PauseSegment splice --
    // no sidestep walks -- while radius=2.0 splices in exactly two extra bounded-length WalkSegments
    // (sidestep out, sidestep back) around the SAME wait. Exercises PedDemand's private
    // `BuildLivelyTimeline` directly (via reflection) so the assertion is about the TIMELINE'S SEGMENT
    // STRUCTURE itself, not an indirect inference from sampled positions.
    [Fact]
    public void CrosswalkWaitSpreadRadius_Zero_IsPlainPause_Positive_AddsEnterBlobAndDiagonalCross()
    {
        var (nav, polys) = BuildNav();
        var signals = CrosswalkSignals.FromNet(NetPath, polys);
        var path = nav.FindPath(WestNorthArm, EastNorthArm);
        Assert.NotNull(path);

        // MaxPausesPerTrip = 0: no unrelated liveliness "sip" pause is spliced in, so every PauseSegment
        // in the built timeline is unambiguously a crosswalk "wait" beat.
        var liveliness = new PedLivelinessConfig { MaxPausesPerTrip = 0 };

        var offTimeline = InvokeBuildLivelyTimeline(
            path!, now: 0.0, liveliness, MaxSpeed, weave: false, seed: 0, globalSeed: 0,
            p => nav.HalfWidthsAlong(p), signals, waitSpreadRadius: 0.0,
            livelinessRngSeed: 11UL, waitRngSeed: 999UL);

        var onTimeline = InvokeBuildLivelyTimeline(
            path!, now: 0.0, liveliness, MaxSpeed, weave: false, seed: 0, globalSeed: 0,
            p => nav.HalfWidthsAlong(p), signals, waitSpreadRadius: 2.0,
            livelinessRngSeed: 11UL, waitRngSeed: 999UL);

        var offSegments = offTimeline.Segments.ToList();
        var onSegments = onTimeline.Segments.ToList();

        var offWaitPauses = offSegments.Count(s => s is PauseSegment ps && ps.AnimTag == CrosswalkWaitAnimTag);
        var onWaitPauses = onSegments.Count(s => s is PauseSegment ps && ps.AnimTag == CrosswalkWaitAnimTag);
        Assert.True(offWaitPauses > 0, "no crosswalk wait was spliced in -- test is vacuous (route/geometry wrong)");
        Assert.Equal(offWaitPauses, onWaitPauses); // same number of wait beats either way -- only their shape differs

        var offWalkCount = offSegments.Count(s => s is WalkSegment);
        var onWalkCount = onSegments.Count(s => s is WalkSegment);
        // Each spread wait replaces the plain pause with enter-blob + pause + diagonal-cross: +2 walks for a
        // mid-route crossing, +1 when the crossing is the walk's last sub-segment (the diagonal ends at the
        // final vertex, so there is no tail leg). So between +1 and +2 extra walks per wait.
        Assert.True(onWalkCount >= offWalkCount + onWaitPauses && onWalkCount <= offWalkCount + (2 * onWaitPauses),
            $"expected {onWaitPauses} wait(s) to add 1-2 walks each: off={offWalkCount} on={onWalkCount}");

        // Locate the (first) wait pause in each timeline and inspect its immediate neighbours.
        var offIdx = offSegments.FindIndex(s => s is PauseSegment ps && ps.AnimTag == CrosswalkWaitAnimTag);
        var onIdx = onSegments.FindIndex(s => s is PauseSegment ps && ps.AnimTag == CrosswalkWaitAnimTag);

        // OFF: the wait pause's neighbours are the ORIGINAL route legs, not a synthetic 2-point sidestep
        // (if a neighbour happens to be a WalkSegment at all, it must not be the tiny sidestep shape).
        if (offIdx > 0 && offSegments[offIdx - 1] is WalkSegment offBefore)
        {
            Assert.True(offBefore.Path.Count != 2 || (offBefore.Path[1] - offBefore.Path[0]).Abs > 2.0 + 1e-6,
                "OFF path must not carry a bounded 2-point sidestep walk before the wait");
        }

        // ON: the wait pause is flanked by two freshly-inserted 2-point walks -- an ENTER-BLOB step
        // (kerb entry -> the ped's 2-D spot in the waiting blob) before it, and a DIAGONAL CROSS
        // (blob spot -> the far crossing exit) after it. They share the blob spot; the enter starts at
        // the kerb entry and the cross ends at the DIFFERENT far exit -- no step-back to the kerb.
        Assert.True(onIdx > 0 && onIdx < onSegments.Count - 1, "expected enter-blob + diagonal-cross walks around the wait");
        var onBefore = Assert.IsType<WalkSegment>(onSegments[onIdx - 1]);
        var onAfter = Assert.IsType<WalkSegment>(onSegments[onIdx + 1]);
        Assert.Equal(2, onBefore.Path.Count);
        Assert.Equal(2, onAfter.Path.Count);
        // Enter-blob length is bounded by the phyllotaxis slot's max radius: rr_max = 0.4*R*sqrt(SlotCount)
        // (SlotCount=64), plus the fixed 0.3 m back-offset and up to ~0.15*sqrt(2) m of jitter on each axis
        // (bounded here by a conservative +0.6 total slack), R = 2.0.
        var enterLen = (onBefore.Path[1] - onBefore.Path[0]).Abs;
        Assert.True(enterLen > 0.0 && enterLen <= (0.25 * 2.0 * Math.Sqrt(64.0)) + 0.6 + 1e-6, $"enter-blob length {enterLen} out of the phyllotaxis-slot bound");
        // The two walks share the blob spot (enter ends exactly where the diagonal cross starts).
        Assert.Equal(onBefore.Path[1].X, onAfter.Path[0].X, precision: 9);
        Assert.Equal(onBefore.Path[1].Y, onAfter.Path[0].Y, precision: 9);
        // The diagonal cross ends at the far crossing exit, NOT back at the kerb entry (the #6 no-step-back).
        var crossEnd = onAfter.Path[1];
        var kerbEntry = onBefore.Path[0];
        Assert.True((crossEnd - kerbEntry).Abs > 0.5, "diagonal cross must reach the far exit, not step back to the kerb entry");

        _output.WriteLine($"[Bug#6] OFF: {offWaitPauses} wait(s), {offWalkCount} walk(s); "
            + $"ON: {onWaitPauses} wait(s), {onWalkCount} walk(s), enter-blob {enterLen:F3} m.");
    }

    private const string CrosswalkWaitAnimTag = "wait"; // mirrors PedDemand's private CrosswalkWaitTag

    // Invokes PedDemand's private static BuildLivelyTimeline via reflection so this test asserts on the
    // TIMELINE'S SEGMENT STRUCTURE directly (the "simplest and deterministic" fallback the task calls
    // for) rather than inferring it indirectly from sampled positions.
    private static ActivityTimeline InvokeBuildLivelyTimeline(
        IReadOnlyList<Vec2> path, double now, PedLivelinessConfig liveliness, double maxSpeed,
        bool weave, ulong seed, ulong globalSeed,
        System.Func<IReadOnlyList<Vec2>, IReadOnlyList<double>> halfWidthsAlong,
        CrosswalkSignals? crosswalkSignals, double waitSpreadRadius, ulong livelinessRngSeed, ulong waitRngSeed)
    {
        var method = typeof(PedDemand).GetMethod("BuildLivelyTimeline", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new System.MissingMethodException("PedDemand.BuildLivelyTimeline not found (signature changed?)");

        var rng = new VehicleRng(livelinessRngSeed);
        var waitRng = new VehicleRng(waitRngSeed);
        // Reflection needs the EXACT parameter count, so a defaulted parameter added to the production
        // signature must be listed here too -- `elevationsAlong` is null, i.e. the flat behaviour this
        // test has always exercised, so what it asserts is unchanged.
        var args = new object?[]
        {
            path, now, liveliness, maxSpeed, rng, weave, seed, globalSeed, halfWidthsAlong, crosswalkSignals,
            waitSpreadRadius, waitRng, null,
        };

        return (ActivityTimeline)method.Invoke(null, args)!;
    }

    // ---- ground-truth signal evaluation (independent of production code) ---------------------------

    private readonly record struct CrossingTruth(string Id, Vec2[] Verts, TlProgramSpec Program, int LinkIndex);

    private static List<CrossingTruth> BuildCrossingGroundTruth(IReadOnlyList<BakedPolygon> polys)
    {
        var programs = CrossingTlReader.LoadPrograms(NetPath);
        var list = new List<CrossingTruth>();
        foreach (var p in polys)
        {
            if (p.Kind != BakedPolygonKind.Crossing || p.Vertices.Count < 3)
            {
                continue;
            }

            // Baked polygon Id is the crossing ped-lane id (":c_c0_0"); the net gates by the edge id
            // (":c_c0"). Strip the trailing "_<laneIndex>" to look up the controlling link.
            var edgeId = LaneToEdgeId(p.Id);
            var link = CrossingTlReader.FindCrossingLink(NetPath, edgeId);
            if (link is null || !programs.TryGetValue(link.TlId, out var prog))
            {
                continue;
            }

            var verts = new Vec2[p.Vertices.Count];
            for (var i = 0; i < verts.Length; i++)
            {
                verts[i] = p.Vertices[i];
            }

            list.Add(new CrossingTruth(p.Id, verts, prog, link.LinkIndex));
        }

        return list;
    }

    // Is crossing `c` showing walk ('G'/'g' at its linkIndex) at absolute time `t`?
    private static bool IsWalk(CrossingTruth c, double t)
    {
        var cycle = c.Program.CycleLength;
        var local = ((t - c.Program.Offset) % cycle + cycle) % cycle;
        var acc = 0.0;
        foreach (var phase in c.Program.Phases)
        {
            if (local < acc + phase.Duration)
            {
                var s = phase.State;
                if (c.LinkIndex < 0 || c.LinkIndex >= s.Length)
                {
                    return false;
                }

                return s[c.LinkIndex] == 'G' || s[c.LinkIndex] == 'g';
            }

            acc += phase.Duration;
        }

        return false;
    }

    private static string LaneToEdgeId(string laneId)
    {
        var us = laneId.LastIndexOf('_');
        if (us <= 0 || us == laneId.Length - 1)
        {
            return laneId;
        }

        for (var i = us + 1; i < laneId.Length; i++)
        {
            if (!char.IsDigit(laneId[i]))
            {
                return laneId;
            }
        }

        return laneId[..us];
    }

    private static bool PointInPolygon(Vec2 p, Vec2[] v)
    {
        var inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            if (((v[i].Y > p.Y) != (v[j].Y > p.Y)) &&
                (p.X < (v[j].X - v[i].X) * (p.Y - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X))
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
