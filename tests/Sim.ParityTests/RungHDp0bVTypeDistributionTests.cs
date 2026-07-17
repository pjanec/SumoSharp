using System.Text;
using Sim.Core;
using Sim.Harness;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// P0-B (docs/HIGH-DENSITY-P0-DESIGN.md "P0-B"): <vTypeDistribution> resolution, RNG-insensitive
// parity (owner Q1b). Ground truth for the ATTRIBUTE form's actual syntax is the vendored SUMO
// source, sumo/src/microsim/MSRouteHandler.cpp:246-288 (openVehicleTypeDistribution): `vTypes` is
// a whitespace-separated list of PLAIN ids, and a SEPARATE, optional `probabilities` attribute
// carries the parallel weight list (index-aligned; a shorter/absent list defaults each remaining
// entry to weight 1). DemandParser ALSO accepts an "id:weight" colon-embedded token as a harmless
// authoring convenience when no `probabilities` attribute is present. Both are exercised below.
// scenarios/43-vtypedist (already committed, golden regenerated from real SUMO 1.20.0) uses the
// real `vTypes="carA carB" probabilities="0.5 0.5"` form -- its own acceptance test is the last one
// in this file.
public class RungHDp0bVTypeDistributionTests
{
    private const string RouHeader = """
        <routes>
            <vType id="a" vClass="passenger" sigma="0"/>
            <vType id="b" vClass="passenger" sigma="0"/>
            <route id="r0" edges="e0"/>
        """;

    // ----- Parse-level unit tests (offline, DemandParser.ParseXml) -----

    [Fact]
    public void AttributeForm_ColonEmbeddedWeights_NormalisesToDeclaredRatio()
    {
        var demand = DemandParser.ParseXml(RouHeader + """
                <vTypeDistribution id="civ" vTypes="a:0.7 b:0.3"/>
            </routes>
            """);

        var dist = Assert.Single(demand.VTypeDistributions).Value;
        Assert.Equal("civ", dist.Id);
        Assert.Equal(2, dist.Members.Count);
        Assert.Equal("a", dist.Members[0].VTypeId);
        Assert.Equal(0.7, dist.Members[0].Probability, precision: 9);
        Assert.Equal("b", dist.Members[1].VTypeId);
        Assert.Equal(0.3, dist.Members[1].Probability, precision: 9);
    }

    [Fact]
    public void AttributeForm_NoExplicitWeight_DefaultsToUniformSplit()
    {
        var demand = DemandParser.ParseXml(RouHeader + """
                <vTypeDistribution id="civ" vTypes="a b"/>
            </routes>
            """);

        var dist = demand.VTypeDistributions["civ"];
        Assert.Equal(2, dist.Members.Count);
        Assert.Equal(0.5, dist.Members[0].Probability, precision: 9);
        Assert.Equal(0.5, dist.Members[1].Probability, precision: 9);
    }

    // Real SUMO's actual attribute-form syntax (MSRouteHandler.cpp:251-286): vTypes= is a plain id
    // list, probabilities= is the SEPARATE parallel weight list. This is the form
    // scenarios/43-vtypedist's own rou.rou.xml uses.
    [Fact]
    public void AttributeForm_SeparateProbabilitiesAttribute_NormalisesToDeclaredRatio()
    {
        var demand = DemandParser.ParseXml(RouHeader + """
                <vTypeDistribution id="civ" vTypes="a b" probabilities="0.7 0.3"/>
            </routes>
            """);

        var dist = demand.VTypeDistributions["civ"];
        Assert.Equal(2, dist.Members.Count);
        Assert.Equal("a", dist.Members[0].VTypeId);
        Assert.Equal(0.7, dist.Members[0].Probability, precision: 9);
        Assert.Equal("b", dist.Members[1].VTypeId);
        Assert.Equal(0.3, dist.Members[1].Probability, precision: 9);
    }

