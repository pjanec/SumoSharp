using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.LiveCity;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// HEAD-OF-QUEUE STALL PROBE -- the diagnostic that attributes gridlock to the vehicle that is ACTUALLY
// stuck, not to the queue behind it.
//
// WHY THIS IS A COMMITTED TEST AND NOT A SCRATCH EDIT. The first version of this probe was a temporary
// modification, run once and reverted. Its conclusion (48.1% of stall heads held by binder 14) then had to
// be taken on trust for a whole session because the instrument no longer existed. Worse, the measurement it
// REPLACED was wrong in a way only a head/follower split can expose: a population-level count of stalled
// cars had been read as "predominantly legitimate saturation" when 94% of the actual heads were one defect.
// See docs/F3-SESSION-LOG.md §9.100 (the wrong reading) and §9.103-106 (the correction).
//
// THE DISTINCTION IT ENFORCES. In any jam most stalled cars are stalled *because the car ahead is*. Those
// followers are not evidence of anything; they are the shadow of the head. Measured at 3x demo density:
// 618 heads vs 2327 followers, and the followers were 97.2% `leaderFollow` -- pure queue noise that would
// have drowned the signal in any population metric. So:
//
//     a deeply-stalled vehicle is a FOLLOWER iff another deeply-stalled vehicle sits
//     within FollowerGapMetres AHEAD of it on the SAME lane; otherwise it is a HEAD.
//
// Only heads get their binding constraint tallied.
//
// ⚠ THIS IS AN INSTRUMENT, NOT A GATE, AND ITS ASSERTIONS ARE VACUOUS UNLESS YOU ENABLE IT. Two full
// simulated hours at 3x density is minutes of wall clock, far too slow to sit in `dotnet test`. So the body
// early-returns unless `HEADPROBE=1`, and in the default suite run it asserts NOTHING -- it is committed for
// reproducibility, not for coverage. Do not read a green `dotnet test` as evidence the wedge is gone; only a
// HEADPROBE=1 run is evidence. Run it when a gridlock claim needs backing:
//
//     dotnet build -c Release tests/Sim.LiveCity.Tests/Sim.LiveCity.Tests.csproj
//     HEADPROBE=1 dotnet test tests/Sim.LiveCity.Tests -c Release \
//       --filter HeadOfQueueStallProbeTests
//
// ⚠ `dotnet build -c Release` alone does NOT rebuild this project -- Sim.LiveCity.Tests is not in
// Traffic.sln. Build the csproj explicitly or you will measure stale code. That trap already produced two
// contradictory numbers for the same configuration in one session (§7).
public class HeadOfQueueStallProbeTests
{
    private readonly ITestOutputHelper _out;
    public HeadOfQueueStallProbeTests(ITestOutputHelper output) { _out = output; }

    private const string ScratchDir =
        "/tmp/claude-0/-home-user-SumoSharp/e21d49f3-f27d-5fd7-845f-7d5806744c6e/scratchpad";

    // A stopped run this long is a stall, not a traffic light. 300 steps @ dt=0.5 = 150 s -- longer than
    // any signal cycle in the demo net, which is what makes it a defect signal rather than normal waiting.
    private const int DeepStallSteps = 300;

    // Speed below which a vehicle counts as stopped. Same value the long-horizon diagnostic uses, so the
    // two instruments' "stopped" agree.
    private const double StoppedThreshold = 0.5;

    // How far ahead to look for the car that explains ego's stall. A stopped car's front bumper sits
    // ~1 vehicle length + minGap behind its leader's rear; 15 m covers that plus slack without reaching
    // past a genuinely separate stall further up the lane.
    private const double FollowerGapMetres = 15.0;

    // CONTRACT: adding a new LIVECITY_* engine gate REQUIRES adding it here. These are process-global, so
    // an unset gate silently inherits the caller's shell and contaminates the column it claims to measure
    // -- see the identical warning (and the concrete 392-vs-1295 contamination) in
    // LongHorizonGridlockDiagTests.RunConfig.
    private static readonly string[] AllLiveCityGateVars =
    {
        "LIVECITY_CONTTURNFIX",
        "LIVECITY_ISLEADERFIX",
        "LIVECITY_INTERNALJUNCTIONFIX",
        "LIVECITY_INTERNALJUNCTIONENTRYORDER",
        "LIVECITY_INSERTIONFOLLOWERGAP",
        "LIVECITY_COLOCATIONSYMMETRYBREAK",
        "LIVECITY_LANECHANGEARBITRATION",
    };

