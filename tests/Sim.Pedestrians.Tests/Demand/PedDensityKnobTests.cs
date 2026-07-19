using System;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Demand;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Demand;

// P8-4a (docs/PEDESTRIAN-P8-4-DENSITY-DESIGN.md §2/§5): the dialable ped-density knob. Proves the cap is
// monotone in the dial and 0 at d=0, the dial clamps to a safe ceiling (d>1 never exceeds the d=1 cap),
// the rate follows Little's law, walkable length is a sane positive anchor on the box, the knob is
// deterministic, and its output drives PedDemand's live population to the dialed cap.
public class PedDensityKnobTests
{
    private readonly ITestOutputHelper _out;

    public PedDensityKnobTests(ITestOutputHelper output) => _out = output;

    private const double MaxSpeed = 1.4;
    private const double Radius = 0.3;
    private const double ArriveRadius = 0.3;
    private const double ArrivalRadius = 0.5;
    private const double Dt = 0.1;
    private const double DwellSeconds = 0.5;

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Traffic.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string BoxDir() => Path.Combine(RepoRoot(), "scenarios", "_ped", "subarea-box");

    private static PedNetwork BoxNetwork() => PedNetworkParser.Load(Path.Combine(BoxDir(), "net.xml"));

    [Fact]
    public void WalkableLength_IsPositive_AndDeterministic()
    {
        var net = BoxNetwork();
        var l1 = PedDensityKnob.WalkableLengthKm(net);
        var l2 = PedDensityKnob.WalkableLengthKm(BoxNetwork());

        Assert.Equal(l1, l2, precision: 12);              // deterministic
        Assert.InRange(l1, 1.0, 200.0);                   // sane km for an 800x800 crop's sidewalks
        _out.WriteLine($"[P8-4a] box walkable length = {l1:F3} km over {net.Sidewalks.Count} sidewalks");
    }

    [Fact]
    public void Cap_IsMonotoneInDial_ZeroAtZero_AndRateFollowsLittlesLaw()
    {
        var net = BoxNetwork();
        const double meanTrip = 60.0;

        var r0 = PedDensityKnob.ForNetwork(net, 0.0, meanTripSeconds: meanTrip);
        Assert.Equal(0, r0.PopulationCap);                // empty at d=0
        Assert.Equal(0.0, r0.SpawnRatePerSecond);

        var last = -1;
        foreach (var d in new[] { 0.0, 0.1, 0.25, 0.5, 0.75, 1.0 })
        {
            var r = PedDensityKnob.ForNetwork(net, d, meanTripSeconds: meanTrip);
            Assert.True(r.PopulationCap >= last, $"cap not monotone at dial {d}: {r.PopulationCap} < {last}");
            Assert.Equal(r.PopulationCap / meanTrip, r.SpawnRatePerSecond, precision: 12); // Little's law
            last = r.PopulationCap;
        }

        Assert.True(last > 0, "full-dial cap should be positive on a real crop");
    }

    [Fact]
    public void Dial_ClampsToSafeCeiling_AboveOne()
    {
        var net = BoxNetwork();
        var atOne = PedDensityKnob.ForNetwork(net, 1.0);
        var above = PedDensityKnob.ForNetwork(net, 5.0);
        var negative = PedDensityKnob.ForNetwork(net, -3.0);

        Assert.Equal(atOne.PopulationCap, above.PopulationCap);      // never exceeds the safe ceiling
        Assert.Equal(1.0, above.DensityFraction);                   // clamped
        Assert.Equal(0, negative.PopulationCap);                    // clamped to 0
        Assert.Equal(0.0, negative.DensityFraction);
    }

    [Fact]
    public void AppliedToConfig_DrivesLivePopulationToTheDialedCap_AndNeverOver()
    {
        var net = BoxNetwork();
        var polygons = WalkablePolygonBaker.Bake(net);
        var nav = new SumoNavMesh(polygons, new SumoWalkableSpace(polygons));
        var manager = new PedLodManager(nav, new PedPublisher(), ArriveRadius, DwellSeconds);

        // Size the safe density so the dialed cap lands at a small, quickly-reachable number (independent of
        // the box's exact walkable length): aim cap ~= 10 at dial = 1.
        var lengthKm = PedDensityKnob.WalkableLengthKm(net);
        var safe = 10.0 / lengthKm;

        // Uniform O/D from two well-separated walkable points so routing is guaranteed (knob is orthogonal
        // to WHERE peds go; here we exercise only the cap/rate it dials).
        var a = polygons.OrderBy(p => p.Centroid.X + p.Centroid.Y).First().Centroid;
        var b = polygons.OrderByDescending(p => p.Centroid.X + p.Centroid.Y).First().Centroid;

        var baseConfig = new PedDemandConfig
        {
            Origins = new[] { a, b },
            Destinations = new[] { a, b },
            SpawnRatePerSecond = 0.0,   // overwritten by the knob
            PopulationCap = 0,          // overwritten by the knob
            Seed = 4242UL,
            MaxSpeed = MaxSpeed,
            Radius = Radius,
            ArrivalRadius = ArrivalRadius,
        };

        var config = PedDensityKnob.Apply(baseConfig, net, dial: 1.0, safePedsPerWalkableKm: safe, meanTripSeconds: 30.0);
        var r = PedDensityKnob.ForNetwork(net, 1.0, safe, meanTripSeconds: 30.0);
        Assert.Equal(r.PopulationCap, config.PopulationCap);
        Assert.Equal(r.SpawnRatePerSecond, config.SpawnRatePerSecond, precision: 12);
        Assert.True(config.PopulationCap is >= 8 and <= 12, $"expected cap ~10, got {config.PopulationCap}");

        // The knob preserved every non-density field (composes with the rest of the config).
        Assert.Equal(baseConfig.Seed, config.Seed);
        Assert.Equal(baseConfig.MaxSpeed, config.MaxSpeed);

        var demand = new PedDemand(config, nav, manager);
        var field = new InterestField();
        var noEntities = Array.Empty<WorldDisc>();
        var now = 0.0;
        var maxLive = 0;
        for (var i = 0; i < 1500; i++)
        {
            demand.Step(now, Dt, field, noEntities);
            now += Dt;
            maxLive = Math.Max(maxLive, demand.LiveCount);
            Assert.True(demand.LiveCount <= config.PopulationCap, $"live {demand.LiveCount} exceeded dialed cap {config.PopulationCap}");
        }

        Assert.Equal(config.PopulationCap, maxLive); // population climbed to exactly the dialed cap
        _out.WriteLine($"[P8-4a] dialed cap={config.PopulationCap} rate={config.SpawnRatePerSecond:F3}/s; peak live={maxLive}");
    }
}
