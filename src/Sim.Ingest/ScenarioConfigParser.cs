using System.Globalization;
using System.Xml.Linq;

namespace Sim.Ingest;

// Parses the rung-1 subset of .sumocfg: <time> and <processing>. Defaults mirror SUMO's own
// (ballistic=false => Euler; time-to-teleport=-1 => teleport off) so a scenario that omits an
// attribute still behaves as SUMO would.
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
        // C1-i: SUMO's <random_number> section (see ScenarioConfig.Seed's own comment).
        var randomEl = root.Element("random_number");

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
            LateralResolution: ParseDouble(processingEl, "lateral-resolution", 0.0));
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
}