    private static readonly Dictionary<byte, string> BinderNames = new()
    {
        [0] = "none", [1] = "leaderFollow", [2] = "crossJxnLeader", [3] = "freeFlow",
        [4] = "successiveLaneSpeed", [5] = "deadLaneMerge", [6] = "stopLine", [7] = "redLight",
        [8] = "railSignal", [9] = "railCrossing", [10] = "junctionYield", [11] = "keepClear",
        [12] = "obstacle", [13] = "crowd", [14] = "internalJxnAdmission", [15] = "colocationBreak",
    };

    private static string RepoRoot()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && Directory.Exists(Path.Combine(output, "scenarios"))) return output;
        }
        catch { }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios")) && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private sealed record Stall(string Vehicle, string Lane, double Pos, int Length, byte Binder);

    private sealed class ProbeResult
    {
        public string Label = string.Empty;
        public int Steps;
        public long Arrived;
        public List<Stall> Heads = new();
        public List<Stall> Followers = new();
        public Dictionary<byte, int> HeadBinders = new();
        // SC1's direct assertion target: a deep stall standing on an INTERNAL lane held by arm 14 is the
        // bay wedge itself. Tracked separately from the binder histogram so the wedge can be asserted on
        // without reasoning about percentages.
        public List<Stall> BayWedge = new();
    }

    // `overrides` sets individual gates AFTER the blanket on/off, so a single variable can be isolated
    // without losing the "every gate set explicitly" guarantee: the blanket loop still writes all of them,
    // and the override then names exactly what differs. Attributing a change to one gate requires this --
    // comparing all-OFF against all-ON conflates seven gates and cannot attribute anything.
    private ProbeResult Probe(
        string label, bool gatesOn, int cars, int maxSteps, string repoRoot, StreamWriter log,
        Dictionary<string, string>? overrides = null)
    {
        foreach (var gate in AllLiveCityGateVars) Environment.SetEnvironmentVariable(gate, gatesOn ? "1" : "0");
        if (overrides is not null)
        {
            foreach (var kv in overrides)
            {
                Assert.Contains(kv.Key, AllLiveCityGateVars); // an override naming an unknown gate is a silent no-op
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            }
        }
        Environment.SetEnvironmentVariable("LIVECITY_CARS", cars.ToString());

        var cfg = LiveCityConfig.ForRepoRoot(repoRoot);
        using var sim = new LiveCitySim(cfg);
        var res = new ProbeResult { Label = label };

        var nameByHandle = new Dictionary<VehicleHandle, string>();
        // Open stopped runs: handle -> (lane it stalled on, first step of the run, last-seen pos/binder).
        var open = new Dictionary<VehicleHandle, (string Lane, int Start, double Pos, byte Binder)>();
        // Completed deep stalls, keyed by the step at which they ended, so heads/followers can be
        // classified against the state of the lane WHILE the stall was happening (below).
        var deep = new List<Stall>();

        // Snapshot of every deeply-stalled vehicle at the moment of the deepest congestion, used for the
        // head/follower split. Taken at the step where the count of currently-open deep runs peaks --
        // that is the state a viewer would be looking at when they call the demo gridlocked.
        var peakDeepCount = -1;
        var peakSnapshot = new List<Stall>();

        var st = 0;
        for (; st < maxSteps; st++)
        {
            sim.Step();
            foreach (var c in sim.Sample().Cars) nameByHandle[c.Handle] = c.Name;
            var witnesses = sim.WitnessAuthoritative();

            var present = new Dictionary<VehicleHandle, (string Lane, double Speed, double Pos, byte Binder)>(witnesses.Count);
            foreach (var w in witnesses) present[w.Handle] = (w.LaneId ?? string.Empty, w.Speed, w.Pos, w.Binder);

            // Close runs that ended (gone, moving again, or changed lane).
            foreach (var h in open.Keys.ToList())
            {
                var run = open[h];
                var stillStopped = present.TryGetValue(h, out var p)
                    && p.Speed < StoppedThreshold
                    && p.Lane == run.Lane;
                if (stillStopped)
                {
                    open[h] = (run.Lane, run.Start, p.Pos, p.Binder);
                    continue;
                }

                var len = st - run.Start;
                if (len > DeepStallSteps)
                {
                    deep.Add(new Stall(
                        nameByHandle.TryGetValue(h, out var nm) ? nm : h.ToString(),
                        run.Lane, run.Pos, len, run.Binder));
                }
                open.Remove(h);
            }

            // Open new runs.
            foreach (var kv in present)
            {
                if (kv.Value.Speed >= StoppedThreshold) continue;
                if (open.ContainsKey(kv.Key)) continue;
                open[kv.Key] = (kv.Value.Lane, st, kv.Value.Pos, kv.Value.Binder);
            }

            // Peak-congestion snapshot over the currently-OPEN deep runs.
            var openDeep = open.Where(kv => st - kv.Value.Start > DeepStallSteps).ToList();
            if (openDeep.Count > peakDeepCount)
            {
                peakDeepCount = openDeep.Count;
                peakSnapshot = openDeep.Select(kv => new Stall(
                    nameByHandle.TryGetValue(kv.Key, out var nm) ? nm : kv.Key.ToString(),
                    kv.Value.Lane, kv.Value.Pos, st - kv.Value.Start, kv.Value.Binder)).ToList();
            }
        }

        // Flush runs still open at the horizon.
        foreach (var kv in open)
        {
            var len = st - kv.Value.Start;
            if (len > DeepStallSteps)
            {
                deep.Add(new Stall(
                    nameByHandle.TryGetValue(kv.Key, out var nm) ? nm : kv.Key.ToString(),
                    kv.Value.Lane, kv.Value.Pos, len, kv.Value.Binder));
            }
        }

        res.Steps = st;
        res.Arrived = sim.ArrivedTotal;

        // ---- the head/follower split, over the peak-congestion snapshot ----
        // A deep stall is a FOLLOWER iff another deep stall sits within FollowerGapMetres AHEAD of it on
        // the SAME lane. Everything else is a HEAD. Both lists come from ONE snapshot so "ahead of" is a
        // statement about a single instant -- comparing across steps would let a car that stalled and
        // cleared count as another car's blocker.
        foreach (var s in peakSnapshot)
        {
            var hasBlockerAhead = peakSnapshot.Any(o =>
                !ReferenceEquals(o, s) && o.Lane == s.Lane && o.Pos > s.Pos && o.Pos - s.Pos <= FollowerGapMetres);
            if (hasBlockerAhead) res.Followers.Add(s); else res.Heads.Add(s);
        }

        foreach (var h in res.Heads)
        {
            res.HeadBinders.TryGetValue(h.Binder, out var n);
            res.HeadBinders[h.Binder] = n + 1;
        }

        // The wedge: a deep stall on an internal lane held by arm 14.
        res.BayWedge = deep.Where(s => s.Binder == 14 && s.Lane.StartsWith(":", StringComparison.Ordinal)).ToList();

        log.WriteLine($"=== {label}: cars={cars} steps={res.Steps} arrived={res.Arrived}");
        log.WriteLine($"    deep stalls (>{DeepStallSteps} steps, whole run) = {deep.Count}");
        log.WriteLine($"    peak concurrent deep stalls = {peakDeepCount}  -> HEADS={res.Heads.Count} FOLLOWERS={res.Followers.Count}");
        foreach (var kv in res.HeadBinders.OrderByDescending(k => k.Value))
        {
            var pct = res.Heads.Count == 0 ? 0.0 : 100.0 * kv.Value / res.Heads.Count;
            log.WriteLine($"      head binder {kv.Key,2} {BinderNames.GetValueOrDefault(kv.Key, "?"),-22} {kv.Value,5}  {pct,5:F1}%");
        }
        log.WriteLine($"    ARM-14 BAY WEDGE (deep stall on internal lane, binder 14) = {res.BayWedge.Count}");
        foreach (var g in res.BayWedge.GroupBy(s => s.Lane).OrderByDescending(g => g.Max(s => s.Length)).Take(12))
        {
            log.WriteLine($"      {g.Key,-24} n={g.Count(),3} longest={g.Max(s => s.Length),5} steps  pos={g.First().Pos:F2}");
        }
        log.Flush();
        return res;
    }

    [Fact]
    public void HeadOfQueue_ArmFourteenWedge_ThreeXDensity()
    {
        if (Environment.GetEnvironmentVariable("HEADPROBE") != "1")
        {
            // Deliberately vacuous here -- see the class comment. Minutes of wall clock do not belong in
            // the gate suite, and pretending otherwise by asserting something cheap would be worse.
            _out.WriteLine("SKIPPED: set HEADPROBE=1 to run the head-of-queue probe (two hours of sim time).");
            return;
        }

        var repoRoot = RepoRoot();
        Directory.CreateDirectory(ScratchDir);
        using var log = new StreamWriter(Path.Combine(ScratchDir, "head-of-queue-probe.log"), append: false);

        const int steps = 7200;   // one simulated hour @ dt=0.5
        const int cars3x = 480;   // 3x the demo's 160 -- the density at which the wedge is dominant

        // THREE columns, because two cannot attribute anything:
        //   OFF        the shipped default -- the regression reference.
        //   ON, noOrd  every gate on EXCEPT the entry-order sub-gate: the bare-occupancy admission rule
        //              as originally shipped. This is the column the fix must beat.
        //   ON         every gate on. Differs from `ON, noOrd` in EXACTLY ONE variable.
        var off3x = Probe("3x gates OFF", gatesOn: false, cars3x, steps, repoRoot, log);
        var noOrd3x = Probe("3x gates ON, entry-order OFF", gatesOn: true, cars3x, steps, repoRoot, log,
            new Dictionary<string, string> { ["LIVECITY_INTERNALJUNCTIONENTRYORDER"] = "0" });
        var on3x = Probe("3x ALL GATES ON", gatesOn: true, cars3x, steps, repoRoot, log);

        foreach (var r in new[] { off3x, noOrd3x, on3x })
        {
            _out.WriteLine($"{r.Label,-32} arrived={r.Arrived,6} heads={r.Heads.Count,4} followers={r.Followers.Count,4} wedge={r.BayWedge.Count,4}");
        }
        foreach (var kv in on3x.HeadBinders.OrderByDescending(k => k.Value))
            _out.WriteLine($"  ON head binder {kv.Key} {BinderNames.GetValueOrDefault(kv.Key, "?")} = {kv.Value}");

        // ---- SC1: the entry-time ordering must break the circular wait ----
        // Asserted as a comparison against the bare-occupancy column, not against a remembered number:
        // the previous session's 48.1%-of-618-heads figure came from a DIFFERENT (now-deleted) instrument
        // and is not comparable to this one's peak-snapshot counts. Only same-instrument columns may be
        // compared -- that mistake is why this probe is committed at all.
        Assert.True(on3x.BayWedge.Count < noOrd3x.BayWedge.Count,
            $"entry-order ordering did not reduce the bay wedge: {on3x.BayWedge.Count} vs {noOrd3x.BayWedge.Count}");
        Assert.True(on3x.Heads.Count < noOrd3x.Heads.Count,
            $"entry-order ordering did not reduce stall heads: {on3x.Heads.Count} vs {noOrd3x.Heads.Count}");

        // ⚠ THE WEDGE IS REDUCED, NOT ELIMINATED -- this bound is the MEASURED RESIDUAL, not the goal.
        // 9 deep stalls still stand on internal lanes held by arm 14 (junctions d_5_3 / d_5_4, longest 637
        // steps). The goal is 0. This assertion exists only so the residual cannot silently grow while the
        // remaining cause is being found; see docs/F3-SESSION-LOG.md §6.
        const int MeasuredWedgeResidual = 9;
        Assert.True(on3x.BayWedge.Count <= MeasuredWedgeResidual,
            $"bay wedge grew past the measured residual: {on3x.BayWedge.Count} > {MeasuredWedgeResidual}");

        // Throughput must not have been bought by refusing to let cars in.
        Assert.True(on3x.Arrived >= off3x.Arrived,
            $"3x throughput regressed with gates on: {on3x.Arrived} < {off3x.Arrived}");
    }
}
