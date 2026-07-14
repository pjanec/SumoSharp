using System.Text.Json;
using System.Text.Json.Serialization;
using Sim.Core;
using Sim.Harness;
using Sim.Ingest;
using static Sim.Viz.PayloadBuilder;

namespace Sim.Viz;

// VB-1..VB-4 (VIZ_SPEC.md): builds the compact REPLAY_DATA JSON the committed front-end template
// consumes and writes a fully self-contained HTML replay. REPLAY_DATA is now a UNIFIED multi-scene
// payload `{ scenes: [ SCENE, ... ] }` (see Payload.cs); the template renders one scene at a time
// with a scene selector.
//
// Two modes:
//   dotnet run --project src/Sim.Viz -- <scenarioDir> [--fcd <path>]
//       Single-scenario mode (unchanged behaviour): reads a scenario dir's net+fcd+rou and writes
//       <scenarioDir>/replay.html. The one scenario is wrapped as a one-element scenes array, so it
//       shares the exact same template path as the bundle.
//
//   dotnet run --project src/Sim.Viz -- --bundle <outPath>
//       Bundle mode (NEW): assembles the five showcase scenes (two FCD laneless/sublane scenarios
//       plus three programmatically-generated open-space/cross-regime crowd scenes) into ONE
//       self-contained HTML written to <outPath>.
//
// Not part of `dotnet test` -- a utility, and never touches the parity engine's inputs/goldens.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("usage: Sim.Viz <scenarioDir> [--fcd <path>]");
            Console.Error.WriteLine("       Sim.Viz --bundle <outPath>");
            Console.Error.WriteLine("       Sim.Viz --evac-organic <outPath>");
            return args.Length == 0 ? 2 : 0;
        }

        return args[0] switch
        {
            "--bundle" => RunBundle(args),
            "--evac-organic" => RunEvacOrganic(args),
            _ => RunSingle(args),
        };
    }

    // ---------------------------------------------------------------------------------------
    // Standalone organic-town evac mode (PANIC-EVAC-PHASE5-TASKS.md T4.1): emits JUST the one scene,
    // kept OUT of --bundle -- at ~400 vehicle slots x 300 frames the payload is a few MB, which would
    // bloat the showcase bundle for no benefit (this scene is reviewed on its own).
    // ---------------------------------------------------------------------------------------
    private static int RunEvacOrganic(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: --evac-organic requires an output path");
            return 2;
        }

        var outPath = args[1];
        var repoRoot = RepoRoot();

        var scene = SceneGen.BuildEvacOrganic(repoRoot);
        var payload = new ReplayData(new[] { scene });
        if (!WriteHtml(payload, scene.Name, outPath))
        {
            return 2;
        }

        var vehicleSlots = scene.Frames.Length > 0 ? scene.Frames[0].V.Length : 0;

        var pedestrianDiscs = 0;
        foreach (var frame in scene.Frames)
        {
            foreach (var d in frame.D)
            {
                if (d.Length > 3 && (d[3] == SceneGen.KindFleeing || d[3] == SceneGen.KindEscaped))
                {
                    pedestrianDiscs++;
                }
            }
        }

        var size = new FileInfo(outPath).Length;
        Console.WriteLine(
            $"wrote {outPath}  ({size} bytes)  frames={scene.Frames.Length} vehicleSlots={vehicleSlots} " +
            $"pedestrianDiscs={pedestrianDiscs}");
        return 0;
    }

    // ---------------------------------------------------------------------------------------
    // Single-scenario mode (backwards compatible).
    // ---------------------------------------------------------------------------------------
    private static int RunSingle(string[] args)
    {
        var scenarioDir = args[0];
        if (!Directory.Exists(scenarioDir))
        {
            Console.Error.WriteLine($"error: scenario directory not found: {scenarioDir}");
            return 2;
        }

        string? fcdOverride = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fcd" when i + 1 < args.Length:
                    fcdOverride = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"error: unrecognized argument: {args[i]}");
                    return 2;
            }
        }

        var scene = BuildFcdScene(scenarioDir, fcdOverride, out var err);
        if (scene is null)
        {
            Console.Error.WriteLine(err);
            return 2;
        }

        var payload = new ReplayData(new[] { scene });
        var outPath = Path.Combine(scenarioDir, "replay.html");
        if (!WriteHtml(payload, scene.Name, outPath))
        {
            return 2;
        }

        Console.WriteLine(
            $"wrote {outPath}  (scene='{scene.Name}', lanes={scene.Network?.Lanes.Length ?? 0}, " +
            $"frames={scene.Frames.Length}, dt={scene.Dt:0.###})");
        return 0;
    }

    // ---------------------------------------------------------------------------------------
    // Bundle mode: the five-scene laneless showcase.
    // ---------------------------------------------------------------------------------------
    private static int RunBundle(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: --bundle requires an output path");
            return 2;
        }

        var outPath = args[1];
        var repoRoot = RepoRoot();
        var scenarios = Path.Combine(repoRoot, "scenarios");

        var scenes = new List<ScenePayload>();

        // Scene 0 -- the panic-evacuation demo (opens first, docs/PANIC-EVAC-DESIGN.md S6): organized
        // grid traffic transitions to panic/abandonment/foot-flight under a central incident, driven
        // through the real Engine + external Sim.Evac.EvacDirector (the same fixture EvacSpineTests use).
        scenes.Add(SceneGen.BuildEvacGrid(repoRoot));

        // Scene 1 -- the Indian junction: SHAPED mixed traffic (long buses, compact
        // motorcycles) negotiating an uncontrolled crossroads by anisotropic avoidance with SOFT
        // priority (assertive main road vs yielding cross road), from the Sim.Core.Mixed layer.
        scenes.Add(SceneGen.BuildIndianJunction());

        // Scene 1 -- the earlier dense uncontrolled junction (disc agents): many mixed movers on a
        // shared crossroads with no lanes/signals (Egypt/India-style congestion), from the ORCA layer.
        scenes.Add(SceneGen.BuildDenseJunction());

        // Scene A -- FCD "Laneless overtake" (8 vehicles, lateral RVO).
        scenes.Add(RequireFcdScene(
            Path.Combine(scenarios, "65-mixed-sublane"),
            "Laneless overtake",
            "Eight vehicles on a laneless (sub-lane) road overtaking with continuous lateral RVO. "
            + "Replayed from the committed SUMO golden FCD trajectory."));

        // Scene B -- FCD "Sublane overtake" (a follower drifts to pass).
        scenes.Add(RequireFcdScene(
            Path.Combine(scenarios, "63-sublane-overtake-wide"),
            "Sublane overtake",
            "A faster follower drifts laterally within a wide lane to pass the vehicle ahead. "
            + "Replayed from the committed SUMO golden FCD trajectory."));

        // Scenes C/D/E -- programmatically generated from the engine's ORCA layer (no golden FCD).
        scenes.Add(SceneGen.BuildCarAvoidsPedestrian(Path.Combine(scenarios, "_fixtures", "bridge-crossing")));
        scenes.Add(SceneGen.BuildCounterFlow());
        scenes.Add(SceneGen.BuildCrossing());

        var payload = new ReplayData(scenes.ToArray());
        if (!WriteHtml(payload, "Laneless showcase", outPath))
        {
            return 2;
        }

        var size = new FileInfo(outPath).Length;
        Console.WriteLine($"wrote {outPath}  ({size} bytes, {scenes.Count} scenes)");
        foreach (var s in scenes)
        {
            Console.WriteLine(
                $"  - {s.Name}: network={(s.Network is null ? "none" : s.Network.Lanes.Length + " lanes")}, " +
                $"frames={s.Frames.Length}, dt={s.Dt:0.###}");
        }

        return 0;
    }

    private static ScenePayload RequireFcdScene(string scenarioDir, string name, string desc)
    {
        var scene = BuildFcdScene(scenarioDir, null, out var err, name, desc);
        return scene ?? throw new InvalidOperationException(err);
    }

    // ---------------------------------------------------------------------------------------
    // FCD scene builder: reads a scenario dir's net + rou + golden FCD and turns it into a SCENE
    // (network + vehicle-box frames). Reuses the original single-scenario derivation. Returns null
    // (with `err` set) if the inputs are missing, so the single-scenario CLI can report cleanly.
    // ---------------------------------------------------------------------------------------
    private static ScenePayload? BuildFcdScene(
        string scenarioDir,
        string? fcdOverride,
        out string err,
        string? name = null,
        string? desc = null)
    {
        err = string.Empty;

        var netPath = SingleFile(scenarioDir, "*.net.xml");
        var rouPath = SingleFile(scenarioDir, "*.rou.xml");
        var cfgPath = SingleFile(scenarioDir, "*.sumocfg");
        var fcdPath = fcdOverride ?? Path.Combine(scenarioDir, "golden.fcd.xml");

        if (netPath is null || rouPath is null)
        {
            err = $"error: scenario dir must contain exactly one each of *.net.xml, *.rou.xml " +
                  $"(found net={netPath}, rou={rouPath})";
            return null;
        }

        if (!File.Exists(fcdPath))
        {
            err = $"error: FCD file not found: {fcdPath}";
            return null;
        }

        var network = NetworkParser.Parse(netPath);
        var demand = DemandParser.Parse(rouPath);
        var trajectorySet = FcdParser.Parse(fcdPath);
        var config = cfgPath is not null ? ScenarioConfigParser.Parse(cfgPath) : null;

        var sceneName = name ?? Path.GetFileName(Path.GetFullPath(scenarioDir).TrimEnd('/', '\\'));

        var networkPayload = BuildNetwork(network);

        // Camera view = the extent of the network geometry plus every trajectory sample.
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Track(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var lane in networkPayload.Lanes)
        {
            for (var p = 0; p < lane.Shape.Length; p += 2) Track(lane.Shape[p], lane.Shape[p + 1]);
        }

        foreach (var j in networkPayload.Junctions)
        {
            for (var p = 0; p < j.Shape.Length; p += 2) Track(j.Shape[p], j.Shape[p + 1]);
        }

        // Resolve one shared vehicle box dimension for the scene (VIZ_SPEC unified model uses a
        // single vdim per scene). Use the first vehicle's resolved vType; the committed sublane
        // scenarios are homogeneous passenger traffic, so this is representative.
        var vehicleTypeById = demand.Vehicles.ToDictionary(v => v.Id, v => v.TypeId);
        double vehLength = 0, vehWidth = 0;
        foreach (var vid in trajectorySet.VehicleIds)
        {
            if (vehicleTypeById.TryGetValue(vid, out var t) && demand.VTypesById.TryGetValue(t, out var vType))
            {
                var resolved = VTypeDefaults.Resolve(vType);
                vehLength = resolved.Length;
                vehWidth = resolved.Width;
                break;
            }
        }

        if (vehLength <= 0)
        {
            var fallback = VTypeDefaults.Resolve(new VType("__default__", "passenger", Sigma: null));
            vehLength = fallback.Length;
            vehWidth = fallback.Width;
        }

        // Fixed vehicle slots: a stable index per vehicle id (sorted for determinism), so slot i is
        // always the same vehicle across frames; a vehicle absent in a frame is null in its slot.
        var orderedIds = trajectorySet.VehicleIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var slotById = new Dictionary<string, int>(orderedIds.Length);
        for (var i = 0; i < orderedIds.Length; i++) slotById[orderedIds[i]] = i;

        // Group FCD points by timestep.
        var byTime = new SortedDictionary<double, Dictionary<string, (double X, double Y, double A)>>();
        foreach (var point in trajectorySet.AllPoints)
        {
            if (!byTime.TryGetValue(point.Time, out var atTime))
            {
                atTime = new Dictionary<string, (double, double, double)>();
                byTime[point.Time] = atTime;
            }

            atTime[point.VehicleId] = (point.X, point.Y, point.Angle);
            Track(point.X, point.Y);
        }

        var frames = new FramePayload[byTime.Count];
        var noDiscs = Array.Empty<double[]>();
        var fi = 0;
        foreach (var kv in byTime)
        {
            var v = new double[orderedIds.Length][];
            foreach (var (vid, st) in kv.Value)
            {
                v[slotById[vid]] = new[] { R(st.X), R(st.Y), R(st.A) };
            }

            frames[fi++] = new FramePayload(v, noDiscs);
        }

        var times = byTime.Keys.ToArray();
        var dt = times.Length > 1 ? times[1] - times[0] : config?.StepLength ?? 1.0;

        if (double.IsInfinity(minX))
        {
            minX = minY = 0;
            maxX = maxY = 1;
        }

        return new ScenePayload(
            sceneName,
            desc ?? sceneName,
            new[] { R(minX), R(minY), R(maxX), R(maxY) },
            networkPayload,
            new[] { vehLength, vehWidth },
            dt,
            frames);
    }

    // ---------------------------------------------------------------------------------------
    // Shared HTML writer: serialize the payload and inject it + the template JS into template.html.
    // ---------------------------------------------------------------------------------------
    private static bool WriteHtml(ReplayData payload, string title, string outPath)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        var json = JsonSerializer.Serialize(payload, jsonOptions);

        var templateDir = AppContext.BaseDirectory;
        var templateHtmlPath = Path.Combine(templateDir, "template.html");
        var templateJsPath = Path.Combine(templateDir, "template.js");
        if (!File.Exists(templateHtmlPath) || !File.Exists(templateJsPath))
        {
            Console.Error.WriteLine(
                $"error: template files not found next to the built exe ({templateHtmlPath}, {templateJsPath})");
            return false;
        }

        var html = File.ReadAllText(templateHtmlPath);
        var js = File.ReadAllText(templateJsPath);

        html = html.Replace("__SCENARIO_NAME__", title);
        html = html.Replace("/*REPLAY_DATA*/", json);
        html = html.Replace("/*TEMPLATE_JS*/", js);

        File.WriteAllText(outPath, html);
        return true;
    }

    private static string? SingleFile(string dir, string pattern)
    {
        var matches = Directory.GetFiles(dir, pattern);
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above the exe).");
    }
}
