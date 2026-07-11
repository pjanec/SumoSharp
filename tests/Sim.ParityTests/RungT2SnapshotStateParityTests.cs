using System.Globalization;
using System.Xml.Linq;
using Sim.Core;
using Xunit;

namespace Sim.ParityTests;

// Rung T2 (W3): cross-check our SaveSnapshot against SUMO's native --save-state at a mid-run instant.
// Our per-step trajectory already matches golden.fcd.xml within 1e-3, but that validates the RUNNING
// engine, not the SNAPSHOT: T2 confirms the state SaveSnapshot writes to disk agrees with the state
// SUMO writes with --save-state at the same instant (the physically-modeled fields: lane position,
// speed, lateral offset). It is the state-boundary hardening check the W3 handoff asked for.
//
// Timing convention (calibrated): SUMO --save-state.times T writes the state ENTERING step T, i.e.
// after (T-1) movements, so it is compared against our engine.Run(T-1).SaveSnapshot. Golden =
// scenarios/<name>/golden.state.mid.xml (a committed SUMO --save-state, see provenance.txt).
public class RungT2SnapshotStateParityTests
{
    private const double Tol = 1e-2; // SUMO state written at --precision 6; our per-step FCD parity is 1e-3

    [Theory]
    [InlineData("12-overtake", 15)]     // 2 vehicles, one mid-lane-change (left lane) -> distinct pos/speed
    [InlineData("22-idm-carfollow", 30)] // IDM follower + leader in steady following
    public void SaveSnapshot_MatchesSumoSaveState_AtMidRun(string scenario, int sumoSaveTime)
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", scenario);

        // SUMO ground truth: id -> (front pos, speed, posLat) for every vehicle that is ON A LANE
        // (the active ones; not-yet-departed vehicles are excluded by the lane-membership filter).
        var sumo = ParseSumoState(Path.Combine(dir, "golden.state.mid.xml"));
        Assert.NotEmpty(sumo);

        // Our snapshot at the calibrated matching step.
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(dir, "net.net.xml"),
            Path.Combine(dir, "rou.rou.xml"),
            Path.Combine(dir, "config.sumocfg"));
        engine.Run(sumoSaveTime - 1);
        var snapPath = Path.Combine(Path.GetTempPath(), $"t2_{scenario}_{Guid.NewGuid():N}.xml");
        try
        {
            engine.SaveSnapshot(snapPath);
            var ours = XDocument.Load(snapPath).Root!.Elements("vehicle")
                .Where(v => (string?)v.Attribute("arrived") == "false")
                .ToDictionary(v => v.Attribute("id")!.Value, v => (
                    Pos: D(v, "pos"), Speed: D(v, "speed"), Lat: D(v, "latOffset")));

            // Every active vehicle in our snapshot must be present in SUMO's state and agree on the
            // physical fields; and the run must be non-vacuous (at least one moving vehicle).
            Assert.NotEmpty(ours);
            var moving = 0;
            foreach (var (id, s) in ours)
            {
                Assert.True(sumo.TryGetValue(id, out var g), $"{scenario}: vehicle '{id}' absent from SUMO save-state");
                Assert.True(Math.Abs(s.Pos - g.Pos) <= Tol, $"{scenario} {id}: pos ours={s.Pos:F4} sumo={g.Pos:F4}");
                Assert.True(Math.Abs(s.Speed - g.Speed) <= Tol, $"{scenario} {id}: speed ours={s.Speed:F4} sumo={g.Speed:F4}");
                Assert.True(Math.Abs(s.Lat - g.Lat) <= Tol, $"{scenario} {id}: posLat ours={s.Lat:F4} sumo={g.Lat:F4}");
                if (s.Speed > 0.1) moving++;
            }
            Assert.True(moving > 0, $"{scenario}: expected at least one moving vehicle at the save instant");
        }
        finally
        {
            if (File.Exists(snapPath)) File.Delete(snapPath);
        }
    }

    private static double D(XElement v, string attr) => double.Parse(v.Attribute(attr)!.Value, CultureInfo.InvariantCulture);

    // SUMO state <vehicle> carries pos="front back laneSpeed" and speed="cur prev"; posLat is the
    // signed lateral offset. A vehicle is ACTIVE iff it is listed under some <lane><vehicles>.
    private static Dictionary<string, (double Pos, double Speed, double Lat)> ParseSumoState(string path)
    {
        var doc = XDocument.Load(path);
        var ns = doc.Root!.Name.Namespace;
        var onLane = doc.Root.Elements(ns + "lane")
            .Elements(ns + "vehicles")
            .SelectMany(e => ((string?)e.Attribute("value") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet();

        var result = new Dictionary<string, (double, double, double)>();
        foreach (var v in doc.Root.Descendants(ns + "vehicle"))
        {
            var id = (string?)v.Attribute("id");
            if (id is null || !onLane.Contains(id)) continue;
            var pos = First(v.Attribute("pos")!.Value);
            var speed = First(v.Attribute("speed")!.Value);
            var lat = double.Parse((string?)v.Attribute("posLat") ?? "0", CultureInfo.InvariantCulture);
            result[id] = (pos, speed, lat);
        }

        return result;
    }

    private static double First(string spaceSeparated) =>
        double.Parse(spaceSeparated.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Traffic.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
