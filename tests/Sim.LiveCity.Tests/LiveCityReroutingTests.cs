using Sim.LiveCity;
using Xunit;

namespace Sim.LiveCity.Tests;

// LIVECITY-REROUTING T1/T2 (docs/LIVECITY-REROUTING-TASKS.md; design docs/LIVECITY-REROUTING-DESIGN.md).
// The engine's device.rerouting port (P1E, golden-pinned) reaches the LiveCity hosts through the
// spliced config XML; these tests pin the T1 inertness/enablement conditions and the T2 determinism +
// non-vacuity conditions.
//
// ENV HAZARD (CLAUDE.md measurement discipline #10): LIVECITY_REROUTE / LIVECITY_REROUTE_PERIOD /
// LIVECITY_REROUTE_PROB are process-global and the ctor reads them as overrides. These tests set cfg
// fields directly and assume the vars are UNSET in the test process (they are in CI; a shell that
// exports them will fail the OFF test loudly, which is the correct failure direction).
public class LiveCityReroutingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private static LiveCityConfig MakeConfig(double period, double prob, int cars)
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.ReroutePeriodSeconds = period;
        cfg.RerouteProbability = prob;
        cfg.CarTargetConcurrent = cars;
        return cfg;
    }

    // T1.1 (inertness): with the device off (the default), the engine never installs a periodic
    // reroute -- the counter staying 0 over a real simulated stretch is the observable half of the
    // "no splice => byte-identical engine config" condition (the byte-level half is pinned by every
    // pre-existing LiveCity test still passing on the same build).
    [Fact]
    public void DeviceOff_InstallsNoPeriodicReroutes()
    {
        using var sim = new LiveCitySim(MakeConfig(period: 0.0, prob: 1.0, cars: 120));
        for (var i = 0; i < 240; i++)
        {
            sim.Step();
        }

        Assert.Equal(0, sim.PeriodicRerouteCount);
    }

    // T2 (determinism + non-vacuity): two identically-configured runs with the device ON produce
    // byte-identical car streams AND a non-zero install count (a vacuously-deterministic
    // never-fired device would pass the first assertion alone). Congestion is what makes recomputed
    // routes actually differ, hence the elevated car target; the demo box is a grid, so
    // alternatives exist.
    [Fact]
    public void DeviceOn_IsDeterministic_AndActuallyReroutes()
    {
        static (string Stream, long Count) Run()
        {
            using var sim = new LiveCitySim(MakeConfig(period: 30.0, prob: 1.0, cars: 300));
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 500; i++)
            {
                sim.Step();
                if (i % 10 == 0)
                {
                    foreach (var c in sim.WitnessAuthoritative())
                    {
                        sb.Append(c.DefId).Append('|').Append(c.LaneId).Append('|')
                          .Append(c.Pos.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                          .Append(c.Speed.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                    }
                }
            }

            return (sb.ToString(), sim.PeriodicRerouteCount);
        }

        var a = Run();
        var b = Run();

        Assert.True(a.Count > 0,
            $"expected the enabled device to install at least one periodic reroute over 250 simulated " +
            $"seconds at 300 cars (period 30 s, probability 1.0); got {a.Count} -- either the splice " +
            "did not reach the engine or no congestion-driven alternative ever won");
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Stream, b.Stream);
    }
}
