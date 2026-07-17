using System.Xml.Linq;
using Sim.Core;
using Sim.Harness;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// P0-C2 acceptance gate (docs/HIGH-DENSITY-P0-DESIGN.md "P0-C2 -- grounded design"): a
// departPos="stop" car whose sole <stop> targets a roadsideCapacity-based <parkingArea> (declared
// in an additional-file) must start AT the empty-area lot-0 position ON-lane, park for the stop
// duration, then Krauss-accelerate away -- mechanically identical to a plain lane <stop>, only the
// position is resolved from the parkingArea. scenarios/48-parking-depart: pa0 on e0_0,
// startPos=195/endPos=210/roadsideCapacity=5 -> lot-0 = 195 + (210-195)/5 = 198.0; the SUMO 1.20.0
// golden parks veh0 at pos=198.0 from t=0..10 then accelerates (2.6, 5.2, ...). SumoSharp must
// reproduce (lane, pos, speed) within tolerance.json.
//
// Plus offline unit tests over the new parkingArea registry + resolution: the lot-0 formula, that a
// <stop parkingArea> binds to the pa's lane at lot-0, and that a capacity-0 reference throws.
public class RungHDp0c2ParkingDepartParityTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "48-parking-depart");

    [Fact]
    public void ParkingDepart_LoadsViaCfg_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var actual = engine.Run(120);
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch, BuildFailureMessage(result));
    }

    // Engine-level sanity check on the parked-then-depart timeline: veh0 sits at exactly 198.0
    // (speed 0) through the 10s stop, then moves. Reads the SumoSharp trajectory directly (not the
    // golden) so it fails loudly if the on-lane stop hold ever stops reproducing the parked origin.
    [Fact]
    public void ParkingDepart_ParksAtLotZero_ThenDrivesOff()
    {
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));

        var traj = engine.Run(120);
        var points = traj.PointsFor("veh0");

        // Parked at lot-0 (198.0), speed 0, from t=0 through t=10 (11 samples).
        for (var t = 0; t <= 10; t++)
        {
            var p = points[t];
            Assert.Equal("e0_0", p.Lane);
            Assert.Equal(198.0, p.Pos, 3);
            Assert.Equal(0.0, p.Speed, 3);
        }

        // Drives off at t=11 (Krauss first-step accel 2.6 m/s).
        Assert.Equal(2.6, points[11].Speed, 3);
        Assert.True(points[11].Pos > 198.0);
    }

    // --- Offline unit tests: parkingArea registry + resolution -----------------------------------

    [Fact]
    public void ParkingArea_Lot0Position_MatchesSumoFormula()
    {
        var add = XDocument.Parse("""
            <additional>
                <parkingArea id="pa0" lane="e0_0" startPos="195.00" endPos="210.00" roadsideCapacity="5"/>
            </additional>
            """);

        var pa = Assert.Single(AdditionalFileParser.ParseParkingAreas(add.Root!, _ => 1000.0));
        Assert.Equal("pa0", pa.Id);
        Assert.Equal("e0_0", pa.LaneId);
        Assert.Equal(195.0, pa.StartPos, 6);
        Assert.Equal(210.0, pa.EndPos, 6);
        Assert.Equal(5, pa.RoadsideCapacity);
        // 195 + (210-195)/5 = 198.0
        Assert.Equal(198.0, pa.Lot0Position(), 6);
    }

    // endPos defaults to the lane length (NLTriggerBuilder.cpp:566-569) when the attribute is absent.
    [Fact]
    public void ParkingArea_EndPosDefaultsToLaneLength()
    {
        var add = XDocument.Parse("""
            <additional>
                <parkingArea id="pa0" lane="e0_0" startPos="10" roadsideCapacity="2"/>
            </additional>
            """);

        var pa = Assert.Single(AdditionalFileParser.ParseParkingAreas(add.Root!, laneId =>
        {
            Assert.Equal("e0_0", laneId);
            return 500.0;
        }));
        Assert.Equal(500.0, pa.EndPos, 6);
    }

    // A <stop parkingArea="X"/> parses with no lane= and carries only the parkingArea id; after
    // Engine resolution it binds to the pa's lane at the lot-0 position.
    [Fact]
    public void StopParkingArea_ParsesWithoutLane_AndResolvesToLaneAndLotZero()
    {
        var demand = DemandParser.ParseXml("""
            <routes>
                <vType id="car" vClass="passenger" sigma="0"/>
                <route id="r0" edges="e0"/>
                <vehicle id="veh0" type="car" route="r0" depart="0" departPos="stop" departSpeed="0">
                    <stop parkingArea="pa0" duration="10"/>
                </vehicle>
            </routes>
            """);

        var v = Assert.Single(demand.Vehicles);
        var stop = Assert.Single(v.Stops);
        Assert.Equal("pa0", stop.ParkingAreaId);
        Assert.Equal(string.Empty, stop.LaneId);
        Assert.Equal(10.0, stop.Duration, 6);
    }

    [Fact]
    public void Stop_WithNeitherLaneNorParkingArea_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DemandParser.ParseXml("""
            <routes>
                <vType id="car" vClass="passenger" sigma="0"/>
                <route id="r0" edges="e0"/>
                <vehicle id="veh0" type="car" route="r0" depart="0">
                    <stop duration="10"/>
                </vehicle>
            </routes>
            """));

        Assert.Contains("lane", ex.Message);
        Assert.Contains("parkingArea", ex.Message);
    }

    [Fact]
    public void ParkingArea_CapacityZero_Lot0PositionThrowsClearError()
    {
        var pa = new ParkingArea("pa0", "e0_0", 195.0, 210.0, 0);
        var ex = Assert.Throws<InvalidDataException>(() => pa.Lot0Position());
        Assert.Contains("pa0", ex.Message);
        Assert.Contains("roadsideCapacity", ex.Message);
    }

    private static string BuildFailureMessage(ComparisonResult result)
    {
        var lines = new List<string>
        {
            $"P0-C2 parking-depart parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}",
        };

        foreach (var attribute in result.Attributes)
        {
            lines.Add(
                $"  attribute={attribute.Attribute} maxAbsError={attribute.MaxAbsError} rmse={attribute.Rmse} withinTolerance={attribute.WithinTolerance}");
        }

        if (result.PresenceMismatches.Count > 0)
        {
            lines.Add("  presence mismatches:");
            foreach (var mismatch in result.PresenceMismatches)
            {
                lines.Add($"    {mismatch.Kind} vehicle={mismatch.VehicleId} time={mismatch.Time?.ToString() ?? "n/a"}");
            }
        }

        return string.Join(Environment.NewLine, lines);
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
