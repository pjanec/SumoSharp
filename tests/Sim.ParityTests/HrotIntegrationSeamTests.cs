using Sim.Core;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// docs/HROT-INTEGRATION-DESIGN.md H1 + H4 — the host seams HROT needs. Both are additive and default-off, so
// nothing here may move a golden; these rails pin the new surfaces themselves.
public class HrotIntegrationSeamTests
{
    // ── H1: <location> ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void H1_AGeoreferencedNet_ReportsItsProjectionAndOffset()
    {
        var net = NetworkParser.Parse(Path.Combine(RepoRoot(), "scenarios", "_ped", "georef_min", "scenario.net.xml"));

        Assert.NotNull(net.Location);
        Assert.True(net.Location!.IsGeoreferenced);
        Assert.StartsWith("+proj=utm +zone=32", net.Location.ProjParameter);
        Assert.Equal(-187497.01, net.Location.NetOffsetX, 6);
        Assert.Equal(-5046275.45, net.Location.NetOffsetY, 6);
        Assert.Equal(91849.56, net.Location.ConvBoundary.MinX, 6);
        Assert.Equal(74370.22, net.Location.ConvBoundary.MaxY, 6);
    }

    [Fact]
    public void H1_ANonGeoreferencedNet_SaysSo()
    {
        var net = NetworkParser.Parse(Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml"));

        Assert.NotNull(net.Location);
        Assert.False(net.Location!.IsGeoreferenced);
        Assert.Equal("!", net.Location.ProjParameter);
        Assert.Equal(-150.0, net.Location.NetOffsetX, 6);
        Assert.Equal(4750.0, net.Location.ConvBoundary.MaxX, 6);
    }

    [Fact]
    public void H1_ANetWithoutLocation_ParsesToNull()
    {
        var net = NetworkParser.ParseXml(
            "<net><edge id=\"e0\" from=\"a\" to=\"b\"><lane id=\"e0_0\" index=\"0\" speed=\"13.9\" length=\"100\" shape=\"0,0 100,0\"/></edge></net>");

        Assert.Null(net.Location);
    }

    // ── H4: a host-chosen vehicle id ─────────────────────────────────────────────────────────────

    private static string Net14 => Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle", "net.net.xml");

    [Fact]
    public void H4_AHostChosenId_IsTheVehiclesIdOnEverySurface()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        var h = e.SpawnVehicle(e.DefaultVType, new[] { "e0" }, vehicleId: "4711");

        e.Step();

        Assert.Equal(VehicleLifecycle.Active, e.GetLifecycle(h));
        Assert.Contains("4711", e.VehicleIds.ToArray());
    }

    [Fact]
    public void H4_ASecondLiveVehicleWithTheSameId_Throws()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        e.SpawnVehicle(e.DefaultVType, new[] { "e0" }, vehicleId: "4711");

        Assert.Throws<ArgumentException>(() => e.SpawnVehicle(e.DefaultVType, new[] { "e0" }, vehicleId: "4711"));
    }

    [Fact]
    public void H4_AfterTheVehicleIsGone_TheIdMayBeReused()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        var h = e.SpawnVehicle(e.DefaultVType, new[] { "e0" }, vehicleId: "4711");
        e.Step();
        Assert.True(e.Despawn(h));

        var again = e.SpawnVehicle(e.DefaultVType, new[] { "e0" }, vehicleId: "4711");

        Assert.NotEqual(VehicleLifecycle.Unknown, e.GetLifecycle(again));
    }

    [Fact]
    public void H4_WithoutAnId_TheGeneratedIdIsUnchanged()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        e.SpawnVehicle(e.DefaultVType, new[] { "e0" });

        e.Step();

        Assert.Contains("__veh0", e.VehicleIds.ToArray());
    }

    // ── H5: the routed spawn with a host-chosen id ───────────────────────────────────────────────

    [Fact]
    public void H5_TheRoutedSpawn_CarriesTheHostChosenId()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        e.SpawnVehicle(e.DefaultVType, "e0", "e0", vehicleId: "car-17");

        e.Step();

        Assert.Contains("car-17", e.VehicleIds.ToArray());
    }

    [Fact]
    public void H5_TheRoutedSpawn_RejectsALiveDuplicate_AndANullIdIsTheGeneratedOne()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        e.SpawnVehicle(e.DefaultVType, "e0", "e0", vehicleId: "car-17");

        Assert.Throws<ArgumentException>(() => e.SpawnVehicle(e.DefaultVType, "e0", "e0", vehicleId: "car-17"));

        e.SpawnVehicle(e.DefaultVType, "e0", "e0", vehicleId: null);
        for (var i = 0; i < 20 && !e.VehicleIds.ToArray().Contains("__veh0"); i++)
        {
            e.Step(); // the second car waits for a gap behind the first
        }

        Assert.Contains("__veh0", e.VehicleIds.ToArray());
    }

    // ── H9: a runtime vType's width reaches the vehicle's dims ───────────────────────────────────

    [Fact]
    public void H9_ARuntimeVTypeWidth_ReachesTheVehicleWidth_AndNullKeepsTheVClassDefault()
    {
        var e = new Engine();
        e.LoadNetwork(Net14);
        var wide = e.DefineVType(new VTypeParams { VClass = "passenger", Length = 9.0, Width = 2.7 }, id: "wide");
        var plain = e.DefineVType(new VTypeParams { VClass = "passenger" }, id: "plain");
        e.SpawnVehicle(wide, new[] { "e0" }, vehicleId: "w", departPos: 20.0);
        e.SpawnVehicle(plain, new[] { "e0" }, vehicleId: "p", departPos: 0.0);

        var ids = Array.Empty<string>();
        for (var i = 0; i < 10 && !(ids.Contains("w") && ids.Contains("p")); i++)
        {
            e.Step();
            ids = e.VehicleIds.ToArray();
        }

        var widths = e.VehicleWidths.ToArray();
        var lengths = e.VehicleLengths.ToArray();
        var w = Array.IndexOf(ids, "w");
        var p = Array.IndexOf(ids, "p");
        Assert.True(w >= 0 && p >= 0, string.Join(",", ids));
        Assert.Equal(2.7f, widths[w]);
        Assert.Equal(9.0f, lengths[w]);
        Assert.Equal(1.8f, widths[p]); // the passenger vClass default, unchanged
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above test assembly).");
    }
}
