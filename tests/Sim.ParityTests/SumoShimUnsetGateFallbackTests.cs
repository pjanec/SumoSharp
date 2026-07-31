using System.Security.Cryptography;
using Sim.Sumo;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// THE TEST NOBODY HAD: the UNSET shim path.
//
// `SumoShim` used to read its three junction gates with the two-state form
// `GetEnvironmentVariable(name) == "1"`, which FORCES FALSE when the variable is absent, while all three
// `Engine` properties default to `true`. So every `sumosharp` invocation that did not set them -- the
// SumoData pipeline's included, via `SUMO_BINARY` -- silently ran with three junction gates disabled that
// the engine, the goldens and the LiveCity host all had enabled.
//
// WHY IT SURVIVED SO LONG, and what this file fixes: every test that touched those gates SET them, so
// nothing exercised the unset path, and the goldens go through `Engine` directly so they were blind to
// it. `docs/TASKS-TODO.md` recorded "add a test covering the unset shim path (nothing does today)" as
// part of the fix. This is that test.
//
// It is a BEHAVIOURAL check, not a source-shape one: it asserts that the shim with the variables ABSENT
// produces byte-identical output to the shim with them explicitly set to the engine defaults. A revert
// to the two-state form fails it immediately.
//
// See docs/ENV-GATES.md §"The three-state trap" and
// docs/JUNCTION-REALISM-SESSION-JOURNAL.md Entry 19.
[Collection(SumoShimEnvCollection.Name)]
public class SumoShimUnsetGateFallbackTests
{
    private readonly ITestOutputHelper _out;

    public SumoShimUnsetGateFallbackTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Gates =
    {
        "SUMOSHARP_CONTTURNFIX", "SUMOSHARP_ISLEADERFIX", "SUMOSHARP_INTERNALJUNCTIONFIX",
    };

    [Fact]
    public void UnsetGates_FallBackToTheEngineDefaults_NotToFalse()
    {
        var cfg = Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2", "scenario.sumocfg");
        Assert.True(File.Exists(cfg), $"repro scenario missing: {cfg}");

        var previous = Gates.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            var unset = RunAndHash(cfg, null);
            var onExplicit = RunAndHash(cfg, "1");
            var offExplicit = RunAndHash(cfg, "0");

            _out.WriteLine($"gates unset       -> {unset}");
            _out.WriteLine($"gates set to '1'  -> {onExplicit}   (the Engine defaults)");
            _out.WriteLine($"gates set to '0'  -> {offExplicit}");

            // VACUITY GUARD, and it is load-bearing. If this scenario were insensitive to the three
            // gates, the equality below would hold no matter how the shim read them and this test would
            // assert nothing. Fail loudly instead -- a green test that cannot fail is worse than no test,
            // and this whole bug survived precisely because nothing exercised the path.
            Assert.True(
                onExplicit != offExplicit,
                "gates ON and OFF produced IDENTICAL output on synthetic-junction2, so this scenario no "
                + "longer discriminates the three junction gates and the assertion below is vacuous. "
                + "Pick a scenario that does, rather than leaving a test that cannot fail.");

            Assert.True(
                unset == onExplicit,
                "with the three junction gates UNSET the shim must fall back to the ENGINE DEFAULTS (all "
                + $"true), but its output ({unset}) matches neither that ({onExplicit}) nor -- if it "
                + $"equals {offExplicit} -- anything but the old `== \"1\"` two-state form, which forces "
                + "every absent gate OFF. See docs/ENV-GATES.md \"The three-state trap\".");
        }
        finally
        {
            for (var i = 0; i < Gates.Length; i++)
            {
                Environment.SetEnvironmentVariable(Gates[i], previous[i]);
            }
        }
    }

    // Runs the shim with all three gates set to `value` (null == removed from the environment) and
    // returns a hash of the produced FCD. A hash rather than a parsed comparison: the question here is
    // "is this the same simulation", for which byte equality is exactly the right and strictest answer.
    private static string RunAndHash(string cfg, string? value)
    {
        foreach (var gate in Gates)
        {
            Environment.SetEnvironmentVariable(gate, value);
        }

        var outDir = Path.Combine(Path.GetTempPath(), "sumosharp-unsetgate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var fcd = Path.Combine(outDir, "out.fcd.xml");
            var exit = SumoShim.Run(
                new[] { "-c", cfg, "--fcd-output", fcd, "--end", "600", "--no-step-log", "true" },
                new StringWriter(), new StringWriter());
            Assert.Equal(0, exit);

            using var stream = File.OpenRead(fcd);
            return Convert.ToHexString(SHA256.HashData(stream))[..16];
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "scenarios"))
                && File.Exists(Path.Combine(d.FullName, "Traffic.sln")))
            {
                return d.FullName;
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }
}
