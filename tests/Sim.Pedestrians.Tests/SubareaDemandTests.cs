using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.Core.Orca;
using Sim.Pedestrians;
using Xunit;

namespace Sim.Pedestrians.Tests;

// P8-3a (docs/PEDESTRIAN-P8-3-DEMAND-DESIGN.md): SubareaDemand -- the weighted O->D endpoint set. Proves the
// weighted draw respects endpoint weights, every endpoint is a legitimate fringe/POI edge (appearance
// legitimacy by construction), fringe edges resolve onto the walkable surface, and the draw is deterministic.
public class SubareaDemandTests
{
    private static string BoxDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Traffic.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, "scenarios", "_ped", "subarea-box");
    }

    [Fact]
    public void WeightedDraw_RespectsEndpointWeights()
    {
        // Two POIs, weights 1 : 3 -> the second should be drawn ~3x as often.
        var pois = new[]
        {
            new PedPoi("a", PedPoiKind.Venue, new Vec2(0, 0), "eA", 1.0),
            new PedPoi("b", PedPoiKind.Venue, new Vec2(1, 1), "eB", 3.0),
        };
        var demand = SubareaDemand.Build(pois, Array.Empty<(string, Vec2)>());

        var rng = VehicleRng.SeedFor(123, 0);
        int a = 0, b = 0;
        for (var i = 0; i < 40000; i++)
        {
            if (demand.DrawWeighted(ref rng).EdgeId == "eA")
            {
                a++;
            }
            else
            {
                b++;
            }
        }

        var bShare = (double)b / (a + b);
        Assert.InRange(bShare, 0.73, 0.77); // expected 0.75
    }

    [Fact]
    public void AllZeroWeights_FallBackToUniform_Deterministically()
    {
        var pois = new[]
        {
            new PedPoi("a", PedPoiKind.DwellSpot, new Vec2(0, 0), "eA", 0.0),
            new PedPoi("b", PedPoiKind.DwellSpot, new Vec2(1, 1), "eB", 0.0),
        };
        var demand = SubareaDemand.Build(pois, Array.Empty<(string, Vec2)>());
        Assert.Equal(0.0, demand.TotalWeight);

        var rng = VehicleRng.SeedFor(9, 0);
        var counts = new int[2];
        for (var i = 0; i < 20000; i++)
        {
            counts[demand.DrawWeightedIndex(ref rng)]++;
        }

        // Uniform fallback -> roughly even, no index out of range.
        Assert.InRange((double)counts[0] / 20000, 0.45, 0.55);
    }

    [Fact]
    public void BuiltFromBox_AllEndpointsLegitimate_FringeOnWalkableSurface_AndDeterministic()
    {
        var pois = PedPoiReader.LoadJson(Path.Combine(BoxDir(), "pois.json"));
        var network = PedNetworkParser.Load(Path.Combine(BoxDir(), "net.xml"));
        var manifest = SubareaManifest.Load(Path.Combine(BoxDir(), "manifest.json"));

        var fringe = SubareaDemand.FringeEndpointsFromNetwork(network, manifest.WalkableFringeEdges);
        Assert.Equal(48, fringe.Count); // all 48 walkable-fringe edges resolve to a sidewalk point

        // Fringe points sit on the walkable surface -> inside the 0,0 -> 800,800 box (small tolerance).
        foreach (var (_, pos) in fringe)
        {
            Assert.InRange(pos.X, -5.0, 805.0);
            Assert.InRange(pos.Y, -5.0, 805.0);
        }

        var demand = SubareaDemand.Build(pois, fringe, fringeWeight: 1.0);
        Assert.Equal(pois.Count + fringe.Count, demand.Count); // 159 + 48 = 207
        Assert.True(demand.TotalWeight > 0.0);

        // Every endpoint is a POI edge or a fringe edge -> spawn/despawn legitimate by construction.
        var poiEdges = pois.Select(p => p.Edge).ToHashSet();
        var fringeEdges = manifest.WalkableFringeEdges.ToHashSet();
        foreach (var e in demand.Endpoints)
        {
            Assert.True(poiEdges.Contains(e.EdgeId) || fringeEdges.Contains(e.EdgeId),
                $"endpoint edge {e.EdgeId} is neither a POI edge nor a fringe edge");
        }

        // Deterministic: identical seed -> identical draw sequence.
        var r1 = VehicleRng.SeedFor(7, 0);
        var r2 = VehicleRng.SeedFor(7, 0);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(demand.DrawWeightedIndex(ref r1), demand.DrawWeightedIndex(ref r2));
        }
    }
}
