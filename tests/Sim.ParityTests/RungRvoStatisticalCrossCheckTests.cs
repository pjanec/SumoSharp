using Sim.Core;
using Sim.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Laneless direction (docs/LANELESS-DIRECTION.md), Stage 4: statistical / AGGREGATE cross-check of the
// laneless RVO model against a SUMO sublane run -- SUMO-grounding WITHOUT byte-chasing. The RVO model
// is our own (behavioural bar, not exact parity); this asserts it produces SUMO-COMPARABLE aggregate
// flow on a mixed/heterogeneous scenario (scenarios/65-mixed-sublane: interleaved fast + slow vehicles
// on a 7.2 m wide sublane lane, so the fast ones overtake laterally). We compare distribution-level
// aggregates -- mean speed and the lateral spread (posLat std) over all vehicle-steps -- NOT per-vehicle
// per-step positions. The tolerance is loose by design (a laneless model is not SUMO's sublane model);
// the point is order-of-magnitude, same-qualitative-behaviour grounding.
public class RungRvoStatisticalCrossCheckTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "65-mixed-sublane");
    private readonly ITestOutputHelper _out;

    public RungRvoStatisticalCrossCheckTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RvoAggregate_IsComparableToSumoSublane_MeanSpeedAndLateralSpread()
    {
        var engine = new Engine { LanelessRvo = true };
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(120);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));

        var (engMeanSpeed, engLatStd) = Aggregate(actual);
        var (sumoMeanSpeed, sumoLatStd) = Aggregate(golden);

        _out.WriteLine($"engine RVO : meanSpeed={engMeanSpeed:F4}  posLatStd={engLatStd:F4}");
        _out.WriteLine($"sumo sublane: meanSpeed={sumoMeanSpeed:F4}  posLatStd={sumoLatStd:F4}");

        // Mean speed: within 20% -- the aggregate throughput/flow is SUMO-comparable.
        var speedRelErr = Math.Abs(engMeanSpeed - sumoMeanSpeed) / sumoMeanSpeed;
        Assert.True(speedRelErr <= 0.20,
            $"mean speed diverges: engine {engMeanSpeed:F3} vs sumo {sumoMeanSpeed:F3} (rel err {speedRelErr:P1} > 20%)");

        // Lateral spread: both models spread vehicles across the wide lane to overtake -- the posLat std
        // should be the same order of magnitude (within a factor of ~2), confirming qualitatively
        // similar lateral usage rather than everyone glued to the centreline or pinned to one edge.
        Assert.True(engLatStd > 0.15, $"engine barely used the lateral width (posLatStd {engLatStd:F3}) -- expected overtaking spread");
        Assert.True(engLatStd < 2.0 * sumoLatStd + 0.3,
            $"engine lateral spread {engLatStd:F3} much larger than sumo {sumoLatStd:F3}");
    }

    // Distribution-level aggregate over every (vehicle, time) sample: mean speed + population std of posLat.
    private static (double MeanSpeed, double LatStd) Aggregate(TrajectorySet traj)
    {
        double sumSpeed = 0, sumLat = 0, sumLatSq = 0;
        var n = 0;
        foreach (var id in traj.VehicleIds)
        {
            for (var t = 0; t <= 120; t++)
            {
                if (!traj.TryGet(id, t, out var p))
                {
                    continue;
                }
                sumSpeed += p.Speed;
                sumLat += p.PosLat;
                sumLatSq += p.PosLat * p.PosLat;
                n++;
            }
        }

        if (n == 0)
        {
            return (0, 0);
        }
        var meanLat = sumLat / n;
        return (sumSpeed / n, Math.Sqrt(Math.Max(0, sumLatSq / n - meanLat * meanLat)));
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
