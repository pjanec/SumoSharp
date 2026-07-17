using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// P1E-2 (HIGH-DENSITY-P1E-DESIGN.md §1C, §9) -- RerouteEdgeWeights is STANDALONE infrastructure
// (no Engine dependency; nothing in the running engine calls it yet -- that is P1E-4). These tests
// exercise it in complete isolation: seed, the exact incremental ring-buffer recurrence, the
// isDelayed one-way latch, and the effort floor.
//
// Reuses the routing-diamond fixture (scenarios/_fixtures/routing-diamond/net.net.xml, the same
// net RungB2RouterTests validates NetworkRouter against) purely for a ready-made, committed
// NetworkModel with known edge lengths/speeds -- every normal edge in it has speed 13.89 m/s;
// edge "AB" has length 505.07m, edge "AC" 634.63m (both used below by id, not by re-deriving the
// XML).
public class RungHDp1e2EdgeWeightsTests
{
    private const string EdgeAB = "AB";
    private const double LengthAB = 505.07;
    private const string EdgeAC = "AC";
    private const double FreeFlow = 13.89;

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot(), "scenarios", "_fixtures", "routing-diamond");

    private static NetworkModel LoadNetwork() =>
        NetworkParser.Parse(Path.Combine(FixtureDir, "net.net.xml"));

    [Fact]
    public void Seed_SmoothedSpeedEqualsFreeFlow()
    {
        var weights = new RerouteEdgeWeights(LoadNetwork(), adaptationSteps: 18);

        Assert.Equal(FreeFlow, weights.FreeFlowSpeed(EdgeAB), precision: 6);
        Assert.Equal(FreeFlow, weights.SmoothedSpeed(EdgeAB), precision: 6);
        Assert.False(weights.IsDelayed(EdgeAB));
    }

    [Fact]
    public void Seed_UntouchedEdge_EffortEqualsLengthOverMinOfFreeFlowAndVehicleMaxSpeed()
    {
        var weights = new RerouteEdgeWeights(LoadNetwork(), adaptationSteps: 18);

        // vehicleMaxSpeed slower than free-flow: the per-vClass minimum-travel-time floor wins.
        const double slowVehicle = 10.0;
        Assert.Equal(LengthAB / slowVehicle, weights.Effort(EdgeAB, slowVehicle), precision: 6);

        // vehicleMaxSpeed faster than free-flow: the free-flow-speed term wins (still floored by
        // the *edge's* free-flow speed, since the edge was never marked delayed).
        const double fastVehicle = 20.0;
        Assert.Equal(LengthAB / FreeFlow, weights.Effort(EdgeAB, fastVehicle), precision: 6);
    }

    // Ported EXACTLY from MSRoutingEngine::adaptEdgeEfforts (.cpp:245-248): smoothed +=
    // (curr - past[k]) / N; past[k] = curr; k advances once per Update call (shared across every
    // edge), not per edge. Uses a small N=4 so every intermediate value can be hand-computed.
    [Fact]
    public void RingBufferRecurrence_MatchesHandComputedIncrementalAverage()
    {
        const int n = 4;
        var weights = new RerouteEdgeWeights(LoadNetwork(), adaptationSteps: n);
        weights.MarkDelayed(EdgeAB);

        // Feed curr == freeFlow for many updates (more than one full ring cycle): since
        // curr == past[k] at every slot (every slot still seeded to freeFlow), the incremental
        // delta is exactly 0 every time -- smoothedSpeed must stay exactly at the free-flow seed.
        for (var i = 0; i < 2 * n + 3; i++)
        {
            weights.Update(edgeId => edgeId == EdgeAB ? FreeFlow : throw Unexpected(edgeId));
            Assert.Equal(FreeFlow, weights.SmoothedSpeed(EdgeAB), precision: 9);
        }

        // Feed a slower curr ONCE: every past[] slot is still exactly `FreeFlow` (the constant
        // feed above never changed any of them), so regardless of the current ring index, the
        // incremental step is (slow - FreeFlow) / N.
        const double slow = 5.0;
        weights.Update(edgeId => edgeId == EdgeAB ? slow : throw Unexpected(edgeId));
        var expectedAfterOne = FreeFlow + (slow - FreeFlow) / n;
        Assert.Equal(expectedAfterOne, weights.SmoothedSpeed(EdgeAB), precision: 9);

        // Feed the same slow value for the remaining (n - 1) updates to complete one full ring
        // cycle (n total slow samples, one per ring slot) -- every slot now holds `slow`, so the
        // moving average has fully converged to it: smoothedSpeed == slow exactly.
        for (var i = 0; i < n - 1; i++)
        {
            weights.Update(edgeId => edgeId == EdgeAB ? slow : throw Unexpected(edgeId));
        }

        Assert.Equal(slow, weights.SmoothedSpeed(EdgeAB), precision: 9);
    }

    [Fact]
    public void IsDelayedLatch_NeverMarked_IsNeverSampledOrUpdated()
    {
        var weights = new RerouteEdgeWeights(LoadNetwork(), adaptationSteps: 18);

        Assert.False(weights.IsDelayed(EdgeAC));

        // The sampling function throws if it is ever invoked for AC -- proving Update truly
        // skips a never-delayed edge rather than merely updating it with a value that happens to
        // equal free-flow.
        for (var i = 0; i < 25; i++)
        {
            weights.Update(edgeId => edgeId == EdgeAC ? throw Unexpected(edgeId) : FreeFlow);
        }

        Assert.False(weights.IsDelayed(EdgeAC));
        Assert.Equal(FreeFlow, weights.SmoothedSpeed(EdgeAC), precision: 9);
    }

    [Fact]
    public void IsDelayedLatch_OnceMarked_StaysLatchedForever()
    {
        var weights = new RerouteEdgeWeights(LoadNetwork(), adaptationSteps: 4);
        weights.MarkDelayed(EdgeAB);
        Assert.True(weights.IsDelayed(EdgeAB));

        // Recovering to free-flow (e.g. the edge emptied out again) must NOT reset the latch --
        // it keeps being updated every interval forever, per MSEdge::isDelayed's contract.
        for (var i = 0; i < 10; i++)
        {
            weights.Update(edgeId => FreeFlow);
        }

        Assert.True(weights.IsDelayed(EdgeAB));
    }

    [Fact]
    public void EffortFloor_HugeSmoothedSpeed_FloorsAtLengthOverVehicleMaxSpeed()
    {
        const int n = 2;
        var weights = new RerouteEdgeWeights(LoadNetwork(), adaptationSteps: n);
        weights.MarkDelayed(EdgeAB);

        // Drive smoothedSpeed far above any plausible vehicle max speed.
        const double huge = 1_000_000.0;
        for (var i = 0; i < n; i++)
        {
            weights.Update(edgeId => edgeId == EdgeAB ? huge : FreeFlow);
        }

        Assert.True(weights.SmoothedSpeed(EdgeAB) > 10_000.0);

        const double vehicleMaxSpeed = 13.89;
        Assert.Equal(LengthAB / vehicleMaxSpeed, weights.Effort(EdgeAB, vehicleMaxSpeed), precision: 6);
    }

    private static Exception Unexpected(string edgeId) =>
        new InvalidOperationException($"currentMeanSpeed must not be sampled for un-delayed edge '{edgeId}'.");

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