    [Fact]
    public void NestedVTypeForm_AddsInlineVTypesToVTypeSet_AndNormalisesProbabilityWeights()
    {
        var demand = DemandParser.ParseXml("""
            <routes>
                <route id="r0" edges="e0"/>
                <vTypeDistribution id="civ">
                    <vType id="car" vClass="passenger" sigma="0" probability="0.7"/>
                    <vType id="van" vClass="passenger" sigma="0" probability="0.3"/>
                </vTypeDistribution>
            </routes>
            """);

        var dist = demand.VTypeDistributions["civ"];
        Assert.Equal(2, dist.Members.Count);
        Assert.Equal("car", dist.Members[0].VTypeId);
        Assert.Equal(0.7, dist.Members[0].Probability, precision: 9);
        Assert.Equal("van", dist.Members[1].VTypeId);
        Assert.Equal(0.3, dist.Members[1].Probability, precision: 9);

        // The nested <vType>s are ALSO folded into the normal vType set, so a plain direct lookup
        // by member id resolves (exactly like a top-level <vType>).
        Assert.True(demand.VTypesById.ContainsKey("car"));
        Assert.True(demand.VTypesById.ContainsKey("van"));
    }

    [Fact]
    public void NestedVTypeForm_MemberWithNoProbabilityAttribute_DefaultsToWeightOne()
    {
        var demand = DemandParser.ParseXml("""
            <routes>
                <route id="r0" edges="e0"/>
                <vTypeDistribution id="civ">
                    <vType id="car" vClass="passenger" sigma="0"/>
                    <vType id="van" vClass="passenger" sigma="0"/>
                </vTypeDistribution>
            </routes>
            """);

        var dist = demand.VTypeDistributions["civ"];
        Assert.Equal(0.5, dist.Members[0].Probability, precision: 9);
        Assert.Equal(0.5, dist.Members[1].Probability, precision: 9);
    }

    // ----- Engine-level resolution: statistical sampling + determinism + membership -----

    // 500+ vehicles over a 0.7/0.3 weighted distribution, resolved through Engine's real
    // vehicle-creation path (BuildRuntime -> ResolveEffectiveTypeId), observed via the public
    // Step()/VehicleTypes read surface (Engine.VehicleTypes is populated only by the Step()
    // projection, not by Run()'s TrajectorySet -- see Engine.cs's own VehicleExportSnapshot/
    // VehicleReadBuffer comments). Fully offline: no SUMO golden, different RNG by design
    // (HIGH-DENSITY-P0-DESIGN.md "P0-B" point 4) -- this checks SumoSharp's OWN sampler against
    // its OWN declared weights.
    private const int VehicleCount = 520;
    private const double Spacing = 15.0; // >> heavy/light length(4)+minGap(1), so every vehicle
                                          // gets a conflict-free depart position on the one lane.

