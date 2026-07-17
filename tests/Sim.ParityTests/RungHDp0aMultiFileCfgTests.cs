using Sim.Ingest;

namespace Sim.ParityTests;

// P0-A (docs/HIGH-DENSITY-P0-DESIGN.md "P0-A"): multi-file .sumocfg <input> support --
// ScenarioConfigParser reading <net-file>/<route-files>/<additional-files>, and DemandParser
// merging multiple route-files into one DemandModel. These are pure-parser unit tests (no
// SUMO, no scenario dir, no golden) -- offline-only, per the task's done-condition.
public class RungHDp0aMultiFileCfgTests
{
    [Fact]
    public void ScenarioConfigParser_ParsesInputSection_CommaSeparatedLists()
    {
        var xml = """
            <configuration>
              <input>
                <net-file value="net.net.xml"/>
                <route-files value="vtypes.rou.xml,demand.rou.xml"/>
                <additional-files value="extra.add.xml"/>
              </input>
              <time>
                <begin value="0"/>
                <end value="10"/>
              </time>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(xml);

        Assert.Equal("net.net.xml", config.NetFile);
        Assert.Equal(new[] { "vtypes.rou.xml", "demand.rou.xml" }, config.RouteFiles);
        Assert.Equal(new[] { "extra.add.xml" }, config.AdditionalFiles);
        // Existing <time> parsing must still work alongside the new <input> parsing.
        Assert.Equal(0.0, config.Begin);
        Assert.Equal(10.0, config.End);
    }

    [Fact]
    public void ScenarioConfigParser_ParsesInputSection_SpaceSeparatedRouteFiles()
    {
        var xml = """
            <configuration>
              <input>
                <net-file value="net.net.xml"/>
                <route-files value="vtypes.rou.xml demand.rou.xml"/>
              </input>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(xml);

        Assert.Equal(new[] { "vtypes.rou.xml", "demand.rou.xml" }, config.RouteFiles);
        Assert.Empty(config.AdditionalFiles);
    }

