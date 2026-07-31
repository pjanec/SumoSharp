using System.Xml;
using Sim.Sumo;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// URGENT-STRATEGIC-FOLLOW T3.3 (docs/URGENT-STRATEGIC-FOLLOW-DESIGN.md §5; journal Entries 24-30):
// pins the mechanism's showcase behaviour so the default cannot silently regress.
//
// THE BEHAVIOUR. On junction-realism-L2-light the left-turner `f_left_W00.0` departs on lane 0 of
// `in_W00` and must reach lane 1 to take its left turn. The target lane's leader cruises at the same
// max speed, so a gap never opens by itself; SUMO's `MSLCM_LC2013::informLeader` brakes ego to slot
// in BEHIND that leader and changes at t=3 / pos 30.94 / 11.95 m/s -- 158 m before the junction, at
// speed. Without the coupling this engine changes at t=45 / pos 189.60 / 1.00 m/s -- at the stop
// line, standing: the owner's "lateral lane change while standing" artefact, strategic form.
//
// THE TEST. Two shim runs of the same scenario differing ONLY in SUMOSHARP_URGENTFOLLOW:
//   - the SHIPPED DEFAULT (variable unset => Engine.UrgentStrategicLeaderFollow's default): the
//     change must happen EARLY (front bumper < 100 m into the 189.6 m lane) and AT SPEED (> 5 m/s);
//   - forced OFF ("0"): the change happens late and slow -- the VACUITY GUARD. If both arms behaved
//     the same, the assertion above could not fail for the reason it exists, so fail loudly instead.
//     This is also exactly the arm that fails if the shipped default is ever silently flipped back.
//
// Shim-driven (SumoShim.Run, same in-process CLI path the other behavioural tests use), so it joins
// SumoShimEnvCollection -- SumoShim reads process-global env vars, and cross-class parallelism has
// already produced one false RED this workstream (see IgnoreJunctionBlockerTests' header).
[Collection(SumoShimEnvCollection.Name)]
public class UrgentStrategicFollowBehaviourTests
{
    private readonly ITestOutputHelper _out;

    public UrgentStrategicFollowBehaviourTests(ITestOutputHelper output) => _out = output;

    private const string Vehicle = "f_left_W00.0";
    private const string DepartLane = "in_W00_0";
    private const string TargetLane = "in_W00_1";

    [Fact]
    public void LeftTurner_ChangesLanesAtSpeed_NotAtTheStopLine()
    {
        var cfg = Path.Combine(
            RepoRoot(), "scenarios", "_diag", "junction-realism-L2-light", "config.sumocfg");
        Assert.True(File.Exists(cfg), $"scenario missing: {cfg}");

        var prevGates = JunctionGateEnv.PinToEngineDefaults();
        var prevFlag = Environment.GetEnvironmentVariable("SUMOSHARP_URGENTFOLLOW");
        try
        {
            var shipped = FirstStepOnTargetLane(cfg, flag: null);
            var forcedOff = FirstStepOnTargetLane(cfg, flag: "0");

            _out.WriteLine($"shipped default: change at t={shipped.Time} pos={shipped.Pos:F2} speed={shipped.Speed:F2}");
            _out.WriteLine($"forced off:      change at t={forcedOff.Time} pos={forcedOff.Pos:F2} speed={forcedOff.Speed:F2}");

            // VACUITY GUARD: the forced-off arm must still show the artefact (a late, slow change).
            // If it ever changes early/at speed too, the flag no longer drives this behaviour and the
            // assertion below asserts nothing -- fail loudly rather than leave a test that cannot fail.
            Assert.True(
                forcedOff.Pos > 150.0 && forcedOff.Speed < 2.0,
                $"the forced-OFF arm changed at pos {forcedOff.Pos:F2} / {forcedOff.Speed:F2} m/s -- no "
                + "longer a late, standing change, so this A/B no longer discriminates "
                + "UrgentStrategicLeaderFollow and the shipped-default assertion is vacuous. Re-anchor "
                + "the scenario (see the header) rather than weakening the assertion.");

            Assert.True(
                shipped.Pos < 100.0 && shipped.Speed > 5.0,
                $"under the SHIPPED default the left-turner changed to {TargetLane} at pos "
                + $"{shipped.Pos:F2} m / {shipped.Speed:F2} m/s -- the informLeader coupling "
                + "(Engine.UrgentStrategicLeaderFollow, default ON since journal Entry 30) should have "
                + "let it change at speed ~158 m before the junction (SUMO: t=3, pos 30.94, 11.95 m/s). "
                + "A late, standing change is the owner's 'lateral lane change while stopped' artefact "
                + "come back -- see docs/URGENT-STRATEGIC-FOLLOW-DESIGN.md.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUMOSHARP_URGENTFOLLOW", prevFlag);
            JunctionGateEnv.Restore(prevGates);
        }
    }

    // Runs the shim (flag == null -> variable removed -> the shipped engine default) and returns the
    // first FCD sample of the left-turner on the TARGET lane. Fails the test if it never gets there
    // (it must -- its route needs lane 1 for the left turn).
    private (double Time, double Pos, double Speed) FirstStepOnTargetLane(string cfg, string? flag)
    {
        Environment.SetEnvironmentVariable("SUMOSHARP_URGENTFOLLOW", flag);

        var outDir = Path.Combine(Path.GetTempPath(), "sumosharp-urgentfollow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var fcd = Path.Combine(outDir, "out.fcd.xml");
            var exit = SumoShim.Run(
                new[] { "-c", cfg, "--fcd-output", fcd, "--end", "60", "--no-step-log", "true" },
                new StringWriter(), new StringWriter());
            Assert.Equal(0, exit);

            double time = -1.0;
            using var reader = XmlReader.Create(fcd);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "timestep")
                {
                    time = double.Parse(reader.GetAttribute("time")!, System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (reader.Name == "vehicle" && reader.GetAttribute("id") == Vehicle
                         && reader.GetAttribute("lane") == TargetLane)
                {
                    return (time,
                        double.Parse(reader.GetAttribute("pos")!, System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(reader.GetAttribute("speed")!, System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            throw new Xunit.Sdk.XunitException(
                $"{Vehicle} never appeared on {TargetLane} within the 60 s horizon -- it departs on "
                + $"{DepartLane} and its left-turn route REQUIRES lane 1, so either the scenario or the "
                + "strategic lane-change path is broken.");
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
