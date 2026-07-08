using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// C2-i: additive, byte-identical rung -- offline unit tests for `NetworkModel.ComputeBestLanes`,
// the scoped port of `MSVehicle::updateBestLanes` / `struct LaneQ`
// (sumo/src/microsim/MSVehicle.cpp:5744-6063, sumo/src/microsim/MSVehicle.h:865-886; see
// NetworkModel.cs's `LaneContinuation`/`ComputeBestLanes` doc comments for the exact
// single-look-ahead scope and the deferred multi-edge cumulative-length recursion). This test
// exercises ONLY the new ingest-layer computation -- it touches no engine/reducer/LC code, so it
// makes no behavioral change to any existing scenario (best-lane data is inert until C2-ii wires
// it into the strategic lane-change decision).
public class RungC2iBestLanesTests
{
    private const double Tolerance = 1e-9;

    // --- Discriminating case: scenarios/18-strategic-turnlane -----------------------------------
    // E1 (2 lanes, A->B, 500m) -> E2 (1 lane, B->C); ONLY E1_1 (index 1, left) connects to E2 (via
    // :B_0_0); E1_0 (index 0, right) is a drop lane with no onward <connection>. Route "E1 E2".

    [Fact]
    public void Scenario18_E1_DropLane_HasNoContinuation_AndOffsetPointsLeftToE1_1()
    {
        var network = LoadScenario18();
        var routeEdges = new[] { "E1", "E2" };

        var lanes = network.ComputeBestLanes(routeEdges, "E1");
        var e1_0 = Assert.Single(lanes, l => l.LaneIndex == 0);

        Assert.False(e1_0.AllowsContinuation);
        // E1_1 (index 1) is E1_0's LeftNeighbor (NetworkParser: LeftNeighbor == Index+1) -- the
        // signed offset toward it from E1_0 must be POSITIVE under this repo's left==+1
        // convention, matching SUMO's own bestLaneOffset = bestThisIndex - index sign
        // (MSVehicle.cpp:5973) for a target lane at a HIGHER index.
        Assert.Equal(1, e1_0.BestLaneOffset);
        Assert.Equal(496.00, e1_0.Length, Tolerance);
    }

    [Fact]
    public void Scenario18_E1_ContinuingLane_AllowsContinuation_ZeroOffset()
    {
        var network = LoadScenario18();
        var routeEdges = new[] { "E1", "E2" };

        var lanes = network.ComputeBestLanes(routeEdges, "E1");
        var e1_1 = Assert.Single(lanes, l => l.LaneIndex == 1);

        Assert.True(e1_1.AllowsContinuation);
        Assert.Equal(0, e1_1.BestLaneOffset);
        Assert.Equal(496.00, e1_1.Length, Tolerance);
    }

    [Fact]
    public void Scenario18_SignConvention_MatchesLeftNeighbor()
    {
        var network = LoadScenario18();
        var edge = network.EdgesById["E1"];
        var e1_0 = edge.Lanes.Single(l => l.Index == 0);
        var e1_1 = edge.Lanes.Single(l => l.Index == 1);

        // E1_1 is E1_0's LeftNeighbor (by handle) -- confirms the fixture's own left/right
        // convention before asserting BestLaneOffset's sign matches it above.
        Assert.Equal(e1_1.Handle, e1_0.LeftNeighbor);
    }

    [Fact]
    public void Scenario18_E2_LastRouteEdge_EveryLaneContinues_ZeroOffset()
    {
        var network = LoadScenario18();
        var routeEdges = new[] { "E1", "E2" };

        var lanes = network.ComputeBestLanes(routeEdges, "E2");
        var e2_0 = Assert.Single(lanes);

        Assert.True(e2_0.AllowsContinuation);
        Assert.Equal(0, e2_0.BestLaneOffset);
        Assert.Equal(496.00, e2_0.Length, Tolerance);
    }

    // --- Inert control: scenarios/11-priority-junction (single-lane-per-edge, multi-edge) -------
    // Proves that for an EXISTING parity scenario (single lane per edge everywhere), the best-lane
    // offset is 0 on every lane of every route edge -- i.e. C2-ii built on this data will be
    // inert/byte-identical here, since there is never more than one lane to choose between.

    [Fact]
    public void PriorityJunction_MinorRoute_EveryLane_ZeroOffset_AllowsContinuation()
    {
        var network = LoadPriorityJunction();
        var routeEdges = new[] { "SJ", "JN" };

        foreach (var edgeId in routeEdges)
        {
            var lanes = network.ComputeBestLanes(routeEdges, edgeId);
            foreach (var lane in lanes)
            {
                Assert.True(lane.AllowsContinuation);
                Assert.Equal(0, lane.BestLaneOffset);
            }
        }
    }

    [Fact]
    public void PriorityJunction_MajorRoute_EveryLane_ZeroOffset_AllowsContinuation()
    {
        var network = LoadPriorityJunction();
        var routeEdges = new[] { "WJ", "JE" };

        foreach (var edgeId in routeEdges)
        {
            var lanes = network.ComputeBestLanes(routeEdges, edgeId);
            foreach (var lane in lanes)
            {
                Assert.True(lane.AllowsContinuation);
                Assert.Equal(0, lane.BestLaneOffset);
            }
        }
    }

    private static NetworkModel LoadScenario18() =>
        NetworkParser.Parse(Path.Combine(RepoRoot(), "scenarios", "18-strategic-turnlane", "net.net.xml"));

    private static NetworkModel LoadPriorityJunction() =>
        NetworkParser.Parse(Path.Combine(RepoRoot(), "scenarios", "11-priority-junction", "net.net.xml"));

    // Mirrors Rung9biJunctionGeometryTests.RepoRoot(): resolve the repo root by walking up from
    // the test assembly's runtime location, so the test does not depend on the working directory
    // `dotnet test` happens to be invoked from.
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
