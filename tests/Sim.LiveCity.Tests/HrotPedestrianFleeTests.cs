using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.Pedestrians.Lod;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H16 -- a frightened pedestrian flees, inside SumoSharp: LiveCitySim.FleePedestrian
// re-routes a live, named ped away from an incident to a walkable point at least minDistance from it, at speedFactor x
// its normal speed, pinned high-power (PedFleeHighPower, default) or as a low-power runner. Same committed
// `scenarios/_ped/roadnet_min` fixture as H14 (a ped-equipped 240 m x 240 m 3x3 grid, RouteGraph mode).
public class HrotPedestrianFleeTests
{
    private readonly ITestOutputHelper _out;
    public HrotPedestrianFleeTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (Traffic.sln) not found above " + AppContext.BaseDirectory);
    }

    private static LiveCityConfig Config(bool highPower = true)
    {
        var cfg = LiveCityConfig.ForDataset(Path.Combine(RepoRoot(), "scenarios", "_ped", "roadnet_min"));
        cfg.PedPopulationCap = 60;
        cfg.PedSpawnRatePerSecond = 10.0;
        cfg.CarTargetConcurrent = 5;
        var n = 0;
        cfg.PedIdProvider = () => "ped:" + (++n).ToString(CultureInfo.InvariantCulture);
        cfg.PedFleeHighPower = highPower;
        return cfg;
    }

    private static (double X, double Y)? ServerPos(LiveCitySim sim, string name, List<LivePedestrian> scratch)
    {
        sim.CopyLivePedestrians(scratch);
        foreach (var p in scratch)
        {
            if (p.Name == name)
            {
                return (p.X, p.Y);
            }
        }

        return null;
    }

    private static double Dist(double ax, double ay, double bx, double by) =>
        Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

    // The ped's incident: 4 m from it, on the side of the net centre, so "away" points outward into the grid.
    private static (double X, double Y) IncidentNear(LivePedestrian p)
    {
        var dx = 120.0 - p.X;
        var dy = 120.0 - p.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        return len < 1e-6 ? (p.X + 4.0, p.Y) : (p.X + (4.0 * dx / len), p.Y + (4.0 * dy / len));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void H16_AFleeingPed_RunsAwayFromTheIncident_EndsFarEnough_AndTheWireFollowsIt(bool highPower)
    {
        const double MinDistance = 80.0;
        using var sim = new LiveCitySim(Config(highPower));
        var remote = new PedRemoteReconstructor(sim.PedSource);
        for (var i = 0; i < 40; i++)
        {
            sim.Step();
            remote.Pump(sim.Time);
        }

        var live = new List<LivePedestrian>();
        sim.CopyLivePedestrians(live);
        var victim = live[live.Count / 2];
        var (ix, iy) = IncidentNear(victim);
        var d0 = Dist(victim.X, victim.Y, ix, iy);
        Assert.True(remote.TryGetId(victim.Name, out var wireId));
        var legsBefore = sim.PedSource.PathArcs.Count(r => (int)r.Handle.Index == wireId);

        Assert.True(sim.FleePedestrian(victim.Name, ix, iy, MinDistance));
        Assert.Equal(1, sim.FleeingPedestrianCount);
        Assert.True(sim.IsPedestrianFleeing(victim.Name));

        // Paused (no Step): the new leg is already on the wire, starting where the ped stands.
        sim.PedSource.Pump();
        var leg = sim.PedSource.PathArcs.Where(r => (int)r.Handle.Index == wireId).ToList();
        Assert.Equal(legsBefore + 1, leg.Count);
        Assert.Equal(sim.Time, leg[^1].StartTime, 3);
        Assert.Equal(victim.X, leg[^1].Path[0].X, 2);
        Assert.Equal(victim.Y, leg[^1].Path[0].Y, 2);
        var end = leg[^1].Path[^1];
        Assert.True(Dist(end.X, end.Y, ix, iy) >= MinDistance, $"leg ends {Dist(end.X, end.Y, ix, iy):F1} m from the incident");
        Assert.Equal(victim.Name, remote.TryGetName(wireId, out var wireName) ? wireName : null);

        var distances = new List<double> { d0 };
        var worstWireGap = 0.0; // high-power steps (crowd frames)
        var worstLegGap = 0.0;  // low-power steps (the flee leg)
        var steps = 0;
        (double X, double Y) last = (victim.X, victim.Y);
        while (steps < 600)
        {
            sim.Step();
            remote.Pump(sim.Time);
            steps++;
            if (ServerPos(sim, victim.Name, live) is not { } p)
            {
                break; // arrived and despawned
            }

            last = p;
            distances.Add(Dist(p.X, p.Y, ix, iy));
            Assert.True(remote.TryGetPoseAt(victim.Name, sim.Time, out var w, out _, out _));
            var gap = Dist(w.X, w.Y, p.X, p.Y);
            var model = remote.Ig.ModelOf(wireId);
            if (model == PedDrModel.FreeKinematic)
            {
                worstWireGap = Math.Max(worstWireGap, gap);
            }
            else
            {
                worstLegGap = Math.Max(worstLegGap, gap);
            }
        }

        var finalDist = Dist(last.X, last.Y, ix, iy);
        _out.WriteLine($"highPower={highPower}: d0={d0:F1} m, after 10 steps {distances[Math.Min(10, distances.Count - 1)]:F1} m, "
            + $"final {finalDist:F1} m after {steps} steps (min {distances.Min():F1}); worst wire gap {worstLegGap:F3} m on the leg, {worstWireGap:F3} m on crowd frames");

        Assert.True(steps < 600, "the fleeing ped never arrived");
        Assert.True(distances[Math.Min(10, distances.Count - 1)] > d0 + 5.0, "it is not getting away");
        Assert.True(distances.Min() >= d0 - LiveCityPedFleeSlack, $"it came back toward the incident ({distances.Min():F1} m)");
        Assert.True(finalDist >= MinDistance - 1.0, $"it stopped {finalDist:F1} m from the incident");
        // The leg is the PathArc the server evaluates (cm-quantized vertices); a high-power ped's crowd samples are
        // DR-gated by LiveCity's PedDrErrorPublishPolicy (sent when the reader's dead reckoning is off by 0.1 m).
        Assert.True(worstLegGap < 0.05, $"the wire reader drifted {worstLegGap:F3} m from the server on the leg");
        Assert.True(worstWireGap < 0.12, $"the wire reader drifted {worstWireGap:F3} m from the server on crowd frames");

        // It arrived and despawned normally: gone from the live set and the wire, no longer counted.
        Assert.Equal(0, sim.FleeingPedestrianCount);
        Assert.False(sim.IsPedestrianFleeing(victim.Name));
        Assert.Contains(sim.PedSource.Lifecycles, l => l.Kind == PedLifecycleKind.Despawn && (int)l.Handle.Index == wireId);
        Assert.False(remote.TryGetId(victim.Name, out _));
        if (highPower)
        {
            Assert.Contains(sim.PedSource.Lifecycles, l => l.Kind == PedLifecycleKind.PromoteToFreeKinematic && (int)l.Handle.Index == wireId);
        }
    }

    // PedDemand.FleeApproachToleranceMeters, plus the ORCA agent's corner-cutting.
    private const double LiveCityPedFleeSlack = 3.0;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void H16_SpeedFactor_MakesItRunFaster_ThanItWalked(bool highPower)
    {
        var cfg = Config(highPower);
        cfg.PedPauseProbability = 0.0; // walking speed before = its leg speed: no pauses diluting it,
        cfg.PedEnableWeave = false;    // and no lateral weave lengthening the measured path
        using var sim = new LiveCitySim(cfg);
        for (var i = 0; i < 30; i++) sim.Step();

        var live = new List<LivePedestrian>();
        sim.CopyLivePedestrians(live);

        // A ped that keeps walking for the next 10 steps (does not arrive) -- measure its walking speed.
        string? name = null;
        double walked = 0.0;
        var track = live.ToDictionary(p => p.Name, p => (p.X, p.Y));
        var path = live.ToDictionary(p => p.Name, _ => 0.0);
        for (var i = 0; i < 10; i++)
        {
            sim.Step();
            sim.CopyLivePedestrians(live);
            foreach (var p in live)
            {
                if (track.TryGetValue(p.Name, out var prev))
                {
                    path[p.Name] += Dist(p.X, p.Y, prev.X, prev.Y);
                    track[p.Name] = (p.X, p.Y);
                }
            }

            var alive = new HashSet<string>(live.Select(p => p.Name));
            foreach (var gone in track.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                track.Remove(gone);
            }
        }

        foreach (var (n, len) in path.Where(kv => track.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            name = n;
            walked = len;
            break;
        }

        Assert.NotNull(name);
        var dt = 10 * cfg.Dt;
        var walkSpeed = walked / dt;

        var here = track[name!];
        var dx = 120.0 - here.X;
        var dy = 120.0 - here.Y;
        var len0 = Math.Max(1e-6, Math.Sqrt((dx * dx) + (dy * dy)));
        Assert.True(sim.FleePedestrian(name!, here.X + (3.0 * dx / len0), here.Y + (3.0 * dy / len0), 60.0, speedFactor: 2.0));

        var ran = 0.0;
        var prevPos = here;
        var runSteps = 0;
        for (var i = 0; i < 10; i++)
        {
            sim.Step();
            if (ServerPos(sim, name!, live) is not { } p)
            {
                break;
            }

            ran += Dist(p.X, p.Y, prevPos.X, prevPos.Y);
            prevPos = p;
            runSteps++;
        }

        Assert.True(runSteps >= 5, "it arrived too soon to measure");
        var runSpeed = ran / (runSteps * cfg.Dt);
        _out.WriteLine($"highPower={highPower}: {name} walked {walkSpeed:F2} m/s, ran {runSpeed:F2} m/s (x{runSpeed / walkSpeed:F2})");
        Assert.InRange(walkSpeed, 0.8, cfg.PedMaxSpeed * 1.05);
        Assert.True(runSpeed > 1.5 * walkSpeed, $"ran {runSpeed:F2} m/s vs walked {walkSpeed:F2} m/s");
        Assert.True(runSpeed <= 2.0 * cfg.PedMaxSpeed * 1.05, $"ran {runSpeed:F2} m/s, faster than 2x its speed");
    }

    [Fact]
    public void H16_CallingItAgain_ReTargets_FromTheNormalSpeed_AndRemovalStopsCountingIt()
    {
        using var sim = new LiveCitySim(Config(highPower: false)); // low-power: the leg speed is the ped's speed
        for (var i = 0; i < 40; i++) sim.Step();
        var live = new List<LivePedestrian>();
        sim.CopyLivePedestrians(live);
        var victim = live[0];
        var other = live[1];

        Assert.True(sim.FleePedestrian(victim.Name, victim.X + 3.0, victim.Y, 50.0, speedFactor: 2.0));
        Assert.True(sim.FleePedestrian(other.Name, other.X, other.Y + 3.0, 50.0));
        Assert.Equal(2, sim.FleeingPedestrianCount);
        sim.Step();
        sim.PedSource.Pump();
        var firstLeg = sim.PedSource.PathArcs.Last();

        // A second incident on the other side: re-targeted, a new leg, speed = 3x the NORMAL speed (not 3x 2x).
        var p = ServerPos(sim, victim.Name, live)!.Value;
        Assert.True(sim.FleePedestrian(victim.Name, p.X - 3.0, p.Y, 50.0, speedFactor: 3.0));
        Assert.Equal(2, sim.FleeingPedestrianCount);
        sim.PedSource.Pump();
        var retarget = sim.PedSource.PathArcs[^1];
        var mine = sim.PedSource.PathArcs.Where(r => r.Handle.Index == retarget.Handle.Index).ToList();
        var flee1 = mine[^2];
        Assert.Equal(sim.Time, retarget.StartTime, 3);
        Assert.Equal(sim.Time - 0.5, flee1.StartTime, 3);
        _out.WriteLine($"first flee leg speed {flee1.Speed:F3} m/s, re-target {retarget.Speed:F3} m/s");
        Assert.Equal(flee1.Speed * 1.5, retarget.Speed, 4);
        Assert.Equal(cfg_PedMaxSpeed * 3.0, retarget.Speed, 4);

        // Removing a fleeing ped stops counting it.
        Assert.True(sim.RemovePedestrian(other.Name, out _));
        Assert.Equal(1, sim.FleeingPedestrianCount);
        Assert.False(sim.IsPedestrianFleeing(other.Name));
        Assert.NotNull(firstLeg.Path);

        // Nothing is minDistance away: the fallback runs to the farthest reachable point -- farther than it is now.
        sim.CopyLivePedestrians(live);
        var far = live.First(q => q.Name != victim.Name);
        Assert.True(sim.FleePedestrian(far.Name, 120.0, 120.0, 1e5));
        sim.PedSource.Pump();
        var fallback = sim.PedSource.PathArcs[^1].Path[^1];
        _out.WriteLine($"fallback: from {Dist(far.X, far.Y, 120, 120):F1} m to {Dist(fallback.X, fallback.Y, 120, 120):F1} m from the incident");
        Assert.True(Dist(fallback.X, fallback.Y, 120, 120) > Dist(far.X, far.Y, 120, 120));
    }

    private const double cfg_PedMaxSpeed = 1.3; // LiveCityConfig.PedMaxSpeed default (no speed variation)

    [Fact]
    public void H16_UnknownNames_BadArguments_AndNoPedestrians_AreFalse_NeverThrow()
    {
        using var sim = new LiveCitySim(Config());
        for (var i = 0; i < 20; i++) sim.Step();
        var live = new List<LivePedestrian>();
        sim.CopyLivePedestrians(live);
        var name = live[0].Name;

        Assert.False(sim.FleePedestrian("nobody", 0, 0, 10));
        Assert.False(sim.FleePedestrian(null!, 0, 0, 10));
        Assert.False(sim.FleePedestrian(name, double.NaN, 0, 10));
        Assert.False(sim.FleePedestrian(name, 0, 0, -1));
        Assert.False(sim.FleePedestrian(name, 0, 0, double.PositiveInfinity));
        Assert.False(sim.FleePedestrian(name, 0, 0, 10, speedFactor: 0.0));
        Assert.False(sim.FleePedestrian(name, 0, 0, 10, speedFactor: double.NaN));
        Assert.Equal(0, sim.FleeingPedestrianCount);

        Assert.True(sim.RemovePedestrian(name, out _));
        Assert.False(sim.FleePedestrian(name, 0, 0, 10)); // gone

        var dir = Path.Combine(Path.GetTempPath(), "h16-noped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "net.xml"),
                "<net>\n"
                + "  <edge id=\"e1\" from=\"A\" to=\"B\">\n"
                + "    <lane id=\"e1_0\" index=\"0\" speed=\"13.9\" length=\"30.0\" shape=\"0,0 30,0\"/>\n"
                + "  </edge>\n"
                + "</net>\n");
            using var bare = new LiveCitySim(LiveCityConfig.ForDataset(dir));
            Assert.False(bare.PedestriansEnabled);
            Assert.False(bare.FleePedestrian("1", 0, 0, 10));
            Assert.Equal(0, bare.FleeingPedestrianCount);
            Assert.False(bare.IsPedestrianFleeing("1"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Records every ped wire record as a canonical line (exact floats) and forwards nothing -- the record/DDS tee.
    private sealed class PedLines : IPedReplicationSink
    {
        public readonly List<string> Lines = new();

        public void PublishCrowdFrame(uint step, float time, ReadOnlySpan<PedFreeKinematicRecord> records)
        {
            var line = $"C {step} {time:R}";
            foreach (var r in records)
            {
                line += $" {r.Handle.Index}:{r.X:R},{r.Y:R},{r.Vx:R},{r.Vy:R}";
            }

            Lines.Add(line);
        }

        public void PublishPathArc(in Sim.Replication.PathArcRecord record) =>
            Lines.Add($"P {record.Handle.Index} {record.Speed:R} {record.StartTime:R} "
                + string.Join(";", record.Path.Select(v => $"{v.X:R},{v.Y:R}")));

        public void PublishActivityTimeline(VehicleHandle handle, ReadOnlySpan<byte> timelineBytes) =>
            Lines.Add($"A {handle.Index} {Convert.ToBase64String(timelineBytes.ToArray())}");

        public void PublishPedLifecycle(in PedLifecycleRecord record) =>
            Lines.Add($"L {record.Handle.Index} {record.Kind} {record.Time:R} {record.Name}");

        public void Dispose() { }
    }

    private static List<string> Run(Action<LiveCitySim, int>? between)
    {
        var tee = new PedLines();
        using var sim = new LiveCitySim(Config(), recordPedSink: tee);
        for (var i = 0; i < 120; i++)
        {
            between?.Invoke(sim, i);
            sim.Step();
        }

        tee.Lines.Add($"live {sim.CurrentPeds} fleeing {sim.FleeingPedestrianCount}");
        return tee.Lines;
    }

    [Fact]
    public void H16_Determinism_AndAFalseCallChangesNothing()
    {
        static void Flee(LiveCitySim sim, int step)
        {
            if (step != 40 && step != 41)
            {
                return;
            }

            var live = new List<LivePedestrian>();
            sim.CopyLivePedestrians(live);
            for (var k = 0; k < 3 && k < live.Count; k++)
            {
                var p = live[(k * 7) % live.Count];
                Assert.True(sim.FleePedestrian(p.Name, p.X + 2.0, p.Y + 1.0, 60.0));
            }
        }

        var a = Run(Flee);
        var b = Run(Flee);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a, b);
        _out.WriteLine($"twin flee runs: {a.Count} identical ped wire records; {a[^1]}");

        // Off by default: only false calls (unknown names, bad arguments) leave the ped wire byte-identical to none.
        var none = Run(null);
        var falseCalls = Run((sim, step) =>
        {
            Assert.False(sim.FleePedestrian("nobody", 0, 0, 10));
            Assert.False(sim.FleePedestrian("ped:1", double.NaN, 0, 10));
        });
        Assert.Equal(none, falseCalls);
        Assert.NotEqual(none, a);
    }
}
