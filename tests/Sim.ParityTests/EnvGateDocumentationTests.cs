using System.Text.RegularExpressions;

namespace Sim.ParityTests;

// Completeness tripwire for docs/ENV-GATES.md.
//
// WHY THIS EXISTS. The LIVECITY_* / SUMOSHARP_* / CITY3D_* gates are PROCESS-GLOBAL environment
// variables, so a value inherited from the shell is indistinguishable from one a measurement set
// deliberately (CLAUDE.md measurement-discipline #10). That has already cost this project a real
// measurement -- an inherited gate produced a 392-vs-1295 "OFF" baseline. An undocumented gate is
// therefore not a tidiness problem: it is a gate nobody knows to pin, and one of them
// (LIVECITY_MINORARRIVALSPEED) breaks 14 goldens when set.
//
// Unlike the CLI flags, these have no `--help` to fall back on, so the doc IS the discovery surface.
// This test makes the machine own COMPLETENESS (every gate the code reads has a row, and every row
// corresponds to a gate the code reads) while the doc's prose owns MEANING. A new gate fails the
// build until someone describes it.
//
// Deliberately NOT asserted here: that a gate is read via EnvGate() rather than the unsafe
// `== "1"` two-state form. Three SumoShim gates currently use the unsafe form against engine
// defaults of `true` -- a real open bug, tracked in docs/TASKS-TODO.md and documented in
// docs/ENV-GATES.md. Asserting the safe form would fail on that known bug rather than on a
// regression, so it stays a documented finding until it is fixed; then this test can be tightened.
public class EnvGateDocumentationTests
{
    // Both read forms. `EnvGate(name, engineDefault)` falls back to the engine default; the bare
    // GetEnvironmentVariable form is usually compared against "1" or "0" by the caller.
    private static readonly Regex ReadSite = new(
        "(?:GetEnvironmentVariable|EnvGate)\\(\\s*\"((?:LIVECITY|SUMOSHARP|CITY3D)_[A-Z0-9_]*)\"",
        RegexOptions.Compiled);

    // A documented gate is the first cell of a markdown table row, wrapped in backticks.
    private static readonly Regex DocRow = new(
        @"^\|\s*`((?:LIVECITY|SUMOSHARP|CITY3D)_[A-Z0-9_]*)`\s*\|",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Gates named in the prose warning table rather than an inventory row would be missed by DocRow
    // alone; the drop-in section lists all three in a normal row, so no exemption is needed today.
    // Keep this empty unless there is a gate that genuinely cannot have a row.
    private static readonly HashSet<string> Exempt = new();

    [Fact]
    public void EveryEnvGateTheCodeReadsIsDocumented()
    {
        var inSource = ScanSource();
        var documented = ScanDoc();

        Assert.NotEmpty(inSource);

        var undocumented = inSource.Keys.Except(documented).Except(Exempt).OrderBy(g => g).ToList();

        Assert.True(
            undocumented.Count == 0,
            $"{undocumented.Count} environment gate(s) are read by the code but have no row in "
            + "docs/ENV-GATES.md. These are process-global and have no --help, so an undocumented gate "
            + "is one nobody knows to pin in an A/B. Add a row (gate, what it sets, what an unset value "
            + "means, and its class) for each:\n"
            + string.Join("\n", undocumented.Select(g => $"  {g}  -- read at {inSource[g]}")));
    }

    [Fact]
    public void EveryDocumentedEnvGateIsStillReadByTheCode()
    {
        var inSource = ScanSource();
        var documented = ScanDoc();

        Assert.NotEmpty(documented);

        var stale = documented.Except(inSource.Keys).OrderBy(g => g).ToList();

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} gate(s) are documented in docs/ENV-GATES.md but no longer read anywhere in "
            + "src/ or demos/. A documented gate that does nothing is worse than an undocumented one -- "
            + "someone will set it and believe it took effect. Remove the row, or say in it that the gate "
            + "is retired:\n" + string.Join("\n", stale.Select(g => $"  {g}")));
    }

    // gate name -> first "file:line" it is read at, for a failure message that points somewhere useful.
    private static Dictionary<string, string> ScanSource()
    {
        var root = RepoRoot();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var dir in new[] { "src", "demos" })
        {
            var abs = Path.Combine(root, dir);
            if (!Directory.Exists(abs))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(abs, "*.cs", SearchOption.AllDirectories))
            {
                // obj/ and bin/ hold generated and copied sources; scanning them would double-count
                // and could resurrect a gate deleted from the real source.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in ReadSite.Matches(lines[i]))
                    {
                        var name = m.Groups[1].Value;
                        if (name.Length == 0)
                        {
                            continue;
                        }

                        var where = $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}";
                        if (!found.ContainsKey(name))
                        {
                            found[name] = where;
                        }
                    }
                }
            }
        }

        return found;
    }

    private static HashSet<string> ScanDoc()
    {
        var path = Path.Combine(RepoRoot(), "docs", "ENV-GATES.md");
        Assert.True(File.Exists(path), $"docs/ENV-GATES.md is missing (looked at {path}).");

        var text = File.ReadAllText(path);
        return DocRow.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repo root (Traffic.sln not found above the test assembly).");
    }
}
