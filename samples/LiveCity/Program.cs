// LiveCity -- a tutorial-style walkthrough of SumoSharp.LiveCity: cars and pedestrians sharing one
// net, coupled so a car yields to a pedestrian on a crosswalk. Run it with:
//   dotnet run --project samples/LiveCity
using Sim.LiveCity;

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("LiveCity -- SumoSharp.LiveCity: cars (Engine) + pedestrians (Sim.Pedestrians) coupled.");

        // 1) Build a config. LiveCityConfig.ForDataset(dir) is the arbitrary-net-import factory: it wants
        //    a directory containing a net (`net.xml`, or a SumoData-cut-style `scenario.net.xml`) and,
        //    optionally, a `scenario.rou.xml` it scrapes for spawn edges only -- LiveCitySim generates its
        //    own procedural car+ped demand, it does not replay a route file. We reuse the georeferenced
        //    scenarios/_ped/georef_min fixture: it is named exactly `scenario.net.xml`/`scenario.rou.xml`
        //    (ForDataset's cut-style convention, no path hacking needed) and -- unlike this repo's
        //    PedestrianCrowd sample's poc0-crossing-plaza fixture, which is a bare `net.net.xml` with no
        //    scenario.rou.xml -- has a real (if tiny) demand file and pedestrian infrastructure, so both
        //    halves of the coupling have somewhere to be interesting. ForSumocfg(path) is the alternative
        //    entry point when you have a `.sumocfg` instead of a bare dataset directory (it resolves
        //    <net-file>/<route-files> the same way `sumo -c scenario.sumocfg` would).
        var datasetDir = ResolveDatasetDir();
        var config = LiveCityConfig.ForDataset(datasetDir);

        // 2) The knobs that matter. These are exactly the fields the LIVECITY_CARS / LIVECITY_PEDS /
        //    LIVECITY_HZ environment gates override at construction time (LiveCityConfig.WithEnvOverrides)
        //    -- so `LIVECITY_CARS=40 dotnet run --project samples/LiveCity` changes the same knob this code
        //    sets explicitly. Kept small here so the sample runs fast and its output is readable.
        config.CarTargetConcurrent = 20;   // target concurrent live cars (closed-loop -- see note in step 7)
        config.PedPopulationCap = 60;      // target concurrent live pedestrians (closed-loop, same caveat)
        config.Dt = 0.5;                   // seconds per Step() -- 2 Hz, LiveCityConfig's own default

        Console.WriteLine($"dataset          : {datasetDir}");
        Console.WriteLine($"net              : {config.ResolveNetPath()}");
        Console.WriteLine($"CarTargetConcurrent = {config.CarTargetConcurrent}  PedPopulationCap = {config.PedPopulationCap}  Dt = {config.Dt}s");
        Console.WriteLine();

        // 3) Construct the coupled sim. This parses the net TWICE internally (once for the vehicle-side
        //    NetworkModel, once for the pedestrian-side PedNetwork -- the two models are deliberately
        //    disjoint, CLAUDE.md's "follow SUMO on anything behavioral" plus the pedestrian design's own
        //    "never merge with the parity network model"), bakes the ped navmesh, and wires the Engine +
        //    PedLodManager + PedDemand + crossing-signal machinery together exactly as
        //    src/Sim.Viz/SceneGen.cs's reference recipe does.
        using var sim = new LiveCitySim(config);

        // 4) THE COUPLING: cars see pedestrians through `Engine.CrowdSource` (set inside the LiveCitySim
        //    constructor to a composite of the promoted-pedestrian footprint source and the crossing-
        //    occupancy source) -- that is the ONE seam that makes a car brake for a pedestrian standing on
        //    or crossing a crosswalk, exactly like the vehicle-vs-vehicle Krauss following model, just with
        //    a pedestrian disc as the "leader". Only PROMOTED (high-power, FreeKinematic/full-ORCA) peds
        //    are visible in that footprint source -- low-power PathArc/ActivityTimeline peds are not, by
        //    design (docs/PEDESTRIAN-DESIGN.md sec5/sec9); crossing occupancy is tracked separately and
        //    covers everybody, promoted or not. This is a LIVE-REACTIVITY concern, not a parity one: the
        //    car-following model itself is still the same SUMO-ported Krauss model CLAUDE.md's parity bar
        //    applies to; what is new here is WHAT it reacts to, not HOW it reacts.
        Console.WriteLine("--- stepping the coupled sim ---");
        const int steps = 200;
        const int reportEvery = 10;
        var stepsWithHeldCar = 0;
        var peakHeld = 0;
        for (var step = 1; step <= steps; step++)
        {
            // 5) One call per tick drives everything: car insertion/Krauss-following/lane-changing, ped
            //    demand spawn/despawn, LOD promotion/demotion, crossing-signal state, and the crowd-yield
            //    coupling above -- nothing else is required of the host.
            sim.Step();

            // 6) "Held/yielding" is read off Engine's own authoritative per-car diagnostics via
            //    WitnessAuthoritative(): each car's `Binder` byte names WHICH speed constraint bound it
            //    this step (Engine.cs's fixed-order Math.Min fold over every candidate constraint --
            //    leader-follow, red light, junction yield, ...). Binder == 13 is
            //    `CrowdLongitudinalConstraint` specifically: "brake for a crowd agent ego is still
            //    laterally overlapping" -- i.e. THIS car, THIS step, is slowing for a pedestrian through
            //    the exact CrowdSource seam described above, not for a red light or another car. That
            //    makes this an EXACT count, not a speed-threshold guess -- checked every step (not just
            //    report steps) since a crowd-yield event can be brief (a car clears its own gap fast).
            var held = 0;
            foreach (var w in sim.WitnessAuthoritative())
            {
                if (w.Binder == 13)
                {
                    held++;
                }
            }

            if (held > 0)
            {
                stepsWithHeldCar++;
                peakHeld = Math.Max(peakHeld, held);
            }

            if (step % reportEvery == 0 || step == steps)
            {
                var snap = sim.Sample();
                Console.WriteLine(
                    $"  step {step,3}  t={step * config.Dt,6:F1}s  cars={snap.Cars.Count,3}  peds={snap.Peds.Count,3}  " +
                    $"heldForPed={held,2}  occupiedCrossings={snap.OccupiedCrossings,2}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"crowd-yield coupling: {stepsWithHeldCar}/{steps} steps had at least one car held for a pedestrian " +
            $"(peak {peakHeld} simultaneously) -- the direct effect of Engine.CrowdSource on car following.");
        Console.WriteLine($"done. final live cars={sim.CurrentCars}  final live peds={sim.CurrentPeds}");

        // 7) Further reading:
        //    - docs/ENV-GATES.md (MANDATORY): every LIVECITY_*/SUMOSHARP_* gate this host reads is
        //      PROCESS-GLOBAL -- an inherited shell value is indistinguishable from a deliberately-set one
        //      -- and SEVERAL of them are behavioural (they change the trajectory, not just perf). Any A/B
        //      comparison MUST set every gate it cares about explicitly, in BOTH arms; see that doc's
        //      "three-state trap" section for a form (`GetEnvironmentVariable(name) == "1"`) that has
        //      already caused a real measurement bug here.
        //    - docs/LIVE-CITY-HARNESS-GUIDE.md: how to drive LiveCitySim from a real measurement harness
        //      (Sim.BenchLiveCity), not just a hand-rolled loop like this sample's.
        //    - docs/LIVE-CITY-STATUS.md: what is and isn't validated about this host today.
        //
        //    CLOSED-LOOP CAVEAT (read this before drawing any capacity/throughput conclusion from a run
        //    like this one): CarTargetConcurrent/PedPopulationCap are CLOSED-LOOP caps -- the host inserts
        //    a new car/ped only while the live count is BELOW the cap, so inflow is throttled by this
        //    sample's own drain and the resident count can never run away, no matter how congested the net
        //    gets. That makes a run like this one useless as evidence about capacity or discharge rate --
        //    it can only ever show "did the cap get reached and held". For an OPEN-LOOP measurement (fixed
        //    inflow rate regardless of how full the net is, the shape a real capacity claim needs), use
        //    `Sim.BenchLiveCity --inflow` instead.
        return 0;
    }

    // scenarios/_ped/georef_min, found by walking up to the repo root (Traffic.sln) -- the committed
    // georeferenced 3-D fixture (20 crossings, 24 walking areas, 195 ped lanes, one small scenario.rou.xml)
    // named the way a SumoData preprocess.py cut sub-area is (`scenario.net.xml` + `scenario.sumocfg`,
    // not `net.xml`), so `LiveCityConfig.ForDataset` resolves it with no extra path configuration.
    private static string ResolveDatasetDir()
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

        return Path.Combine(dir.FullName, "scenarios", "_ped", "georef_min");
    }
}
