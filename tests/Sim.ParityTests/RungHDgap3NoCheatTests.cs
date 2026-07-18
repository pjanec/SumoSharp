using Sim.Core;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// GAP-3 offline no-cheating audit (docs/SUMOSHARP-SERVE-PATH-DROP-IN.md §3, SERVE-PATH-PLAN.md):
// a C# port of `scripts/audit_nocheat.py`'s rule -- a vehicle may be BORN only on a FRINGE edge (a
// boundary stub) or a PARK edge (an edge hosting a <parkingArea>, entered via departPos="stop"),
// and DIE (tripinfo arrivalLane) only on a FRINGE edge or a PARK edge (a park-and-stay sink) --
// against the ENGINE's OWN output (LoadScenario + Run), so it needs no SUMO/sumolib and runs in
// the offline `dotnet test` loop. scenarios/68-serve-nocheat is a small synthetic "served box":
// eA (fringe stub, west) -> eB (interior, hosts parkingArea "pa_eB") -> eC (fringe stub, east).
// Three vehicles exercise every no-cheating shape: through0 (fringe->fringe, no parking),
// originPark0 (departPos="stop" born off-road in pa_eB, pulls out, exits at the east fringe), and
// sinkPark0 (born at the west fringe, drives in, parks in pa_eB forever -- never arrives, which is
// expected/allowed, not a violation).
public class RungHDgap3NoCheatTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "68-serve-nocheat");
    private const int Steps = 100; // matches config.sumocfg's <end value="100"/> at step-length 1s.

    [Fact]
    public void ServeNoCheat_EngineOwnOutput_ZeroBirthDeathFcdViolations()
    {
        // ---- Route-intent audit (mirrors audit_nocheat.py's "births" section: trusts the parking
        // assignment declared in the route file, exactly like the Python original reads rou.xml via
        // ElementTree rather than the engine). ----
        var network = NetworkParser.Parse(Path.Combine(ScenarioDir, "net.net.xml"));
        var demand = DemandParser.Parse(Path.Combine(ScenarioDir, "rou.rou.xml"));

        var (fringeEdges, parkEdges) = ClassifyEdges(network, ScenarioDir);
        Assert.True(fringeEdges.Count > 0, "test scenario has no fringe edges -- audit would be vacuous.");
        Assert.True(parkEdges.Count > 0, "test scenario has no park edges -- audit would be vacuous.");

        var vehicleInfo = new Dictionary<string, (string FirstEdge, string LastEdge, bool OriginPark, bool DestPark)>(StringComparer.Ordinal);
        var birthViolations = new List<string>();

        foreach (var v in demand.Vehicles)
        {
            var route = demand.RoutesById[v.RouteId];
            var firstEdge = route.Edges[0];
            var lastEdge = route.Edges[^1];
            var originPark = v.DepartPos.Kind == DepartPosSpec.Stop;

            // "a long-duration stop at the last edge is the destination sink" (audit_nocheat.py).
            var destPark = v.Stops.Count > 0
                && v.Stops[^1].ParkingAreaId is { } lastStopPa
                && lastStopPa == $"pa_{lastEdge}";

            vehicleInfo[v.Id] = (firstEdge, lastEdge, originPark, destPark);

            var birthOk = originPark ? parkEdges.Contains(firstEdge) : fringeEdges.Contains(firstEdge);
            if (!birthOk)
            {
                birthViolations.Add($"{v.Id}: first={firstEdge} originPark={originPark}");
            }
        }

        Assert.True(vehicleInfo.Count >= 3, $"expected >= 3 vehicles (through/origin/sink shapes), got {vehicleInfo.Count}.");
        Assert.Contains(vehicleInfo.Values, i => !i.OriginPark && !i.DestPark);   // through0
        Assert.Contains(vehicleInfo.Values, i => i.OriginPark);                  // originPark0
        Assert.Contains(vehicleInfo.Values, i => i.DestPark);                    // sinkPark0

        // ---- Run the ENGINE itself (no SUMO) and audit its OWN tripinfo + FCD trajectory. ----
        var engine = new Engine();
        engine.LoadScenario(Path.Combine(ScenarioDir, "config.sumocfg"));
        var traj = engine.Run(Steps);

        // ---- Deaths: from the engine's own CompletedTrips (its tripinfo-equivalent). ----
        var deathViolations = new List<string>();
        var completedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trip in engine.CompletedTrips)
        {
            completedIds.Add(trip.Id);
            if (!vehicleInfo.TryGetValue(trip.Id, out var info))
            {
                continue;
            }

            var arrivalEdge = EdgeOfLane(trip.ArrivalLane);
            var deathOk = info.DestPark ? parkEdges.Contains(arrivalEdge) : fringeEdges.Contains(arrivalEdge);
            if (!deathOk)
            {
                deathViolations.Add($"{trip.Id}: arrivalLane={trip.ArrivalLane} edge={arrivalEdge} destPark={info.DestPark}");
            }
        }

        // The park-and-stay sink must NOT have completed (still parked at run end) -- if it did,
        // the scenario's timing no longer exercises the "never leaves" shape this test requires.
        var sinkId = vehicleInfo.Single(kv => kv.Value.DestPark).Key;
        Assert.DoesNotContain(sinkId, completedIds);
        // The through/origin vehicles MUST have completed, or the death check above is vacuous.
        foreach (var (id, info) in vehicleInfo)
        {
            if (!info.DestPark)
            {
                Assert.Contains(id, completedIds);
            }
        }

        // ---- FCD birth check: a vehicle's FIRST appearance in the engine's own trajectory output
        // must be on a fringe or park edge -- the authoritative runtime check (catches a stop the
        // engine silently rejected, which the route-intent audit above cannot see). ----
        var fcdViolations = new List<string>();
        var seenFirst = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in traj.AllPoints)
        {
            if (!seenFirst.Add(point.VehicleId))
            {
                continue;
            }

            var edge = EdgeOfLane(point.Lane);
            if (!fringeEdges.Contains(edge) && !parkEdges.Contains(edge))
            {
                fcdViolations.Add($"{point.VehicleId}: firstLane={point.Lane} edge={edge} speed={point.Speed}");
            }
        }

        Assert.True(seenFirst.Count >= 3, $"expected all 3 vehicles to appear in FCD, got {seenFirst.Count}.");

        var diagnostic =
            $"fringeEdges=[{string.Join(",", fringeEdges)}] parkEdges=[{string.Join(",", parkEdges)}] " +
            $"vehicles={vehicleInfo.Count} completed={completedIds.Count}\n" +
            $"birthViolations({birthViolations.Count})=[{string.Join(";", birthViolations)}]\n" +
            $"deathViolations({deathViolations.Count})=[{string.Join(";", deathViolations)}]\n" +
            $"fcdViolations({fcdViolations.Count})=[{string.Join(";", fcdViolations)}]";

        Assert.True(birthViolations.Count == 0 && deathViolations.Count == 0 && fcdViolations.Count == 0, diagnostic);
    }

    // Same edge => "fringe" (boundary stub) classification audit_nocheat.py derives from sumolib's
    // Edge.is_fringe(): here, structurally, an edge whose FROM or TO junction is a dead_end (no
    // further network beyond it) -- exactly how eA/eC are built (scenario 68's net.net.xml). "park"
    // edges are the set of edges that host at least one <parkingArea> (any lane on that edge).
    private static (HashSet<string> Fringe, HashSet<string> Park) ClassifyEdges(NetworkModel network, string scenarioDir)
    {
        var fringe = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in network.Edges)
        {
            var fromDeadEnd = network.JunctionsById.TryGetValue(edge.From, out var fromJ) && fromJ.Type == "dead_end";
            var toDeadEnd = network.JunctionsById.TryGetValue(edge.To, out var toJ) && toJ.Type == "dead_end";
            if (fromDeadEnd || toDeadEnd)
            {
                fringe.Add(edge.Id);
            }
        }

        var park = new HashSet<string>(StringComparer.Ordinal);
        var addPath = Path.Combine(scenarioDir, "extra.add.xml");
        using (var stream = File.OpenRead(addPath))
        {
            var root = System.Xml.Linq.XDocument.Load(stream).Root!;
            foreach (var pa in AdditionalFileParser.ParseParkingAreas(root, laneId => network.LanesById[laneId].Length))
            {
                park.Add(EdgeOfLane(pa.LaneId));
            }
        }

        return (fringe, park);
    }

    // Same convention as audit_nocheat.py's `edge = lambda lane: lane.rsplit('_', 1)[0]`.
    private static string EdgeOfLane(string laneId)
    {
        var idx = laneId.LastIndexOf('_');
        return idx < 0 ? laneId : laneId[..idx];
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
