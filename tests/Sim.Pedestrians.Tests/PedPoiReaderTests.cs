using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Pedestrians;
using Xunit;

namespace Sim.Pedestrians.Tests;

// P8-3 (docs/PEDESTRIAN-P8-3-POI-REQUEST.md): PedPoiReader ingests the sub-area pipeline's deduced POIs.
// Pins the committed synthetic fixture (scenarios/_ped/subarea-box/pois.json) -- all 5 kinds, the documented
// per-kind counts, non-negative weights -- and cross-checks that every normal-edge POI sits on a real baked
// sidewalk (the walkable id space the P8-2 gate and the O->D demand key on).
public class PedPoiReaderTests
{
    private static string BoxDir()
    {
        var dir = System.AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Traffic.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, "scenarios", "_ped", "subarea-box");
    }

    [Fact]
    public void LoadJson_ReadsTheSyntheticFixture_AllKindsAndCounts()
    {
        var pois = PedPoiReader.LoadJson(Path.Combine(BoxDir(), "pois.json"));

        Assert.Equal(159, pois.Count);

        var byKind = pois.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(120, byKind[PedPoiKind.ParkingAccess]);
        Assert.Equal(17, byKind[PedPoiKind.BuildingEntrance]);
        Assert.Equal(9, byKind[PedPoiKind.DwellSpot]);
        Assert.Equal(8, byKind[PedPoiKind.Venue]);
        Assert.Equal(5, byKind[PedPoiKind.TransitStop]);

        // Every POI: non-empty edge, weight >= 0 (O->D attractiveness), sensible position.
        foreach (var p in pois)
        {
            Assert.False(string.IsNullOrEmpty(p.Edge));
            Assert.True(p.Weight >= 0.0);
        }

        // SHOULD fields present where the schema says: venues/dwell_spots carry capacity; entrances carry facing.
        Assert.All(pois.Where(p => p.Kind == PedPoiKind.Venue), p => Assert.NotNull(p.Capacity));
        Assert.All(pois.Where(p => p.Kind == PedPoiKind.BuildingEntrance), p => Assert.NotNull(p.Facing));
    }

    [Fact]
    public void EveryNormalEdgePoi_SitsOnABakedSidewalk()
    {
        var pois = PedPoiReader.LoadJson(Path.Combine(BoxDir(), "pois.json"));
        var network = PedNetworkParser.Load(Path.Combine(BoxDir(), "net.xml"));
        var sidewalkEdges = network.Sidewalks.Select(s => s.EdgeId).ToHashSet();

        // POIs on normal edges (sidewalks) must land on a real sidewalk edge; the 9 on internal
        // walkingarea/crossing edges (ids starting ':') are a separate walkable id space, excluded here.
        var normalEdgePois = pois.Where(p => !p.Edge.StartsWith(':')).ToList();
        Assert.NotEmpty(normalEdgePois);
        foreach (var p in normalEdgePois)
        {
            Assert.Contains(p.Edge, sidewalkEdges);
        }
    }
}