    [Fact]
    public void WeightedDistribution_500Plus_ResolvedFrequencyMatchesDeclaredWeightsWithinTolerance()
    {
        var (netXml, rouXml, cfgXml) = BuildWeightedFixture();
        var dir = Directory.CreateTempSubdirectory("p0b-vtypedist-stat-");
        try
        {
            var (netPath, rouPath, cfgPath) = WriteFixture(dir.FullName, netXml, rouXml, cfgXml);

            var engine = new Engine { Seed = 42 };
            engine.LoadScenario(netPath, rouPath, cfgPath);

            var resolvedTypeById = CollectResolvedTypes(engine, VehicleCount);

            Assert.Equal(VehicleCount, resolvedTypeById.Count);

            var heavyCount = resolvedTypeById.Values.Count(t => t == "heavy");
            var lightCount = resolvedTypeById.Values.Count(t => t == "light");
            Assert.Equal(VehicleCount, heavyCount + lightCount);

            // Every vehicle resolved to AN ACTUAL MEMBER -- never left as the distribution id
            // "wdist", never crashed.
            Assert.All(resolvedTypeById.Values, t => Assert.True(t is "heavy" or "light", $"unexpected resolved type '{t}'"));

            var heavyFrac = heavyCount / (double)VehicleCount;
            Assert.InRange(heavyFrac, 0.7 - 0.05, 0.7 + 0.05);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // Determinism/repeatability (CLAUDE.md's per-entity-seeded-RNG rule): the SAME vehicle id draws
    // the SAME member every time, for the SAME engine seed -- a pure function of (Seed,
    // EntityIndex, distribution), independent of thread/scheduling order.
    [Fact]
    public void WeightedDistribution_SameSeed_SameVehicleId_AlwaysDrawsSameMember()
    {
        var (netXml, rouXml, cfgXml) = BuildWeightedFixture();
        var dir = Directory.CreateTempSubdirectory("p0b-vtypedist-determinism-");
        try
        {
            var (netPath, rouPath, cfgPath) = WriteFixture(dir.FullName, netXml, rouXml, cfgXml);

            var engineA = new Engine { Seed = 7 };
            engineA.LoadScenario(netPath, rouPath, cfgPath);
            var resolvedA = CollectResolvedTypes(engineA, VehicleCount);

            var engineB = new Engine { Seed = 7 };
            engineB.LoadScenario(netPath, rouPath, cfgPath);
            var resolvedB = CollectResolvedTypes(engineB, VehicleCount);

            Assert.Equal(VehicleCount, resolvedA.Count);
            Assert.Equal(VehicleCount, resolvedB.Count);
            foreach (var (id, typeA) in resolvedA)
            {
                Assert.True(resolvedB.TryGetValue(id, out var typeB), $"vehicle '{id}' missing from second run");
                Assert.Equal(typeA, typeB);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // A PLAIN (non-distribution) type= is completely untouched -- the direct _vTypesById lookup
    // path, byte-identical to every pre-P0-B scenario. Regression guard for ResolveEffectiveTypeId's
    // fast path.
    [Fact]
    public void PlainVTypeId_NotADistribution_ResolvesUnchanged()
    {
        var dir = Directory.CreateTempSubdirectory("p0b-vtypedist-plain-");
        try
        {
            var netPath = Path.Combine(dir.FullName, "net.net.xml");
            var rouPath = Path.Combine(dir.FullName, "rou.rou.xml");
            var cfgPath = Path.Combine(dir.FullName, "config.sumocfg");

            File.WriteAllText(netPath, BuildSingleLaneNetXml(1000.0));
            File.WriteAllText(rouPath, """
                <routes>
                    <vType id="car" vClass="passenger" sigma="0"/>
                    <route id="r0" edges="e0"/>
                    <vehicle id="v0" type="car" route="r0" depart="0" departPos="0" departSpeed="0" departLane="0"/>
                </routes>
                """);
            File.WriteAllText(cfgPath, BuildConfigXml(end: 10));

            var engine = new Engine();
            engine.LoadScenario(netPath, rouPath, cfgPath);
            engine.Step();

            Assert.Equal(1, engine.VehicleCount);
            Assert.Equal("v0", engine.VehicleIds[0]);
            Assert.Equal("car", engine.VehicleTypes[0]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ----- scenarios/43-vtypedist acceptance (already-committed golden, regenerated from SUMO
    // 1.20.0; see its own rou.rou.xml header comment) -----

    private static readonly string Scenario43Dir = Path.Combine(RepoRoot(), "scenarios", "43-vtypedist");

    [Fact]
    public void Scenario43_MatchesGoldenFcdWithinTolerance()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(Scenario43Dir, "net.net.xml"),
            Path.Combine(Scenario43Dir, "rou.rou.xml"),
            Path.Combine(Scenario43Dir, "config.sumocfg"));

        var actual = engine.Run(120);
        var golden = FcdParser.Parse(Path.Combine(Scenario43Dir, "golden.fcd.xml"));
        var tolerance = ToleranceConfig.Load(Path.Combine(Scenario43Dir, "tolerance.json"));

        var result = TrajectoryComparator.Compare(actual, golden, tolerance);

        Assert.True(result.IsMatch,
            $"scenario 43 vtypedist parity FAILED. FirstDivergenceStep={result.FirstDivergenceStep?.ToString() ?? "none"}");
    }

    // Functional check the tolerance-based comparator above does NOT make (it deliberately never
    // compares FCD type=, per the design's "RNG-insensitive parity" trick): every one of the 4
    // vehicles actually resolved to a concrete member of "civ" (carA/carB), never left unresolved.
    [Fact]
    public void Scenario43_EveryVehicleResolvesToADeclaredDistributionMember()
    {
        var engine = new Engine();
        engine.LoadScenario(
            Path.Combine(Scenario43Dir, "net.net.xml"),
            Path.Combine(Scenario43Dir, "rou.rou.xml"),
            Path.Combine(Scenario43Dir, "config.sumocfg"));

        var resolved = CollectResolvedTypes(engine, expectedCount: 4);

        Assert.Equal(4, resolved.Count);
        Assert.All(resolved.Values, t => Assert.True(t is "carA" or "carB", $"unexpected resolved type '{t}'"));
    }

    // ----- shared fixture helpers -----

    private static (string NetXml, string RouXml, string CfgXml) BuildWeightedFixture()
    {
        var netXml = BuildSingleLaneNetXml(VehicleCount * Spacing + 500.0);

        var rou = new StringBuilder();
        rou.AppendLine("<routes>");
        rou.AppendLine("""    <vType id="heavy" vClass="passenger" sigma="0" length="4.0" minGap="1.0"/>""");
        rou.AppendLine("""    <vType id="light" vClass="passenger" sigma="0" length="4.0" minGap="1.0"/>""");
        rou.AppendLine("""    <vTypeDistribution id="wdist" vTypes="heavy light" probabilities="0.7 0.3"/>""");
        rou.AppendLine("""    <route id="r0" edges="e0"/>""");
        for (var i = 0; i < VehicleCount; i++)
        {
            var pos = (i + 1) * Spacing;
            rou.AppendLine(
                $"""    <vehicle id="v{i}" type="wdist" route="r0" depart="0" departPos="{pos.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}" departSpeed="0" departLane="0"/>""");
        }

        rou.AppendLine("</routes>");

        return (netXml, rou.ToString(), BuildConfigXml(end: 30));
    }

    private static (string NetPath, string RouPath, string CfgPath) WriteFixture(
        string dir, string netXml, string rouXml, string cfgXml)
    {
        var netPath = Path.Combine(dir, "net.net.xml");
        var rouPath = Path.Combine(dir, "rou.rou.xml");
        var cfgPath = Path.Combine(dir, "config.sumocfg");
        File.WriteAllText(netPath, netXml);
        File.WriteAllText(rouPath, rouXml);
        File.WriteAllText(cfgPath, cfgXml);
        return (netPath, rouPath, cfgPath);
    }

    private static string BuildSingleLaneNetXml(double laneLength)
    {
        var len = laneLength.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        return $"""
            <net version="1.20">
                <location netOffset="0.00,0.00" convBoundary="0.00,0.00,{len},0.00" origBoundary="0.00,0.00,{len},0.00" projParameter="!"/>
                <edge id="e0" from="n0" to="n1" priority="-1">
                    <lane id="e0_0" index="0" speed="50.00" length="{len}" shape="0.00,-1.60 {len},-1.60"/>
                </edge>
                <junction id="n0" type="dead_end" x="0.00" y="0.00" incLanes="" intLanes="" shape="0.00,0.00 0.00,-3.20"/>
                <junction id="n1" type="dead_end" x="{len}" y="0.00" incLanes="e0_0" intLanes="" shape="{len},-3.20 {len},0.00"/>
            </net>
            """;
    }

    private static string BuildConfigXml(int end) => $"""
        <configuration>
            <time>
                <begin value="0"/>
                <end value="{end}"/>
                <step-length value="1"/>
            </time>
            <processing>
                <step-method.ballistic value="false"/>
                <time-to-teleport value="-1"/>
                <default.action-step-length value="1"/>
                <default.speeddev value="0"/>
            </processing>
            <random_number>
                <seed value="42"/>
            </random_number>
        </configuration>
        """;

    // Steps the engine until every one of `expectedCount` vehicles has been observed at least once
    // via the Step()/VehicleTypes read surface (or a generous step budget is exhausted), recording
    // each vehicle's FIRST observed resolved type -- BuildRuntime resolves a vehicle's vType exactly
    // once, at creation, so the first observation already carries the final answer.
    private static Dictionary<string, string> CollectResolvedTypes(Engine engine, int expectedCount)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var step = 0; step < 200 && resolved.Count < expectedCount; step++)
        {
            engine.Step();
            for (var i = 0; i < engine.VehicleCount; i++)
            {
                var id = engine.VehicleIds[i];
                if (!resolved.ContainsKey(id))
                {
                    resolved[id] = engine.VehicleTypes[i];
                }
            }
        }

        return resolved;
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
