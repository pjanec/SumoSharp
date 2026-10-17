using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.LiveCity;
using Sim.Replication;
using Xunit;

namespace Sim.LiveCity.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H5, H6, H7, H9 — the live-city host seams for HROT's networked traffic node.
// Every one is default-off; these rails pin the new surfaces on a tiny vehicle-only net (two 100 m edges,
// no sidewalks => no pedestrians), read back through the host's own in-memory car wire (VehicleSource).
public class HrotLiveCitySeamTests
{
    private const string TwoEdgeNetXml = """
        <net>
          <edge id="v1" from="A" to="B">
            <lane id="v1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/>
          </edge>
          <edge id="v2" from="B" to="C">
            <lane id="v2_0" index="0" speed="13.9" length="100.0" shape="200.00,100.00 300.00,100.00"/>
          </edge>
          <connection from="v1" to="v2" fromLane="0" toLane="0"/>
        </net>
        """;

    private static (LiveCityConfig Cfg, string Dir) TwoEdgeConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-hrot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net.xml"), TwoEdgeNetXml);
        var cfg = LiveCityConfig.ForDataset(dir);
        cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1000; cfg.Y1 = 1000;
        cfg.CarTargetConcurrent = 6;
        return (cfg, dir);
    }

    private static void Run(LiveCityConfig cfg, string dir, int steps, Action<LiveCitySim> check)
    {
        try
        {
            using var sim = new LiveCitySim(cfg);
            for (var i = 0; i < steps; i++) sim.Step();
            check(sim);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── H5 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void H5_TheProvidersIds_AreTheLiveVehiclesIds()
    {
        var (cfg, dir) = TwoEdgeConfig();
        var next = 0;
        var issued = new List<string>();
        cfg.VehicleIdProvider = () =>
        {
            var id = "hrot-" + (++next);
            issued.Add(id);
            return id;
        };

        Run(cfg, dir, 40, sim =>
        {
            var names = sim.VehicleSource.Names.Values.ToList();
            Assert.NotEmpty(names);
            Assert.All(names, n => Assert.Contains(n, issued));
            Assert.Equal(names.Count, names.Distinct().Count());
        });
    }

    [Fact]
    public void H5_AProviderRepeatingALiveId_DoesNotCrashTheStep()
    {
        var (cfg, dir) = TwoEdgeConfig();
        cfg.VehicleIdProvider = () => "dup";

        Run(cfg, dir, 40, sim =>
        {
            var names = sim.VehicleSource.Names.Values.ToList();
            Assert.Contains("dup", names);
            Assert.Single(names, n => n == "dup"); // the live duplicate was refused, every time
        });
    }

    [Fact]
    public void H5_WithoutAProvider_TheGeneratedIdsAreUnchanged()
    {
        var (cfg, dir) = TwoEdgeConfig();

        Run(cfg, dir, 40, sim =>
        {
            var names = sim.VehicleSource.Names.Values.ToList();
            Assert.NotEmpty(names);
            Assert.All(names, n => Assert.StartsWith("__veh", n));
        });
    }

    // ── H6 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void H6_StartTime1000_TheClockAndThePublishedFramesStartAt1000()
    {
        var (cfg, dir) = TwoEdgeConfig();
        cfg.StartTime = 1000.0;
        try
        {
            using var sim = new LiveCitySim(cfg);
            Assert.Equal(1000.0, sim.Time);

            for (var i = 0; i < 20; i++) sim.Step();

            Assert.Equal(1000.0 + 20 * cfg.Dt, sim.Time, 9);
            var latest = sim.VehicleSource.LatestVehicleSampleTime;
            Assert.NotNull(latest);
            Assert.True(latest!.Value > 1000.0 && latest.Value <= sim.Time + 1e-9, $"latest frame time {latest}");
            Assert.All(
                sim.VehicleSource.History.Values.SelectMany(h => Enumerable.Range(0, h.Count).Select(k => h[k].TimestampSeconds)),
                t => Assert.True(t > 1000.0, $"sample time {t}"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void H6_StartTime0_IsUnchanged()
    {
        var (cfg, dir) = TwoEdgeConfig();
        try
        {
            using var sim = new LiveCitySim(cfg);
            Assert.Equal(0.0, sim.Time);

            for (var i = 0; i < 20; i++) sim.Step();

            Assert.Equal(20 * cfg.Dt, sim.Time, 9);
            Assert.True(sim.VehicleSource.LatestVehicleSampleTime is { } t && t <= 20 * cfg.Dt + 1e-9);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── H9 (+ H7 through the host) ──────────────────────────────────────────────────────────────

    [Fact]
    public void H9_TwoTypesWeighted1To0_EveryCarIsTheFirst_WithItsDimsAndTag()
    {
        var (cfg, dir) = TwoEdgeConfig();
        cfg.VehicleTypes = new[]
        {
            new LiveCityVehicleType("van", "delivery", Length: 6.0, Width: 2.2, MaxSpeed: 12.0, Weight: 1.0, ExternalType: 7001, Color: 0x112233),
            new LiveCityVehicleType("bike", "passenger", Length: 2.0, Width: 0.8, MaxSpeed: null, Weight: 0.0, ExternalType: 7002, Color: 0x445566),
        };

        Run(cfg, dir, 40, sim =>
        {
            var dims = sim.VehicleSource.Dims;
            Assert.NotEmpty(dims);
            Assert.All(dims.Values, d =>
            {
                Assert.Equal(6.0f, d.Length);
                Assert.Equal(2.2f, d.Width);
            });

            // H7: every announced car carries the entry's external type + colour.
            Assert.Equal(dims.Count, sim.VehicleSource.Tags.Count);
            Assert.All(sim.VehicleSource.Tags.Values, t => Assert.Equal(new VehicleTag(7001, 0x112233), t));
        });
    }

    [Fact]
    public void H9_AnEvenMix_SpawnsBothTypes()
    {
        var (cfg, dir) = TwoEdgeConfig();
        cfg.CarTargetConcurrent = 12;
        cfg.VehicleTypes = new[]
        {
            new LiveCityVehicleType("a", "passenger", 4.0, 1.7, null, 1.0, 1, 0x0000FF),
            new LiveCityVehicleType("b", "passenger", 5.0, 1.9, null, 1.0, 2, 0x00FF00),
        };
        var seen = new HashSet<long>();

        try
        {
            using var sim = new LiveCitySim(cfg);
            for (var i = 0; i < 200 && seen.Count < 2; i++)
            {
                sim.Step();
                foreach (var t in sim.VehicleSource.Tags.Values) seen.Add(t.ExternalType);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        Assert.Equal(new HashSet<long> { 1, 2 }, seen);
    }

    [Fact]
    public void H9_WithoutAMix_NoTagIsPublished_AndTheDefaultDimsAreThePassengers()
    {
        var (cfg, dir) = TwoEdgeConfig();

        Run(cfg, dir, 40, sim =>
        {
            Assert.NotEmpty(sim.VehicleSource.Dims);
            Assert.Empty(sim.VehicleSource.Tags);
            Assert.All(sim.VehicleSource.Dims.Values, d => Assert.Equal((5.0f, 1.8f), (d.Length, d.Width)));
        });
    }
}
