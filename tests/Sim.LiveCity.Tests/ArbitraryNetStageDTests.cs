using System;
using System.Diagnostics;
using System.IO;
using Sim.LiveCity;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §7, -TASKS.md D1: promote LiveCitySim's ctor-hardcoded
// ped-demand knobs (PedMaxSpeed/PedRadius/PedArrivalRadius/PedEnableWeave + the PedLivelinessConfig
// group) to LiveCityConfig fields, each defaulted to the EXACT former literal so `ForRepoRoot` (the
// demo) keeps building a byte-identical PedDemandConfig.
public class ArbitraryNetStageDTests
{
    // Same repo-root resolution as ArbitraryNetStageATests.RepoRoot / LiveCitySimTests.RepoRoot.
    private static string RepoRoot()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && Directory.Exists(Path.Combine(output, "scenarios")))
            {
                return output;
            }
        }
        catch
        {
            // fall through to the walk-up fallback
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios")) && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private static string RoadNetFixtureDir() => Path.Combine(RepoRoot(), "scenarios", "_ped", "roadnet_min");

    // ---- D1 success condition 1: the demo config's surfaced fields equal the FORMER ctor literals ----
    // (design §7 / -TASKS.md D1: "MaxSpeed=1.3, Radius=0.3, ArrivalRadius=0.6, EnableWeave=true", plus
    // the PedLivelinessConfig block "PauseProbability=0.15, MinPauseSeconds=2.0, MaxPauseSeconds=5.0,
    // MaxPausesPerTrip=1, PauseAnimTag=idle").

    [Fact]
    public void ForRepoRoot_PedDemandKnobs_MatchLiveCitySimsFormerHardcodedLiterals()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());

        Assert.Equal(1.3, cfg.PedMaxSpeed);
        Assert.Equal(0.3, cfg.PedRadius);
        Assert.Equal(0.6, cfg.PedArrivalRadius);
        Assert.True(cfg.PedEnableWeave);

        Assert.Equal(0.15, cfg.PedPauseProbability);
        Assert.Equal(2.0, cfg.PedMinPauseSeconds);
        Assert.Equal(5.0, cfg.PedMaxPauseSeconds);
        Assert.Equal(1, cfg.PedMaxPausesPerTrip);
        Assert.Equal("idle", cfg.PedPauseAnimTag);
    }

    // A fresh LiveCityConfig() (not through either factory) must ALSO default to the former literals --
    // the field defaults themselves are the proof, independent of any factory wiring.
    [Fact]
    public void BareLiveCityConfig_PedDemandKnobDefaults_MatchFormerLiterals()
    {
        var cfg = new LiveCityConfig();

        Assert.Equal(1.3, cfg.PedMaxSpeed);
        Assert.Equal(0.3, cfg.PedRadius);
        Assert.Equal(0.6, cfg.PedArrivalRadius);
        Assert.True(cfg.PedEnableWeave);

        Assert.Equal(0.15, cfg.PedPauseProbability);
        Assert.Equal(2.0, cfg.PedMinPauseSeconds);
        Assert.Equal(5.0, cfg.PedMaxPauseSeconds);
        Assert.Equal(1, cfg.PedMaxPausesPerTrip);
        Assert.Equal("idle", cfg.PedPauseAnimTag);
    }

    // ---- Wiring proof: the ctor must actually READ these fields from `cfg`, not just declare them ----
    // (design §7: "the ctor reads them from cfg" instead of the inline literals). Overriding
    // PedArrivalRadius to an astronomically large value makes every freshly spawned ped already
    // "arrived" (within ArrivalRadius of its destination) at its very next DespawnArrivals check, so the
    // live population can never build up -- a sharp, easy-to-assert behavioural signature that only
    // appears if LiveCitySim's ctor is actually consuming cfg.PedArrivalRadius.
    [Fact]
    public void OverridingPedArrivalRadius_ChangesRoadNetFixtureBehaviour_ProvingCtorReadsFromCfg()
    {
        LiveCityConfig MakeCfg()
        {
            var cfg = LiveCityConfig.ForDataset(RoadNetFixtureDir());
            cfg.PedPopulationCap = 200;
            cfg.PedSpawnRatePerSecond = 20.0;
            cfg.CarTargetConcurrent = 20;
            return cfg;
        }

        var cfgDefault = MakeCfg();
        var cfgHugeArrival = MakeCfg();
        cfgHugeArrival.PedArrivalRadius = 1_000_000.0;

        using var simDefault = new LiveCitySim(cfgDefault);
        using var simHuge = new LiveCitySim(cfgHugeArrival);

        for (var i = 0; i < 200; i++)
        {
            simDefault.Step();
            simHuge.Step();
        }

        Assert.True(simDefault.PeakPeds > 5,
            $"expected the default ArrivalRadius to sustain a real live crowd, got PeakPeds={simDefault.PeakPeds}");
        Assert.True(simHuge.PeakPeds <= 1,
            $"an astronomically large ArrivalRadius should despawn peds almost immediately after spawn, " +
            $"capping LiveCount near 0/1 -- proving LiveCitySim's ctor actually reads cfg.PedArrivalRadius " +
            $"rather than the old hardcoded 0.6; got PeakPeds={simHuge.PeakPeds}");
    }

    // Same wiring proof for PedMaxSpeed: an extremely small speed cap should sharply cut the total
    // ground covered by walking peds over a fixed step budget vs. the default -- observable purely
    // through the public Sample()/PedSource contract (no new production API needed).
    [Fact]
    public void OverridingPedMaxSpeed_ChangesRoadNetFixtureDisplacement_ProvingCtorReadsFromCfg()
    {
        LiveCityConfig MakeCfg()
        {
            var cfg = LiveCityConfig.ForDataset(RoadNetFixtureDir());
            cfg.PedPopulationCap = 60;
            cfg.PedSpawnRatePerSecond = 20.0;
            cfg.CarTargetConcurrent = 20;
            return cfg;
        }

        var cfgDefault = MakeCfg();
        var cfgSlow = MakeCfg();
        cfgSlow.PedMaxSpeed = 0.02;

        using var simDefault = new LiveCitySim(cfgDefault);
        using var simSlow = new LiveCitySim(cfgSlow);

        double TotalDisplacement(LiveCitySim sim, int steps)
        {
            var last = new System.Collections.Generic.Dictionary<int, (double X, double Y)>();
            var total = 0.0;
            for (var i = 0; i < steps; i++)
            {
                sim.Step();
                var snap = sim.Sample();
                foreach (var p in snap.Peds)
                {
                    if (last.TryGetValue(p.Id, out var prev))
                    {
                        var dx = p.X - prev.X;
                        var dy = p.Y - prev.Y;
                        total += Math.Sqrt((dx * dx) + (dy * dy));
                    }

                    last[p.Id] = (p.X, p.Y);
                }
            }

            return total;
        }

        var displacementDefault = TotalDisplacement(simDefault, 150);
        var displacementSlow = TotalDisplacement(simSlow, 150);

        Assert.True(displacementDefault > 0.0, "expected the default-speed crowd to actually move");
        Assert.True(displacementSlow < displacementDefault * 0.25,
            $"expected a 0.02 m/s speed cap to cut total ped displacement to well under the default's, " +
            $"got default={displacementDefault:F2} slow={displacementSlow:F2} -- proving LiveCitySim's ctor " +
            $"reads cfg.PedMaxSpeed rather than the old hardcoded 1.3");
    }
}
