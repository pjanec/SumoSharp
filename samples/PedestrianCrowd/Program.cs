// PedestrianCrowd -- a tutorial-style walkthrough of SumoSharp.Pedestrians end to end, headless:
// bake a navmesh from a real SUMO net, generate O/D demand, and watch the two-level LOD (low-power
// PathArc followers vs high-power full-ORCA agents) promote/demote as peds near an "interest source"
// (docs/PEDESTRIAN-DESIGN.md sec5). Run it with:
//   dotnet run --project samples/PedestrianCrowd
using System.Linq;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians;
using Sim.Pedestrians.Demand;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation.Bake;

internal static class Program
{
    private const double MaxSpeed = 1.4;      // m/s, typical adult walking speed
    private const double PedRadius = 0.3;     // m, ORCA agent radius
    private const double ArriveRadius = 0.3;  // m, PedLodManager's waypoint-arrival radius
    private const double DwellSeconds = 1.0;  // s -- minimum time in a LOD state before it may flip again
    private const double Dt = 0.5;            // s per step

    // The same north-arm crossing points PedNetworkParserTests / PedDemandTests use for this fixture:
    // on OPPOSITE sidewalks of the signalized junction's north arm, so walking between them forces a
    // detour down to the junction, across via its TLS crossing, and back up -- every single trip
    // passes through the junction the interest source below sits on.
    private static readonly Vec2 WestNorthArm = new(112.6, 140.0);
    private static readonly Vec2 EastNorthArm = new(127.4, 140.0);

    // Centre of the signalized junction "c" (its shape spans roughly x,y in [109.6, 130.4]) -- the
    // spot every crossing pedestrian passes near, and where we park the interest source in phase B.
    private static readonly Vec2 JunctionCentre = new(120.0, 120.0);

