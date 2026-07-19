using System.Globalization;
using System.Xml.Linq;

namespace Sim.Ingest;

// Parses the rung-1 subset of .sumocfg: <time> and <processing>, plus (P0-A) the <input> section
// (net-file / route-files / additional-files). Defaults mirror SUMO's own (ballistic=false =>
// Euler; time-to-teleport=-1 => teleport off; <input> absent => NetFile null, RouteFiles/
// AdditionalFiles empty) so a scenario that omits an attribute -- or the whole <input> section,
// as every pre-P0-A scenario does -- still behaves as SUMO would / as before.
public static class ScenarioConfigParser
{
    public static ScenarioConfig Parse(string path)
    {
        using var stream = File.OpenRead(path);
        return ParseDocument(XDocument.Load(stream));
    }

    public static ScenarioConfig ParseXml(string xml) => ParseDocument(XDocument.Parse(xml));

    private static ScenarioConfig ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("sumocfg has no root element.");

        var timeEl = root.Element("time");
        var processingEl = root.Element("processing");
        // P2-G Bug-1: SUMO's canonical layout puts the rerouting-device / routing options in a
        // dedicated <routing> section, while our earlier scenarios (and SumoData's older configs)
        // placed the same keys under <processing>. Read the rerouting/routing keys from <processing>
        // first (back-compat: every committed golden uses <processing>, so those stay byte-identical)
        // and fall back to <routing> when the key is absent there. Without this fallback a
        // SUMO-canonical config's device.rerouting.* is silently ignored and rerouting is inert.
        var routingEl = root.Element("routing");
        // C1-i: SUMO's <random_number> section (see ScenarioConfig.Seed's own comment).
        var randomEl = root.Element("random_number");
        // P0-A: SUMO's <input> section -- net-file/route-files/additional-files. Absent for every
        // pre-P0-A scenario (they're driven by the 3-arg LoadScenario / Sim.Run's glob instead).
        var inputEl = root.Element("input");

        return new ScenarioConfig(
            Begin: ParseDouble(timeEl, "begin", 0.0),
            End: ParseDouble(timeEl, "end", 0.0),
            StepLength: ParseDouble(timeEl, "step-length", 1.0),
            Ballistic: ParseBool(processingEl, "step-method.ballistic", defaultValue: false),
            TimeToTeleport: ParseDouble(processingEl, "time-to-teleport", -1.0),
            ActionStepLength: ParseDouble(processingEl, "default.action-step-length", 0.0),
            SpeedDev: ParseDouble(processingEl, "default.speeddev", 0.1),
            Seed: ParseInt(randomEl, "seed", 42),
            // C10-i: SUMO's <processing><lanechange.duration> (default 0 = instant snap).
            LaneChangeDuration: ParseDouble(processingEl, "lanechange.duration", 0.0),
            // Phase 2: SUMO's <processing><lateral-resolution> (default 0 = sublane model OFF).
            LateralResolution: ParseDouble(processingEl, "lateral-resolution", 0.0),
            NetFile: inputEl?.Element("net-file")?.Attribute("value")?.Value,
            RouteFiles: ParseFileList(inputEl, "route-files"),
            AdditionalFiles: ParseFileList(inputEl, "additional-files"),
            // P1E-1: <processing><device.rerouting.*>/<routing-algorithm> -- absent for every
            // pre-P1E-1 scenario, so every default below is "rerouting inert" (see ScenarioConfig's
            // own header comment for what each key means and its SUMO/non-SUMO provenance).
            RerouteProbability: ParseDouble(processingEl, routingEl, "device.rerouting.probability", 0.0),
            ReroutePeriod: ParseDouble(processingEl, routingEl, "device.rerouting.period", 0.0),
            RerouteAdaptationSteps: ParseInt(processingEl, routingEl, "device.rerouting.adaptation-steps", 180),
            RerouteAdaptationInterval: ParseDouble(processingEl, routingEl, "device.rerouting.adaptation-interval", 1.0),
            RoutingAlgorithm: (processingEl?.Element("routing-algorithm") ?? routingEl?.Element("routing-algorithm"))?.Attribute("value")?.Value ?? "dijkstra",
            RerouteJitter: ParseBool(processingEl, routingEl, "device.rerouting.jitter", defaultValue: false),
            // P1F-1: SUMO's <processing><time-to-teleport.remove> (default false). Absent for
            // every pre-P1F scenario, so the default keeps the jam valve's re-insertion behaviour.
            TimeToTeleportRemove: ParseBool(processingEl, "time-to-teleport.remove", defaultValue: false),
            // P2-H: SUMO's <processing><max-depart-delay> (seconds; -1 = never delete, the default).
            // Absent for every pre-P2-H scenario, so the InsertDepartingVehicles eviction branch stays
            // inert (gated on MaxDepartDelay >= 0) and all prior goldens are byte-identical.
            MaxDepartDelay: ParseDouble(processingEl, "max-depart-delay", -1.0));
    }

    // P0-A: SUMO's <route-files value="a.rou.xml,b.rou.xml"/> / <additional-files value="..."/>
    // -- a list of paths, SUMO accepts both a comma-list and a whitespace-list (and mixtures), so
    // split on either, trim, and drop empties. Returns null when the section/attribute is absent
    // so ScenarioConfig's Array.Empty<string>() default applies (rather than an empty-but-non-null
    // list carrying no signal difference either way -- kept null here for symmetry with NetFile).
    private static IReadOnlyList<string>? ParseFileList(XElement? inputEl, string name)
    {
        var value = inputEl?.Element(name)?.Attribute("value")?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private static double ParseDouble(XElement? parent, string name, double defaultValue)
    {
        var value = parent?.Element(name)?.Attribute("value")?.Value;
        return value is null ? defaultValue : double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool ParseBool(XElement? parent, string name, bool defaultValue)
    {
        var value = parent?.Element(name)?.Attribute("value")?.Value;
        return value is null ? defaultValue : bool.Parse(value);
    }

    private static int ParseInt(XElement? parent, string name, int defaultValue)
    {
        var value = parent?.Element(name)?.Attribute("value")?.Value;
        return value is null ? defaultValue : int.Parse(value, CultureInfo.InvariantCulture);
    }

    // P2-G Bug-1 two-parent overloads: read `name` from `primary` first, then `fallback`. Used so a
    // key present under <processing> wins (back-compat) but a SUMO-canonical config that puts it
    // under <routing> is still honored. Returns the default only when neither section carries it.
    private static double ParseDouble(XElement? primary, XElement? fallback, string name, double defaultValue)
    {
        var value = (primary?.Element(name) ?? fallback?.Element(name))?.Attribute("value")?.Value;
        return value is null ? defaultValue : double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool ParseBool(XElement? primary, XElement? fallback, string name, bool defaultValue)
    {
        var value = (primary?.Element(name) ?? fallback?.Element(name))?.Attribute("value")?.Value;
        return value is null ? defaultValue : bool.Parse(value);
    }

    private static int ParseInt(XElement? primary, XElement? fallback, string name, int defaultValue)
    {
        var value = (primary?.Element(name) ?? fallback?.Element(name))?.Attribute("value")?.Value;
        return value is null ? defaultValue : int.Parse(value, CultureInfo.InvariantCulture);
    }
}
