using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-API.md §4 (D5): the handle-based external-obstacle API and its generational ObstacleStore.
// The B1/B5/B6 rungs already prove the BEHAVIOUR via the (now transitional) string-keyed overloads;
// this file proves the new handle-based primary surface is (1) byte-equivalent to the string path and
// (2) enforces the generational stale-handle contract. Behavioural/property tests, not golden-FCD.
public class RungB7ObstacleHandleApiTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "14-external-obstacle");

    private const double ObstacleFrontPos = 250.0;
    private const double ObstacleLength = 5.0;
    private const double ObstacleBackPos = ObstacleFrontPos - ObstacleLength; // 245
    private const double ExpectedSteadyPos = 242.499;
    private const double LaneMaxSpeed = 13.89;

    private static Engine Load()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        return engine;
    }

    // The handle-based AddObstacle produces a trajectory BYTE-IDENTICAL to the string-keyed AddObstacle
    // for the same obstacle -- proving the new store/materialisation path changes nothing numerically.
    [Fact]
    public void HandleApi_ProducesIdenticalTrajectoryToStringApi()
    {
        var stringEngine = Load();
        stringEngine.AddObstacle("obs", "e0_0", frontPos: ObstacleFrontPos, length: ObstacleLength);
        var stringPoints = stringEngine.Run(60).PointsFor("follower");

        var handleEngine = Load();
        var lane = handleEngine.GetLane("e0_0");
        handleEngine.AddObstacle(lane, frontPos: ObstacleFrontPos, length: ObstacleLength);
        var handlePoints = handleEngine.Run(60).PointsFor("follower");

        Assert.Equal(stringPoints.Count, handlePoints.Count);
        foreach (var (time, sp) in stringPoints)
        {
            Assert.True(handlePoints.TryGetValue(time, out var hp), $"missing follower point at t={time}");
            // Bit-exact: same inputs through the same math -> identical doubles.
            Assert.Equal(sp.Pos, hp.Pos);
            Assert.Equal(sp.Speed, hp.Speed);
        }

        // Sanity: the shared expected steady state (so this test also anchors the absolute behaviour).
        var last = handlePoints.Values.Last();
        Assert.Equal(ExpectedSteadyPos, last.Pos, precision: 3);
        Assert.Equal(0.0, last.Speed, precision: 3);
    }

    // Removing an obstacle by handle, then calling UpdateObstacle on that STALE handle, is an inert
    // no-op (the generation no longer matches) -- the moved obstacle never reappears, so the follower
    // runs free. Proves the generational invalidation, not just "removed from the map".
    [Fact]
    public void StaleHandleAfterRemove_UpdateIsInert()
    {
        var engine = Load();
        var lane = engine.GetLane("e0_0");
        var h = engine.AddObstacle(lane, frontPos: ObstacleFrontPos, length: ObstacleLength);

        engine.RemoveObstacle(h);
        // A stale correction that tries to move the (removed) obstacle somewhere else must NOT resurrect
        // it or write into a recycled slot.
        engine.UpdateObstacle(h, frontPos: 100.0, speed: 0.0);

        var points = engine.Run(60).PointsFor("follower");
        var last = points.Values.Last();

        // No obstacle anywhere -> free-flow, driving well past both 100 and the former 245.
        Assert.Equal(LaneMaxSpeed, last.Speed, precision: 2);
        Assert.True(last.Pos > ObstacleBackPos, $"follower should be past {ObstacleBackPos}: pos={last.Pos}");
        Assert.All(points.Values.Skip(1), p => Assert.True(p.Speed > 0.0, $"unexpected stop at t={p.Time}"));
    }

    // A recycled slot gets a fresh generation, so a re-added obstacle's handle never equals the removed
    // one; and after ClearObstacles an outstanding handle is stale (its Update is inert).
    [Fact]
    public void RecycledSlot_GetsFreshGeneration_AndClearInvalidatesHandles()
    {
        var engine = Load();
        var lane = engine.GetLane("e0_0");

        var h1 = engine.AddObstacle(lane, frontPos: ObstacleFrontPos, length: ObstacleLength);
        engine.RemoveObstacle(h1);
        var h2 = engine.AddObstacle(lane, frontPos: ObstacleFrontPos, length: ObstacleLength);

        // Same slot index reused, but a bumped generation -> distinct handle.
        Assert.NotEqual(h1, h2);

        engine.ClearObstacles();
        // h2 is now stale; this correction must be inert (no obstacle in the store at all).
        engine.UpdateObstacle(h2, frontPos: 100.0, speed: 0.0);

        var last = engine.Run(60).PointsFor("follower").Values.Last();
        Assert.Equal(LaneMaxSpeed, last.Speed, precision: 2);
        Assert.True(last.Pos > ObstacleBackPos, $"follower should be past {ObstacleBackPos}: pos={last.Pos}");
    }

    // The moving-obstacle handle overload dead-reckons exactly like its string counterpart.
    [Fact]
    public void MovingObstacleHandleApi_MatchesStringApi()
    {
        var stringEngine = Load();
        stringEngine.AddMovingObstacle("obs", "e0_0", frontPos: ObstacleFrontPos, length: ObstacleLength,
            speed: 2.0, maxDecel: 4.5);
        var stringPoints = stringEngine.Run(60).PointsFor("follower");

        var handleEngine = Load();
        var lane = handleEngine.GetLane("e0_0");
        handleEngine.AddMovingObstacle(lane, frontPos: ObstacleFrontPos, length: ObstacleLength,
            speed: 2.0, maxDecel: 4.5);
        var handlePoints = handleEngine.Run(60).PointsFor("follower");

        Assert.Equal(stringPoints.Count, handlePoints.Count);
        foreach (var (time, sp) in stringPoints)
        {
            Assert.True(handlePoints.TryGetValue(time, out var hp), $"missing follower point at t={time}");
            Assert.Equal(sp.Pos, hp.Pos);
            Assert.Equal(sp.Speed, hp.Speed);
        }
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
