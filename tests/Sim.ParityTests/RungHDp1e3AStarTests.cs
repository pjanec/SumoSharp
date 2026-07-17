using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// P1E-3 (HIGH-DENSITY-P1E-DESIGN.md §1D, §9) -- NetworkRouter.RouteAStar must return the SAME
// optimal path (same edge sequence AND same total cost) as the existing Dijkstra Route on the
// same injected cost function, for both the default free-flow cost and a cost function that
// makes the usual shortcut expensive enough to flip the optimum to the alternate.
//
// Net used: scenarios/_fixtures/routing-diamond/net.net.xml -- the same committed fixture
// RungB2RouterTests validates plain Dijkstra against (a diamond with two OD paths between SA and
// DE: the "top" shortcut AB+BD, 505.07m each way, vs. the "bottom" alternate AC+CD, 634.63m each
// way; all edges share speed 13.89 m/s). It already has >=2 paths between an OD pair, satisfying
// the task's requirement to reuse a net with alternate routes rather than construct a new one.
public class RungHDp1e3AStarTests
{
    private static readonly string FixtureDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "routing-diamond");

    // Parsed once (not per Route/DefaultCost call) -- DefaultCost below is invoked many times per
    // search, and re-parsing the net.xml on every edge relaxation would be needlessly wasteful.
    private static readonly NetworkModel Network =
        NetworkParser.Parse(Path.Combine(FixtureDir, "net.net.xml"));

    private static NetworkRouter LoadRouter() => new(Network);

    // (a) Default free-flow cost (length / max lane speed, all edges 13.89 m/s): the shortcut
    // (AB/BD) is unambiguously cheaper than the alternate (AC/CD), so both routers must agree on
    // the shortcut, with identical total cost.
    [Fact]
    public void AStar_MatchesDijkstra_OnDefaultFreeFlowCost()
    {
        var router = LoadRouter();

        var dijkstra = router.Route("SA", "DE");
        var astar = router.RouteAStar("SA", "DE");

        Assert.NotNull(dijkstra);
        Assert.NotNull(astar);
        Assert.Equal(dijkstra, astar);
        Assert.Contains("AB", astar!);
        Assert.Contains("BD", astar!);

        AssertSameTotalCost(router, dijkstra!, astar!, DefaultCost);
    }

    // (b) An injected cost function that makes the shortcut (AB, BD) artificially expensive --
    // the SUMO-faithful analog of a congested edge's smoothed travel time rising above the
    // alternate's -- so the optimum FLIPS to the bottom alternate (AC/CD). Both Dijkstra and A*
    // must pick the alternate and agree exactly.
    [Fact]
    public void AStar_MatchesDijkstra_WhenInjectedCostFlipsOptimalToAlternate()
    {
        var router = LoadRouter();
        Func<string, double> congestedCost = edgeId =>
            edgeId is "AB" or "BD" ? DefaultCost(edgeId) * 100.0 : DefaultCost(edgeId);

        var dijkstra = router.Route("SA", "DE", congestedCost);
        var astar = router.RouteAStar("SA", "DE", congestedCost);

        Assert.NotNull(dijkstra);
        Assert.NotNull(astar);
        Assert.Equal(dijkstra, astar);

        // Confirm the flip actually happened (the feature is genuinely exercised, not a no-op):
        // both routers must now take the bottom alternate, not the (artificially expensive) top
        // shortcut.
        Assert.Contains("AC", astar!);
        Assert.Contains("CD", astar!);
        Assert.DoesNotContain("AB", astar!);
        Assert.DoesNotContain("BD", astar!);

        AssertSameTotalCost(router, dijkstra!, astar!, congestedCost);
    }

    // A* with the avoid-set overload must still agree with Dijkstra's avoid-set overload (the
    // generalization didn't break the existing B3 avoid-edges seam either).
    [Fact]
    public void AStar_MatchesDijkstra_WithAvoidSet()
    {
        var router = LoadRouter();
        var avoid = new HashSet<string> { "BD" };

        var dijkstra = router.Route("SA", "DE", DefaultCost, avoid);
        var astar = router.RouteAStar("SA", "DE", DefaultCost, avoid);

        Assert.NotNull(dijkstra);
        Assert.NotNull(astar);
        Assert.Equal(dijkstra, astar);
        Assert.Equal(new[] { "SA", "AC", "CD", "DE" }, astar);
    }

    // Free-flow cost identical to NetworkRouter's own private default (length / max lane speed) --
    // duplicated here (not reflection) so the test constructs its "expensive shortcut" scenario
    // independently of the production implementation.
    private static double DefaultCost(string edgeId)
    {
        var edge = Network.EdgesById[edgeId];
        var length = edge.Lanes[0].Length;
        var speed = edge.Lanes.Max(l => l.Speed);
        return length / speed;
    }

    private static void AssertSameTotalCost(
        NetworkRouter router, IReadOnlyList<string> dijkstraPath, IReadOnlyList<string> astarPath,
        Func<string, double> cost)
    {
        var dijkstraTotal = TotalCost(dijkstraPath, cost);
        var astarTotal = TotalCost(astarPath, cost);
        Assert.Equal(dijkstraTotal, astarTotal, precision: 6);
        _ = router; // router unused beyond the paths already computed; kept for call-site clarity.
    }

    // Total cost of a path = sum of edgeCost over every edge AFTER the first (the origin edge
    // itself is never "entered", matching Route/RouteAStar's own relaxation, which only ever
    // calls edgeCost on a successor being relaxed INTO, never on fromEdge).
    private static double TotalCost(IReadOnlyList<string> path, Func<string, double> cost)
    {
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            total += cost(path[i]);
        }

        return total;
    }

    // Mirrors RungB2RouterTests.RepoRoot(): resolve the repo root by walking up from the test
    // assembly's output directory to find Traffic.sln.
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
