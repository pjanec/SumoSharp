using Sim.Core;
using Sim.Core.Orca;

namespace Sim.Pedestrians.Tests.Parking;

// Shared geometry/setup for the POC-6a parking tests (docs/PEDESTRIAN-POC-PLAN.md POC-6, success
// conditions 1 and 2). Reuses POC-0's fixture (Fixtures/net.net.xml + Fixtures/walkable.add.xml,
// copied from scenarios/_ped/poc0-crossing-plaza by the .csproj) exactly as the Crossing tests do.
internal static class ParkingScenarioFixture
{
    public static string NetPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "net.net.xml");
    public static string WalkableAddPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "walkable.add.xml");

    // A lane-world edge to depart/re-insert a car on. "wc" carries two real vehicle lanes
    // (wc_1/wc_2, allow="pedestrian" only on wc_0) -- departLane: 1 lands directly on a vehicle lane,
    // no lane-change settling needed (see CarStopsForPedestrianTests' SettleOntoVehicleLane comment
    // for why lane index 0 on these edges is a sidewalk, not a vehicle lane).
    public const string DepartEdge = "wc";
    public const string ExitEdge = "ce";
    public const int VehicleLaneIndex = 1;

    // A deterministic slot deep inside the parkinglot polygon (walkable.add.xml:
    // shape="-180,-20 -130,-20 -130,-80 -180,-80 -180,-20" -- x in [-180,-130], y in [-80,-20]),
    // reachable in a straight line from both the entry and exit POIs without leaving the polygon
    // (the rectangle is convex, so any chord between two interior/boundary points stays inside).
    public static readonly Vec2 Slot = new(-155.0, -50.0);

    public const double ManeuverArriveRadius = 1.0;
    public const double ManeuverMaxSpeed = 3.0;   // m/s -- a plausible parking-lot crawl speed

    public static Engine NewEngine()
    {
        var engine = new Engine();
        engine.LoadNetwork(NetPath);
        return engine;
    }

    public static VTypeHandle DefineCarType(Engine engine) =>
        engine.DefineVType(new VTypeParams { VClass = "passenger", Sigma = 0.0 });

    public static Sim.Pedestrians.PedNetwork LoadPedNetwork() =>
        Sim.Pedestrians.PedNetworkParser.Load(NetPath, WalkableAddPath);

    public static Sim.Pedestrians.WalkablePolygon ParkingLotPolygon(Sim.Pedestrians.PedNetwork net) =>
        net.WalkablePolygons.Single(p => p.Id == "parkinglot");

    public static Vec2 EntryPoi(Sim.Pedestrians.PedNetwork net) =>
        net.AccessPoints.Single(a => a.Id == "parkinglot_entry").Position;

    public static Vec2 ExitPoi(Sim.Pedestrians.PedNetwork net) =>
        net.AccessPoints.Single(a => a.Id == "parkinglot_exit").Position;

    // Spawns a car on DepartEdge (directly on a real vehicle lane) and steps the engine until it is
    // reported Active (inserted). Mirrors CarStopsForPedestrianTests' insertion wait.
    public static VehicleHandle SpawnAndSettleCar(Engine engine, VTypeHandle vType)
    {
        var handle = engine.SpawnVehicle(vType, new[] { DepartEdge }, departPos: 5.0, departSpeed: 0.0, departLane: VehicleLaneIndex);
        for (var i = 0; i < 10; i++)
        {
            engine.Step();
            if (engine.TryGetVehicle(handle, out _))
            {
                return handle;
            }
        }

        throw new InvalidOperationException("car did not insert within the settle budget.");
    }

    // Standard ray-casting point-in-polygon test (works for the closed, convex rectangle
    // walkable.add.xml declares for "parkinglot"; the polygon's shape repeats its first vertex last,
    // which this algorithm tolerates -- a repeated final edge of zero length never toggles the parity).
    public static bool PointInPolygon(Vec2 p, IReadOnlyList<Vec2> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            var crosses = (a.Y > p.Y) != (b.Y > p.Y);
            if (!crosses)
            {
                continue;
            }

            var xAtY = a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
            if (p.X < xAtY)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
