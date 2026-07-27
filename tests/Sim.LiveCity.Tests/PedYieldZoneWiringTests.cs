using System;
using System.Diagnostics;
using System.IO;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md §3.0 -- the HOST WIRING for the Task B-guard, verified without
// stepping the sim (construction only, so this is fast).
//
// The guard is engine-side and gated on a world-space zone the ENGINE tests ego's own position against.
// The demo's contract is that this zone is exactly the camera-driven LC-realism zone the viewer
// highlights: the region the user is looking at is the region where cars yield to pedestrians. Two ways
// that can silently break -- the ctor never arming it, or SetLcRealismZone moving the highlight while
// leaving the yield behind -- so both are pinned here. The LIVECITY_PEDYIELD=0 opt-out is pinned too,
// because DemoPedYieldInvariantTests' whole A/B baseline arm rests on it actually disabling the guard.
// Driven through LiveCityConfig.PedYieldEnabled, never the process environment -- xunit runs test classes
// in parallel and a global env flip corrupts concurrent tests.
public class PedYieldZoneWiringTests
{
    private readonly ITestOutputHelper _out;

    public PedYieldZoneWiringTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void YieldZoneIsArmedOnTheLcRealismZone_AndFollowsIt()
    {
        {
            var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
            cfg.PedYieldEnabled = true;
            using var sim = new LiveCitySim(cfg);

            // (a) the ctor arms it, on the LC-realism zone.
            _out.WriteLine($"ctor: lcZone=({sim.LcZoneX:F2},{sim.LcZoneY:F2}) r={sim.LcZoneRadius:F2}, " +
                           $"yieldZone=({sim.PedYieldZoneX:F2},{sim.PedYieldZoneY:F2}) r={sim.PedYieldZoneRadius:F2}");
            Assert.True(sim.LcZoneRadius > 0.0, "fixture sanity: the demo starts with a positive LC-realism zone");
            Assert.Equal(sim.LcZoneX, sim.PedYieldZoneX);
            Assert.Equal(sim.LcZoneY, sim.PedYieldZoneY);
            Assert.Equal(sim.LcZoneRadius, sim.PedYieldZoneRadius);

            // (b) it FOLLOWS the camera: moving the highlight moves the yield region with it.
            var newX = sim.LcZoneX + 137.0;
            var newY = sim.LcZoneY - 91.0;
            var newR = sim.LcZoneRadius + 25.0;
            sim.SetLcRealismZone(newX, newY, newR);
            Assert.Equal(newX, sim.PedYieldZoneX);
            Assert.Equal(newY, sim.PedYieldZoneY);
            Assert.Equal(newR, sim.PedYieldZoneRadius);
        }
    }

    [Fact]
    public void LivecityPedYieldZero_LeavesTheGuardDisarmed_EvenAfterTheCameraMoves()
    {
        {
            var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
            cfg.PedYieldEnabled = false;
            using var sim = new LiveCitySim(cfg);

            Assert.Equal(0.0, sim.PedYieldZoneRadius);

            // The opt-out is latched in the ctor, so a later camera push must NOT re-arm it behind the
            // flag's back -- otherwise the A/B baseline arm would quietly become a second fixed arm.
            sim.SetLcRealismZone(sim.LcZoneX + 10.0, sim.LcZoneY + 10.0, sim.LcZoneRadius + 10.0);
            Assert.Equal(0.0, sim.PedYieldZoneRadius);
        }
    }

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
            // fall through
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
}
