using Sim.Sumo;
using Xunit;

namespace Sim.ParityTests;

// GAP-4 (docs/SUMOSHARP-CLI-MAXPARALLELISM.md): the serve CLI's `--max-parallelism N` flag. It is a
// PERFORMANCE knob only -- it caps Engine.MaxParallelism (the plan/willPass/emit Parallel.For degree)
// so the SumoData preprocessing sweep can cap per-sim threads and stop `workers x all-cores` from
// oversubscribing a many-core box. The HARD requirement is that it must NOT change results: the
// engine's parallel loops are order-independent, so the produced FCD/summary/statistic must be
// byte-identical regardless of the value. That invariance is what lets the SumoData timing sweep be
// trusted and what protects the committed goldens (which must stay byte-identical).
//
// These tests drive the shim IN-PROCESS (SumoShim.Run) exactly as SumoData would shell out, over the
// same 41-multifile-cfg scenario the GAP-1 CLI tests use, and assert:
//   1. output byte-invariance across --max-parallelism {1, 2, 4, omitted/default};
//   2. the flag is parsed ORDER-INDEPENDENTLY -- it works when it appears BEFORE `-c` (the SumoData
//      case: it rides in the SUMO_BINARY prefix ahead of the fixed `-c <cfg> ...` argv);
//   3. a bad value is a reported error, not an unhandled throw.
// PROCESS-GLOBAL ENV HAZARD -- this class drives Sim.Sumo.SumoShim.Run, and SumoShim reads the
// PROCESS-WIDE environment variable SUMOSHARP_CONTTURNFIX to set Engine.ContTurnInsideJunctionGate
// (SumoShim.cs:250). IgnoreJunctionBlockerTests SETS that variable around its own shim runs, so with
// xUnit's DEFAULT cross-class parallelism a concurrently-running shim test can observe the other
// class's value and silently simulate with a DIFFERENT engine configuration than it intended.
//
// This was not hypothetical: LowDensityTeleportTests failed 1 of 3 full-suite runs with exactly
// 5 teleports (vs its <= 2 ceiling) while passing every standalone run, and the leak was then
// reproduced deterministically -- `SUMOSHARP_CONTTURNFIX=1 dotnet test --filter LowDensityTeleportTests`
// fails with that identical message. Since LowDensityTeleportTests and DenseFlowDeadLaneDrainTests are
// two of the five load-bearing gridlock diagnostics, an unreliable one is worse than no diagnostic at
// all -- a false RED sends the next session chasing a regression that does not exist.
//
// Every class that calls SumoShim.Run therefore shares this collection, which xUnit runs SEQUENTIALLY.
// A NEW test that drives SumoShim.Run MUST join it. The robust fix (removing the process-global read
// entirely) is docs/NEED-sumoshim-process-global-contturn-env.md.
[Collection(SumoShimEnvCollection.Name)]
public class RungHDgap4MaxParallelismTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "41-multifile-cfg");
    private static readonly string Cfg = Path.Combine(ScenarioDir, "config.sumocfg");

    [Fact]
    public void MaxParallelism_DoesNotChangeOutput_ByteIdenticalAcrossValues()
    {
        // The default (flag omitted) run is the reference; every capped run must match it byte-for-byte.
        var reference = RunAndReadOutputs(maxParallelismArgs: Array.Empty<string>());

        foreach (var n in new[] { "1", "2", "4" })
        {
            var capped = RunAndReadOutputs(maxParallelismArgs: new[] { "--max-parallelism", n });

            Assert.True(BytesEqual(reference.Fcd, capped.Fcd),
                $"--max-parallelism {n} changed the FCD output (must be byte-identical to the default).");
            Assert.True(BytesEqual(reference.Summary, capped.Summary),
                $"--max-parallelism {n} changed the summary output (must be byte-identical to the default).");
            Assert.True(BytesEqual(reference.Statistic, capped.Statistic),
                $"--max-parallelism {n} changed the statistic output (must be byte-identical to the default).");
        }
    }

    [Fact]
    public void MaxParallelism_IsParsedBeforeConfig_ExactlyAsSumoDataShellsOut()
    {
        // SumoData carries the flag as a SUMO_BINARY prefix, so it lands BEFORE `-c <cfg>` in argv.
        // Parsing must be order-independent: a run with the flag first must produce the same bytes as
        // the reference run with the flag omitted entirely.
        var reference = RunAndReadOutputs(maxParallelismArgs: Array.Empty<string>());

        var outDir = NewTempDir();
        try
        {
            var fcd = Path.Combine(outDir, "F.xml");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = SumoShim.Run(
                new[]
                {
                    "--max-parallelism", "4", // BEFORE -c, exactly the SumoData prefix case
                    "-c", Cfg,
                    "--fcd-output", fcd,
                    "--end", "120",
                    "--no-step-log", "true",
                },
                stdout, stderr);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(fcd), "fcd-output not produced with prefix --max-parallelism");
            Assert.True(BytesEqual(reference.Fcd, File.ReadAllBytes(fcd)),
                "--max-parallelism placed before -c changed the FCD output (must be byte-identical).");
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void MaxParallelism_NonPositiveOrOmitted_MeansAllCores_SameBytes()
    {
        // N<=0 is documented to mean "all cores" -- the same as omitting the flag -- so it must produce
        // the same bytes as the default run (and must not error).
        var reference = RunAndReadOutputs(maxParallelismArgs: Array.Empty<string>());
        var zero = RunAndReadOutputs(maxParallelismArgs: new[] { "--max-parallelism", "0" });
        var negative = RunAndReadOutputs(maxParallelismArgs: new[] { "--max-parallelism", "-1" });

        Assert.True(BytesEqual(reference.Fcd, zero.Fcd), "--max-parallelism 0 should equal the default (all cores).");
        Assert.True(BytesEqual(reference.Fcd, negative.Fcd), "--max-parallelism -1 should equal the default (all cores).");
    }

    [Fact]
    public void MaxParallelism_NonIntegerValue_IsReportedNotThrown()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = SumoShim.Run(
            new[] { "-c", Cfg, "--max-parallelism", "lots", "--end", "10" },
            stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Contains("--max-parallelism", stderr.ToString());
    }

    // Runs the shim over the shared scenario with the given --max-parallelism argv (possibly empty) and
    // returns the raw bytes of the three deterministic output files (no timestamps in any writer, so
    // byte-equality is a valid invariance assertion).
    private static (byte[] Fcd, byte[] Summary, byte[] Statistic) RunAndReadOutputs(string[] maxParallelismArgs)
    {
        var outDir = NewTempDir();
        try
        {
            var fcd = Path.Combine(outDir, "F.xml");
            var summary = Path.Combine(outDir, "S.xml");
            var statistic = Path.Combine(outDir, "T.xml");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var args = new List<string>
            {
                "-c", Cfg,
                "--fcd-output", fcd,
                "--summary-output", summary,
                "--statistic-output", statistic,
                "--end", "120",
                "--no-step-log", "true",
            };
            args.AddRange(maxParallelismArgs);

            var exit = SumoShim.Run(args.ToArray(), stdout, stderr);
            Assert.Equal(0, exit);

            return (File.ReadAllBytes(fcd), File.ReadAllBytes(summary), File.ReadAllBytes(statistic));
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static bool BytesEqual(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sumosharp-gap4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
