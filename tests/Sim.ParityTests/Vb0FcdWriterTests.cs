using System.Text;
using Sim.Core;
using Sim.Harness;
using Xunit;

namespace Sim.ParityTests;

// VB-0 (VIZ_BENCH_TASKS.md Phase 0): the FCD-writer seam. Two guarantees:
//
//  1. LOSSLESS: an engine run streamed through FcdWriterObserver, re-parsed by FcdParser,
//     reproduces the engine's own in-memory TrajectorySet with ZERO divergence -- the writer
//     rounds nothing (it uses round-trippable "R" formatting), so the emitted FCD carries the
//     engine's full double precision. This is the CLAUDE.md "engine emits full precision, never
//     rounds to a coarse golden" rule enforced on the export path.
//
//  2. PARITY THROUGH THE NEW PATH: that same emitted FCD, compared against the SUMO golden,
//     lands within tolerance.json -- i.e. Sim.Viz / the benchmark can consume engine FCD exactly
//     as they consume golden.fcd.xml and see the same parity the in-memory Rung1 test sees.
//
// Also asserts the SUMO-facing `type=` attribute round-trips (added to VehicleExportSnapshot in
// VB-0 so the viz can join FCD vehicle -> vType -> dimensions).
public class Vb0FcdWriterTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "01-single-free-flow");

    [Fact]
    public void EngineFcd_RoundTrips_Losslessly_AndMatchesGolden()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var sb = new StringBuilder();
        TrajectorySet inMemory;
        using (var sw = new StringWriter(sb))
        using (var writer = new FcdWriterObserver(sw))
        {
            engine.AddExportObserver(writer);
            inMemory = engine.Run(80);
        }

        var emitted = sb.ToString();
        var reparsed = FcdParser.ParseXml(emitted);

        // (1) Lossless: emitted-then-reparsed == the engine's in-memory trajectory, exactly.
        var exact = ToleranceConfig.Parse(
            "{\"parityMode\":\"exact\",\"comparedAttributes\":[\"lane\",\"pos\",\"speed\",\"x\",\"y\",\"angle\"]," +
            "\"pos\":0.0,\"speed\":0.0,\"x\":0.0,\"y\":0.0,\"angle\":0.0}");
        var lossless = TrajectoryComparator.Compare(reparsed, inMemory, exact);
        Assert.True(lossless.IsMatch, "FcdWriterObserver is lossy: " + Describe(lossless));

        // (2) Parity through the writer path: emitted engine FCD vs the SUMO golden.
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));
        var parity = TrajectoryComparator.Compare(reparsed, golden, tolerance);
        Assert.True(parity.IsMatch, "engine FCD out of tolerance vs golden: " + Describe(parity));

        // (3) The vType id survives as the FCD `type=` attribute (viz join key).
        Assert.Contains("type=\"passenger0\"", emitted);
    }

    private static string Describe(ComparisonResult result)
    {
        var lines = new List<string> { $"FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}" };
        foreach (var a in result.Attributes)
        {
            lines.Add($"  {a.Attribute}: maxAbs={a.MaxAbsError} rmse={a.Rmse} ok={a.WithinTolerance}");
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