    [Fact]
    public void ScenarioConfigParser_NoInputSection_LeavesNewFieldsAtUnchangedDefaults()
    {
        // Every pre-P0-A scenario's .sumocfg omits <input> entirely (net/rou are passed
        // separately to the 3-arg LoadScenario overload). Confirms that shape stays byte-
        // identical: NetFile null, RouteFiles/AdditionalFiles empty (never null to readers).
        var xml = """
            <configuration>
              <time>
                <begin value="0"/>
                <end value="10"/>
              </time>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(xml);

        Assert.Null(config.NetFile);
        Assert.Empty(config.RouteFiles);
        Assert.Empty(config.AdditionalFiles);
    }

    [Fact]
    public void DemandParser_MultiFile_MergesVTypeFromOneFileAndVehicleFromAnother()
    {
        var dir = Directory.CreateTempSubdirectory("p0a-demandparser-");
        try
        {
            var vTypesPath = Path.Combine(dir.FullName, "vtypes.rou.xml");
            var demandPath = Path.Combine(dir.FullName, "demand.rou.xml");

            File.WriteAllText(vTypesPath, """
                <routes>
                  <vType id="car" maxSpeed="20" accel="2.0" decel="4.0"/>
                </routes>
                """);
            File.WriteAllText(demandPath, """
                <routes>
                  <route id="r0" edges="e0 e1"/>
                  <vehicle id="v0" type="car" route="r0" depart="0"/>
                </routes>
                """);

            var merged = DemandParser.Parse(new[] { vTypesPath, demandPath });

            // vType came from fileA, vehicle+route from fileB -- both landed in ONE DemandModel.
            var vType = Assert.Single(merged.VTypes);
            Assert.Equal("car", vType.Id);
            Assert.Equal(20.0, vType.MaxSpeed);
            var route = Assert.Single(merged.Routes);
            Assert.Equal("r0", route.Id);
            var vehicle = Assert.Single(merged.Vehicles);
            Assert.Equal("v0", vehicle.Id);
            // The vehicle's type= resolves against the merged vTypesById (the whole point of the
            // merge -- fileB's vehicle references fileA's vType).
            Assert.True(merged.VTypesById.ContainsKey(vehicle.TypeId));
            Assert.Same(vType, merged.VTypesById[vehicle.TypeId]);

            // Equivalence check: parsing the two files' content combined into ONE document
            // produces the same demand model shape as parsing them as two separate files.
            var combinedXml = """
                <routes>
                  <vType id="car" maxSpeed="20" accel="2.0" decel="4.0"/>
                  <route id="r0" edges="e0 e1"/>
                  <vehicle id="v0" type="car" route="r0" depart="0"/>
                </routes>
                """;
            var combined = DemandParser.ParseXml(combinedXml);

            Assert.Equal(combined.VTypes.Count, merged.VTypes.Count);
            Assert.Equal(combined.Routes.Count, merged.Routes.Count);
            Assert.Equal(combined.Vehicles.Count, merged.Vehicles.Count);
            Assert.Equal(combined.Vehicles[0].TypeId, merged.Vehicles[0].TypeId);
            Assert.Equal(combined.Vehicles[0].RouteId, merged.Vehicles[0].RouteId);
            Assert.Equal(combined.VTypesById["car"].MaxSpeed, merged.VTypesById["car"].MaxSpeed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DemandParser_Parse_SingleFileOverload_StillDelegatesToMultiFilePath()
    {
        var dir = Directory.CreateTempSubdirectory("p0a-demandparser-single-");
        try
        {
            var path = Path.Combine(dir.FullName, "demand.rou.xml");
            File.WriteAllText(path, """
                <routes>
                  <vType id="car" maxSpeed="20"/>
                  <route id="r0" edges="e0 e1"/>
                  <vehicle id="v0" type="car" route="r0" depart="0"/>
                </routes>
                """);

            var viaSinglePathOverload = DemandParser.Parse(path);
            var viaMultiFileOverload = DemandParser.Parse(new[] { path });

            Assert.Equal(viaSinglePathOverload.Vehicles.Count, viaMultiFileOverload.Vehicles.Count);
            Assert.Equal(viaSinglePathOverload.VTypes.Count, viaMultiFileOverload.VTypes.Count);
            Assert.Equal(viaSinglePathOverload.Vehicles[0].Id, viaMultiFileOverload.Vehicles[0].Id);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DemandParser_DuplicateVTypeIdAcrossFiles_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("p0a-demandparser-dup-");
        try
        {
            var fileA = Path.Combine(dir.FullName, "a.rou.xml");
            var fileB = Path.Combine(dir.FullName, "b.rou.xml");
            File.WriteAllText(fileA, """
                <routes>
                  <vType id="car" maxSpeed="20"/>
                </routes>
                """);
            File.WriteAllText(fileB, """
                <routes>
                  <vType id="car" maxSpeed="30"/>
                </routes>
                """);

            var ex = Assert.Throws<InvalidDataException>(
                () => DemandParser.Parse(new[] { fileA, fileB }));
            Assert.Contains("car", ex.Message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DemandParser_DuplicateRouteIdAcrossFiles_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("p0a-demandparser-dup-route-");
        try
        {
            var fileA = Path.Combine(dir.FullName, "a.rou.xml");
            var fileB = Path.Combine(dir.FullName, "b.rou.xml");
            File.WriteAllText(fileA, """
                <routes>
                  <route id="r0" edges="e0 e1"/>
                </routes>
                """);
            File.WriteAllText(fileB, """
                <routes>
                  <route id="r0" edges="e2 e3"/>
                </routes>
                """);

            var ex = Assert.Throws<InvalidDataException>(
                () => DemandParser.Parse(new[] { fileA, fileB }));
            Assert.Contains("r0", ex.Message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
