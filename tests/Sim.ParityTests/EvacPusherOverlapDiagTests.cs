using Sim.Core;
using Sim.Evac;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// DIAGNOSTIC (docs/JUNCTION-REALISM-SESSION-JOURNAL.md Entry 10) for the one regression that
// `Engine.BayExitLaneKeepClear` ships with: `EvacPhase3Tests.ActivePushers_NeverInterpenetrate` goes
// from a minimum pusher separation of 4.073 m to 0.463 m when that gate is on. 0.463 m between ~5 m
// vehicles is a GROSS overlap, not a marginal threshold miss.
//
// `ActivePushers_NeverInterpenetrate` reports only the scalar minimum, which cannot distinguish the two
// hypotheses that have different fixes:
//   (a) the pair CONVERGES while both are active -- something let them close;
//   (b) the pair is ALREADY close on the first step both are active -- a placement/activation effect.
// The discriminator is `firstSep` versus `worstSep`.
//
// ⚠ WHAT THIS TEST DOES *NOT* TELL YOU, and an earlier version of it wrongly claimed: convergence does
// NOT implicate `Sim.Core`'s car-following. Pushers are moved by `VehicleMover`, which wraps
// `MixedTrafficCrowd` -- an ORCA solve -- so the Engine's lane car-following never governs their
// separation. `BayExitLaneKeepClear` perturbs the ENGINE traffic these pushers derive from; the
// separation itself is decided in the crowd. Attributing the subsystem needs its own instrument.
//
// Always-passing instrument: it asserts nothing about the separation, it REPORTS. Committed rather than
// scratch because a probe that is deleted makes its own numbers unfalsifiable (CLAUDE.md #8/#13).
public class EvacPusherOverlapDiagTests
{
    private readonly ITestOutputHelper _out;

    public EvacPusherOverlapDiagTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios"))
                && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    [Fact]
    public void Report_WhichPushersConverge_AndWhether_TheyWereEverApart()
    {
        var netPath = Path.Combine(RepoRoot(), "scenarios", "evac-grid", "net.net.xml");
        var (_, director, _) = EvacGridScenario.Build(netPath);

        // handle-pair -> (first step both active, their separation on that step, worst separation, step of worst)
        var firstSeen = new Dictionary<(int, int), (int Step, double Sep)>();
        var worst = new Dictionary<(int, int), (int Step, double Sep)>();

        // Horizon is env-tunable (EVAC_DIAG_STEPS, default 300 = what
        // ActivePushers_NeverInterpenetrate uses) so the "is this pre-existing?" question can be asked
        // by running LONGER with the gate off, without editing the test each time.
        var steps = int.TryParse(Environment.GetEnvironmentVariable("EVAC_DIAG_STEPS"), out var envSteps)
            ? envSteps : 300;
        for (var step = 0; step < steps; step++)
        {
            director.Tick();

            var poses = new List<(VehicleHandle H, double X, double Y)>();
            foreach (var (h, x, y, _) in director.ActivePushersWithHandle())
            {
                poses.Add((h, x, y));
            }

            for (var a = 0; a < poses.Count; a++)
            {
                for (var b = a + 1; b < poses.Count; b++)
                {
                    var dx = poses[a].X - poses[b].X;
                    var dy = poses[a].Y - poses[b].Y;
                    var d = Math.Sqrt(dx * dx + dy * dy);

                    // Order the key so a pair is one key regardless of iteration order.
                    var ia = poses[a].H.GetHashCode();
                    var ib = poses[b].H.GetHashCode();
                    var key = ia <= ib ? (ia, ib) : (ib, ia);

                    if (!firstSeen.ContainsKey(key))
                    {
                        firstSeen[key] = (step, d);
                        worst[key] = (step, d);
                    }
                    else if (d < worst[key].Sep)
                    {
                        worst[key] = (step, d);
                    }
                }
            }
        }

        var ranked = worst.OrderBy(kv => kv.Value.Sep).Take(6).ToList();
        _out.WriteLine($"pairs tracked: {worst.Count}");
        _out.WriteLine("worst pairs -- 'firstSep' is their separation on the FIRST step both were active:");
        foreach (var (key, w) in ranked)
        {
            var f = firstSeen[key];
            // REPORT THE FACT, NOT AN ATTRIBUTION. The first version of this printed
            // "converged => car-following (Sim.Core)", which is an UNWARRANTED INFERENCE: pushers are
            // moved by `VehicleMover`, which wraps `MixedTrafficCrowd` (an ORCA solve), NOT by the
            // Engine's lane car-following. So convergence tells you the pair closed over time; it does
            // NOT tell you which subsystem let them. Naming the subsystem here would have sent the next
            // reader into Sim.Core for a defect that lives in the crowd solve.
            var verdict = f.Sep < 1.0
                ? "already close when first active"
                : "started apart, converged over time";
            _out.WriteLine(
                $"  pair({key.Item1},{key.Item2})  worstSep={w.Sep:F3} @step {w.Step}   "
                + $"firstSep={f.Sep:F3} @step {f.Step}   {verdict}");
        }

        Assert.True(worst.Count > 0, "expected at least one active pusher pair over 300 steps.");
    }
}