    private static int Main()
    {
        Console.WriteLine("PedestrianCrowd -- SumoSharp.Pedestrians: navmesh bake, O/D demand, and the two-level LOD.");

        // 1) Load the fixture's pedestrian geometry (sidewalks/crossings/walkingAreas from the .net.xml,
        //    plus the plaza/parking-lot walkable polygons from walkable.add.xml) and BAKE it into one
        //    deterministically-ordered polygon set, then build a navmesh over it. Passing the net's own
        //    declared PedConnections lets the graph stitch portals a purely-geometric pass would miss
        //    (docs/PEDESTRIAN-R1-CONNECTION-STITCH-DESIGN.md); this fixture doesn't need it, but it is
        //    always safe to pass.
        var (netPath, walkableAddPath) = ResolveFixturePaths();
        var network = PedNetworkParser.Load(netPath, walkableAddPath);
        var polygons = WalkablePolygonBaker.Bake(network);
        var space = new SumoWalkableSpace(polygons);
        var nav = new SumoNavMesh(polygons, space, network.PedConnections);

        // ConnectedComponentCount is the direct diagnostic that the bake actually produced one walkable
        // surface instead of a shattered mess: a well-connected network is 1 (or a small handful of
        // genuinely separate islands, e.g. the parking lot here, which has no declared connection to the
        // road network), not hundreds.
        var componentCount = nav.ConnectedComponentCount();
        Console.WriteLine($"network   : {netPath}");
        Console.WriteLine($"baked     : {polygons.Count} walkable polygons -> {componentCount} connected component(s)");
        Console.WriteLine();

        // 2) The LOD manager: owns the low-power (PathArc, O(1)/step, no neighbour query) population and
        //    a PERSISTENT high-power OrcaCrowd that promoted peds join/leave one at a time. PedPublisher
        //    is the in-memory wire a real DDS/IG consumer would read from; we don't inspect it here, but
        //    every LOD manager needs one.
        var publisher = new PedPublisher();
        var lod = new PedLodManager(nav, publisher, ArriveRadius, DwellSeconds);

        // 3) O/D demand: PedDemand sits ABOVE the LOD manager and populates the scenario itself -- pick an
        //    O/D pair, route it once via IPedNavigation, spawn as low-power, and despawn on arrival. The
        //    seed makes every random decision (WHEN a ped spawns, WHICH O/D pair it draws) reproducible:
        //    same seed + same step sequence => identical spawns/trajectories, every time. That determinism
        //    is why the engine never uses System.Random anywhere (CLAUDE.md) -- every draw here comes from
        //    a per-entity seeded Sim.Core.VehicleRng stream instead.
        var config = new PedDemandConfig
        {
            Origins = new[] { WestNorthArm, EastNorthArm },
            Destinations = new[] { WestNorthArm, EastNorthArm },
            SpawnRatePerSecond = 1.0,
            PopulationCap = 6,
            Seed = 0xC0FFEE_1234UL,
            MaxSpeed = MaxSpeed,
            Radius = PedRadius,
            ArrivalRadius = 0.5,
        };
        var demand = new PedDemand(config, nav, lod, startTime: 0.0);

        var field = new InterestField();
        var noEntities = Array.Empty<WorldDisc>();
        var now = 0.0;

        // 4) Phase A: step with NO interest source registered at all. `InterestField.Query` then reports
        //    "nothing within promote radius" for every ped, so nobody can promote -- the whole population
        //    stays low-power (PathArc) regardless of where it walks. This is the baseline the next phase's
        //    split is measured against.
        Console.WriteLine("--- phase A: no interest source -- every ped stays low-power (PathArc) ---");
        for (var step = 1; step <= 20; step++)
        {
            demand.Step(now, Dt, field, noEntities);
            now += Dt;
            if (step % 5 == 0)
            {
                PrintSplit(step, now, demand, lod);
            }
        }

        // 5) THIS IS THE POINT OF THE SAMPLE: register an interest source at the junction. Any low-power
        //    ped whose (frozen, start-of-step) position falls within PromoteRadius of it promotes to a
        //    real FreeKinematic agent in the persistent high-power OrcaCrowd; it demotes again once it has
        //    been continuously outside the larger DemoteRadius for DwellSeconds (spatial hysteresis, so a
        //    ped sitting exactly at one shared radius doesn't flip every step). Every ped's O/D route
        //    passes through this junction, so watch the low/high split change below as peds walk into and
        //    back out of the promote radius while crossing.
        Console.WriteLine();
        Console.WriteLine("--- phase B: interest source registered at the junction -- watch peds promote/demote ---");
        var source = new InterestSource(JunctionCentre, promoteRadius: 10.0, demoteRadius: 20.0);
        field.Register(source);

        var sawPromotion = false;
        for (var step = 21; step <= 100; step++)
        {
            demand.Step(now, Dt, field, noEntities);
            now += Dt;
            if (lod.HighPowerCount > 0)
            {
                sawPromotion = true;
            }

            if (step % 5 == 0)
            {
                PrintSplit(step, now, demand, lod);
            }
        }

        Console.WriteLine();
        Console.WriteLine(sawPromotion
            ? "confirmed: at least one ped promoted to high-power (FreeKinematic/ORCA) while crossing the junction."
            : "WARNING: no ped ever promoted -- the interest source or timing needs retuning.");

        // 6) Read back a pose with its elevation -- the mandatory-z API every navigation provider
        //    implements (docs/EXTERNAL-NET-LOADING-DESIGN.md sec3): IPedNavigation.ElevationsAlong for a
        //    whole route, and PedLodManager.ElevationOf for one live ped's CURRENT position (resolved
        //    along its own path, never by a nearest-lane search -- so a ped on a bridge follows the
        //    bridge). This fixture's net is 2-D, so every value below is exactly 0.0; the API is the same
        //    one a 3-D net (e.g. scenarios/_ped/georef_min) would return real heights from.
        Console.WriteLine();
        Console.WriteLine("--- pose + elevation read-back (mandatory-z API) ---");
        // Say this in the OUTPUT, not only in the README: a reader who just runs the sample sees a column
        // of z=0.00 and has no way to tell "flat net, 0 is the right answer" from the separate OPEN bug
        // where low-power peds report z=0 on a 3-D net (docs/TASKS-TODO.md, "Geneva low-power peds still
        // report z = 0"). Those look identical on screen, so the distinction has to be printed.
        Console.WriteLine(
            "    NOTE: this fixture is a FLAT 2-D net, so 0.00 is the correct elevation, not a bug.");
        Console.WriteLine(
            "    For real relief see scenarios/_ped/georef_min (27.5 m), exercised by samples/LiveCity");
        Console.WriteLine(
            "    and asserted by tests/Sim.LiveCity.Tests PedElevation* / ExternalNetLoadingTests.");
        var route = nav.FindPath(WestNorthArm, EastNorthArm, out var vertexSurfaces);
        if (route is not null)
        {
            var elevations = nav.ElevationsAlong(route, vertexSurfaces);
            var zs = string.Join(", ", elevations.Select(z => z.ToString("F2")));
            Console.WriteLine($"route West->East: {route.Count} waypoints, ElevationsAlong -> [{zs}] m");
        }

        foreach (var id in demand.LiveIds)
        {
            var pos = lod.PositionOf(id, now);
            var elevation = lod.ElevationOf(id, now);
            var model = lod.ModelOf(id);
            Console.WriteLine($"  ped {id,3}  model={model,-13}  pos=({pos.X,7:F2},{pos.Y,7:F2})  z={elevation:F2} m");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"done. spawns={demand.SpawnCount} arrivals={demand.ArrivalCount} unreachable={demand.UnreachableSkipCount}");
        return 0;
    }

    // Every currently-live ped's PedDrModel, tallied into low-power (PathArc) vs high-power
    // (FreeKinematic/ORCA) -- the split the whole sample exists to make visible.
    private static void PrintSplit(int step, double now, PedDemand demand, PedLodManager lod)
    {
        var low = 0;
        var high = 0;
        foreach (var id in demand.LiveIds)
        {
            if (lod.ModelOf(id) == PedDrModel.FreeKinematic)
            {
                high++;
            }
            else
            {
                low++;
            }
        }

        Console.WriteLine($"  step {step,3}  t={now,5:F1}s  live={demand.LiveCount,2}  low-power={low,2}  high-power(ORCA)={high,2}");
    }

    // scenarios/_ped/poc0-crossing-plaza/{net.net.xml,walkable.add.xml}, found by walking up to the repo
    // root (Traffic.sln) -- the same purpose-built pedestrian fixture PedNetworkParserTests/PedLodManagerTests/
    // PedDemandTests already prove this exact bake+navmesh+demand pipeline against (a 4-arm signalized
    // junction with sidewalks, TLS-controlled crossings, walkingAreas, plus a plaza + parking-lot walkable
    // polygon). Small (52 KB) and committed, so this sample needs no SUMO and no network access.
    private static (string NetPath, string WalkableAddPath) ResolveFixturePaths()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above the exe).");
        }

        var fixtureDir = Path.Combine(dir.FullName, "scenarios", "_ped", "poc0-crossing-plaza");
        return (Path.Combine(fixtureDir, "net.net.xml"), Path.Combine(fixtureDir, "walkable.add.xml"));
    }
}
