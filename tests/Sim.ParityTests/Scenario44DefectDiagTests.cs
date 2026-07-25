using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sim.Core;
using Sim.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Scenario-44 defect-repro investigation (diagnostic instrument, NOT a regression guard).
//
// Purpose: determine whether scenarios/44-multilane-junction-turn is a solid, committed, offline
// repro of the cont-turn/junction-yield defect described in its provenance.txt (bugs A and B), and
// print the exact per-step numbers an investigator needs to see the defect and later confirm the fix.
//
// This test makes NO assertion on the hypothesis outcome -- it only asserts the harness precondition
// (LIVECITY_F3OCCUPANCY unset, matching the default engine configuration) and otherwise always
// passes. It exists purely to print a report via ITestOutputHelper.
//
// Drives the engine with Step() (not Run()) so the per-step diagnostic read-surface
// (BindingConstraints / JunctionYieldArms, Engine.cs ~2081-2088) is populated; Step()'s Advance(null,
// steps) is the same per-step driver Run() uses (Engine.cs:1929-1934, 2107-2111), so the simulation
// itself is identical to the skipped parity test's engine.Run(45) -- only the read-projection differs.
public class Scenario44DefectDiagTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "44-multilane-junction-turn");

    private readonly ITestOutputHelper _out;

    public Scenario44DefectDiagTests(ITestOutputHelper output) => _out = output;

    // BindingConstraint id -> name, per Engine.cs:5071-5073 (mirrors F3JunctionOverlapDiagTests).
    private static readonly string[] BinderNames =
    {
        "0:none(unconstrained)", "1:leaderFollow", "2:crossJxnLeader", "3:freeFlow", "4:successiveLane",
        "5:deadLaneMerge", "6:stopLine", "7:redLight", "8:railSignal", "9:railCrossing",
        "10:junctionYield", "11:keepClear", "12:obstacle", "13:crowd",
    };

    private static string BinderName(byte b) => b < BinderNames.Length ? BinderNames[b] : $"{b}:UNKNOWN";

    // JunctionYieldArm low bits -> arm name, per Engine.cs ~6805-7201 (mirrors F3JunctionOverlapDiagTests).
    private static readonly string[] JyArmNames =
    {
        "0:none", "1:cycleHold", "2:cautiousApproach", "3:sameTargetMerge",
        "4:externalAgent", "5:adaptToJunctionLeader", "6:approachingCross",
    };

    private static string JyArmDecoded(byte raw)
    {
        var arm = raw & 0x7F;
        var priority = (raw & 0x80) != 0;
        var name = arm < JyArmNames.Length ? JyArmNames[arm] : $"{arm}:UNKNOWN";
        return $"{name}{(priority ? "+prio" : "")}";
    }

    private sealed record Row(int Step, string VehicleId, string LaneId, double Pos, double Speed, byte Binder, byte JyArm);

    [Fact]
    public void Scenario44_DefectReproductionReport()
    {
        Assert.True(
            Environment.GetEnvironmentVariable("LIVECITY_F3OCCUPANCY") is null or "0",
            "LIVECITY_F3OCCUPANCY must be unset/0 for this diagnostic -- it must measure the DEFAULT configuration.");

        const int steps = 45;
        var vehicleIdsOfInterest = new[] { "vW", "vN", "vE", "vS" };

        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));

        var rows = new List<Row>();

        for (var st = 0; st < steps; st++)
        {
            engine.Step();

            var ids = engine.VehicleIds;
            var lanes = engine.LaneIds;
            var pos = engine.Pos;
            var speed = engine.Speed;
            var binder = engine.BindingConstraints;
            var jyArm = engine.JunctionYieldArms;

            for (var i = 0; i < ids.Length; i++)
            {
                rows.Add(new Row(st + 1, ids[i], lanes[i], pos[i], speed[i], binder[i], jyArm[i]));
            }
        }

        _out.WriteLine(new string('=', 100));
        _out.WriteLine("SCENARIO 44 DEFECT REPRODUCTION REPORT");
        _out.WriteLine(new string('=', 100));

        // ---- 1. Per-step per-vehicle trace ----
        _out.WriteLine("");
        _out.WriteLine("--- full per-step trace (step | vehId | laneId | pos | speed | BindingConstraint | JunctionYieldArm) ---");
        foreach (var v in vehicleIdsOfInterest)
        {
            foreach (var r in rows.Where(r => r.VehicleId == v).OrderBy(r => r.Step))
            {
                _out.WriteLine(
                    $"step={r.Step,3} | veh={r.VehicleId} | lane={r.LaneId,-10} | pos={r.Pos,7:F3} | speed={r.Speed,6:F3} "
                    + $"| Binder={r.Binder,2} ({BinderName(r.Binder),-20}) | JyArm={JyArmDecoded(r.JyArm)}");
            }

            _out.WriteLine("");
        }

        // ---- 2. Internal-lane traversal per vehicle ----
        _out.WriteLine(new string('-', 100));
        _out.WriteLine("--- internal (':') lane sequence actually traversed, per vehicle ---");
        var internalSeqByVeh = new Dictionary<string, List<string>>();
        foreach (var v in vehicleIdsOfInterest)
        {
            var seq = new List<string>();
            string? last = null;
            foreach (var r in rows.Where(r => r.VehicleId == v).OrderBy(r => r.Step))
            {
                if (r.LaneId.Length > 0 && r.LaneId[0] == ':' && r.LaneId != last)
                {
                    seq.Add(r.LaneId);
                }

                last = r.LaneId;
            }

            internalSeqByVeh[v] = seq;
            _out.WriteLine($"{v}: [{string.Join(" -> ", seq)}]");
        }

        var secondStageByVeh = new Dictionary<string, string>
        {
            ["vN"] = ":C_16_0", // NC->CE
            ["vS"] = ":C_17_0", // SC->CW
            ["vE"] = "(single-stage :C_7_0, no second stage)",
            ["vW"] = "(single-stage :C_15_0, no second stage)",
        };

        _out.WriteLine("");
        foreach (var v in new[] { "vN", "vS" })
        {
            var occupied = internalSeqByVeh[v].Contains(secondStageByVeh[v]);
            _out.WriteLine($"BUG A CHECK ({v}): expected second-stage lane {secondStageByVeh[v]} -- occupied={occupied}");
        }

        // ---- 3. Stops on internal lanes ----
        _out.WriteLine("");
        _out.WriteLine(new string('-', 100));
        _out.WriteLine("--- stopped-on-internal-lane runs (speed < 0.5, lane starts with ':') ---");
        foreach (var v in vehicleIdsOfInterest)
        {
            var vRows = rows.Where(r => r.VehicleId == v).OrderBy(r => r.Step).ToList();
            var runs = FindStoppedRuns(vRows, 0.5, internalOnly: true);
            if (runs.Count == 0)
            {
                _out.WriteLine($"{v}: no stopped-on-internal-lane run.");
            }
            else
            {
                foreach (var (lane, startStep, endStep, pos) in runs)
                {
                    _out.WriteLine(
                        $"{v}: STOPPED on {lane} steps {startStep}-{endStep} ({endStep - startStep + 1} steps) at pos={pos:F3}");
                }
            }
        }

        // Also check the reported bug-B lane (CE_1 -> CE_0 stranding vN, non-internal lane end stall).
        _out.WriteLine("");
        _out.WriteLine("--- stopped-on-ANY-lane runs for vN (bug B: CE_1->CE_0 strand near pos 189.6) ---");
        var vNRows = rows.Where(r => r.VehicleId == "vN").OrderBy(r => r.Step).ToList();
        var vNStoppedRuns = FindStoppedRuns(vNRows, 0.5, internalOnly: false);
        foreach (var (lane, startStep, endStep, pos) in vNStoppedRuns)
        {
            _out.WriteLine($"vN: STOPPED on {lane} steps {startStep}-{endStep} ({endStep - startStep + 1} steps) at pos={pos:F3}");
        }

        // ---- 4. Arrivals ----
        _out.WriteLine("");
        _out.WriteLine(new string('-', 100));
        _out.WriteLine("--- arrival check (did the vehicle disappear from the read surface before step 45, i.e. arrived?) ---");
        foreach (var v in vehicleIdsOfInterest)
        {
            var vRows = rows.Where(r => r.VehicleId == v).OrderBy(r => r.Step).ToList();
            if (vRows.Count == 0)
            {
                _out.WriteLine($"{v}: NEVER OBSERVED in any step (departed and arrived within the same step, or never departed).");
                continue;
            }

            var last = vRows[^1];
            var arrivedBeforeEnd = last.Step < steps;
            _out.WriteLine(
                $"{v}: last observed step={last.Step} lane={last.LaneId} pos={last.Pos:F3} speed={last.Speed:F3} "
                + (arrivedBeforeEnd
                    ? "-- disappears before step 45 => ARRIVED (or removed)"
                    : "-- STILL PRESENT at step 45 => DID NOT ARRIVE within the 45-step window"));
        }

        // ---- 5. Golden comparison ----
        _out.WriteLine("");
        _out.WriteLine(new string('-', 100));
        _out.WriteLine("--- golden.fcd.xml lane sequence per vehicle (SUMO reference) ---");
        var golden = FcdParser.Parse(Path.Combine(ScenarioDir, "golden.fcd.xml"));
        var goldenByVeh = golden.AllPoints.GroupBy(p => p.VehicleId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Time).ToList());

        foreach (var v in vehicleIdsOfInterest)
        {
            if (!goldenByVeh.TryGetValue(v, out var pts))
            {
                _out.WriteLine($"{v}: not present in golden.fcd.xml");
                continue;
            }

            var distinctLanes = new List<string>();
            string? last = null;
            double? junctionEnterT = null;
            double? junctionClearT = null;
            foreach (var p in pts)
            {
                if (p.Lane != last)
                {
                    distinctLanes.Add(p.Lane);
                }

                if (p.Lane.StartsWith(':') && junctionEnterT is null)
                {
                    junctionEnterT = p.Time;
                }

                if (junctionEnterT is not null && !p.Lane.StartsWith(':') && p.Time > junctionEnterT && junctionClearT is null)
                {
                    junctionClearT = p.Time;
                }

                last = p.Lane;
            }

            var lastPt = pts[^1];
            _out.WriteLine(
                $"{v}: lanes=[{string.Join(" -> ", distinctLanes)}] junctionEnter={junctionEnterT?.ToString("F0") ?? "n/a"} "
                + $"junctionClear={junctionClearT?.ToString("F0") ?? "n/a"} lastSeenT={lastPt.Time:F0} lastLane={lastPt.Lane} lastPos={lastPt.Pos:F3} lastSpeed={lastPt.Speed:F3}");
        }

        _out.WriteLine("");
        _out.WriteLine(new string('-', 100));
        _out.WriteLine("--- engine vs SUMO: does SUMO traverse the second-stage lane where the engine does not? ---");
        foreach (var v in new[] { "vN", "vS" })
        {
            var expected = secondStageByVeh[v];
            var goldenTraversesIt = goldenByVeh.TryGetValue(v, out var pts) && pts.Any(p => p.Lane == expected);
            var engineTraversesIt = internalSeqByVeh[v].Contains(expected);
            _out.WriteLine($"{v}: expected second-stage lane {expected} -- SUMO traverses it={goldenTraversesIt}, engine traverses it={engineTraversesIt}");
        }

        // ---- 6. Proposed metric set ----
        _out.WriteLine("");
        _out.WriteLine(new string('=', 100));
        _out.WriteLine("PROPOSED PASS/FAIL METRICS FOR 'DEFECT FIXED' ON SCENARIO 44");
        _out.WriteLine(new string('=', 100));

        var vNOccupies16 = internalSeqByVeh["vN"].Contains(":C_16_0");
        var vSOccupies17 = internalSeqByVeh["vS"].Contains(":C_17_0");
        var anyStoppedInternal = vehicleIdsOfInterest.Any(v => FindStoppedRuns(rows.Where(r => r.VehicleId == v).OrderBy(r => r.Step).ToList(), 0.5, internalOnly: true).Count > 0);
        var arrivedCount = vehicleIdsOfInterest.Count(v =>
        {
            var vRows = rows.Where(r => r.VehicleId == v).OrderBy(r => r.Step).ToList();
            return vRows.Count > 0 && vRows[^1].Step < steps;
        });

        _out.WriteLine($"metric 1: vN traverses :C_3_0 -> :C_16_0 (cont-turn sequence).      CURRENT={vNOccupies16}   TARGET=True");
        _out.WriteLine($"metric 2: vS traverses :C_11_0 -> :C_17_0 (cont-turn sequence).     CURRENT={vSOccupies17}   TARGET=True");
        _out.WriteLine($"metric 3: zero (vehicle,internal-lane) stopped(<0.5 m/s) runs.       CURRENT={(anyStoppedInternal ? "FOUND >=1 stop" : "0 stops")}   TARGET=0 stops");
        _out.WriteLine($"metric 4: all 4 vehicles ARRIVE within {steps} steps.                CURRENT={arrivedCount}/4 arrived   TARGET=4/4 arrived by step 38 (golden clears by t=38)");
        // Actually run the exact same comparison the skipped parity test performs (fresh engine
        // instance, engine.Run(45) against golden.fcd.xml/tolerance.json) so metric 5's CURRENT value
        // is measured, not assumed from the [Fact(Skip)] attribute alone.
        var freshEngine = new Engine();
        freshEngine.LoadScenario(
            Path.Combine(ScenarioDir, "net.net.xml"),
            Path.Combine(ScenarioDir, "rou.rou.xml"),
            Path.Combine(ScenarioDir, "config.sumocfg"));
        var actualTraj = freshEngine.Run(steps);
        var toleranceCfg = ToleranceConfig.Load(Path.Combine(ScenarioDir, "tolerance.json"));
        var comparison = TrajectoryComparator.Compare(actualTraj, golden, toleranceCfg);

        _out.WriteLine("metric 5: golden FCD parity (lane,pos,speed) within tolerance.json (parityMode=exact, pos/speed atol=0.001) over the full 45-step run.");
        _out.WriteLine($"    MEASURED (fresh engine.Run(45) vs golden, via TrajectoryComparator.Compare): IsMatch={comparison.IsMatch}, FirstDivergenceStep={comparison.FirstDivergenceStep?.ToString() ?? "none"}");
        foreach (var attribute in comparison.Attributes)
        {
            _out.WriteLine($"        attribute={attribute.Attribute} maxAbsError={attribute.MaxAbsError} rmse={attribute.Rmse} withinTolerance={attribute.WithinTolerance}");
        }

        if (comparison.PresenceMismatches.Count > 0)
        {
            _out.WriteLine("        presence mismatches:");
            foreach (var mismatch in comparison.PresenceMismatches)
            {
                _out.WriteLine($"            {mismatch.Kind} vehicle={mismatch.VehicleId} time={mismatch.Time?.ToString() ?? "n/a"}");
            }
        }

        _out.WriteLine("    TARGET: IsMatch=true, FirstDivergenceStep=none, 0 presence mismatches.");

        Assert.True(true, "diagnostic-only test; see report above.");
    }

    // Longest-style enumeration of ALL contiguous (vehicle already filtered by caller) stopped runs on
    // a single lane -- returns every run found (lane, startStep, endStep, posAtStart), not just the
    // longest, so short stalls are not hidden.
    private static List<(string Lane, int StartStep, int EndStep, double Pos)> FindStoppedRuns(
        List<Row> rowsForOneVehicle, double stoppedThreshold, bool internalOnly)
    {
        var result = new List<(string, int, int, double)>();
        var curLane = string.Empty;
        var curStart = -1;
        var curPos = 0.0;
        var prevStep = int.MinValue;

        void Flush(int endStep)
        {
            if (curStart >= 0)
            {
                result.Add((curLane, curStart, endStep, curPos));
            }

            curStart = -1;
        }

        foreach (var r in rowsForOneVehicle)
        {
            var qualifies = r.Speed < stoppedThreshold && (!internalOnly || (r.LaneId.Length > 0 && r.LaneId[0] == ':'));
            var contiguous = r.Step == prevStep + 1;

            if (qualifies)
            {
                if (curStart >= 0 && contiguous && r.LaneId == curLane)
                {
                    // extend
                }
                else
                {
                    Flush(prevStep);
                    curStart = r.Step;
                    curLane = r.LaneId;
                    curPos = r.Pos;
                }
            }
            else
            {
                Flush(prevStep);
            }

            prevStep = r.Step;
        }

        Flush(prevStep);
        return result;
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
