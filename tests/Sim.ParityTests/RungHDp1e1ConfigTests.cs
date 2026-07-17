using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// P1E-1 (HIGH-DENSITY-P1E-DESIGN.md §7/§9) -- config-only, additive infrastructure for the
// periodic congestion-reactive reroute device (MSDevice_Routing). Nothing in the running engine
// reads these fields yet (that is P1E-4); this test only validates the parser round-trips the six
// new keys and that every pre-P1E-1 scenario (which omits them all) gets the documented inert
// defaults, byte-identical to before.
public class RungHDp1e1ConfigTests
{
    [Fact]
    public void RerouteKeys_ParseFromProcessingSection()
    {
        const string cfg = """
            <configuration>
                <time><begin value="0"/><end value="10"/><step-length value="1"/></time>
                <processing>
                    <device.rerouting.probability value="1.0"/>
                    <device.rerouting.period value="30"/>
                    <device.rerouting.adaptation-steps value="18"/>
                    <device.rerouting.adaptation-interval value="2.0"/>
                    <routing-algorithm value="astar"/>
                    <device.rerouting.jitter value="true"/>
                </processing>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(cfg);

        Assert.Equal(1.0, config.RerouteProbability, precision: 6);
        Assert.Equal(30.0, config.ReroutePeriod, precision: 6);
        Assert.Equal(18, config.RerouteAdaptationSteps);
        Assert.Equal(2.0, config.RerouteAdaptationInterval, precision: 6);
        Assert.Equal("astar", config.RoutingAlgorithm);
        Assert.True(config.RerouteJitter);
    }

    [Fact]
    public void RerouteKeys_AbsentSection_YieldsInertDefaults()
    {
        // Every pre-P1E-1 scenario omits the whole device.rerouting.* / routing-algorithm family
        // (and may or may not have a <processing> section at all for unrelated keys) -- the
        // defaults below must make rerouting a complete no-op, matching SUMO's own defaults
        // (probability=0 => nothing equipped; period=0 => never fires) plus our two SUMO-default
        // carries (adaptation-steps=180, adaptation-interval=1) and our own non-SUMO jitter flag
        // (default off).
        const string cfg = """
            <configuration>
                <time><begin value="0"/><end value="10"/><step-length value="1"/></time>
                <processing><time-to-teleport value="-1"/></processing>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(cfg);

        Assert.Equal(0.0, config.RerouteProbability);
        Assert.Equal(0.0, config.ReroutePeriod);
        Assert.Equal(180, config.RerouteAdaptationSteps);
        Assert.Equal(1.0, config.RerouteAdaptationInterval);
        Assert.Equal("dijkstra", config.RoutingAlgorithm);
        Assert.False(config.RerouteJitter);
    }

    [Fact]
    public void RerouteKeys_AbsentProcessingElementEntirely_YieldsInertDefaults()
    {
        // No <processing> element at all (a minimal sumocfg) -- must not throw, and must yield
        // the exact same inert defaults as the "present-but-empty" case above.
        const string cfg = """
            <configuration>
                <time><begin value="0"/><end value="10"/><step-length value="1"/></time>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(cfg);

        Assert.Equal(0.0, config.RerouteProbability);
        Assert.Equal(0.0, config.ReroutePeriod);
        Assert.Equal(180, config.RerouteAdaptationSteps);
        Assert.Equal(1.0, config.RerouteAdaptationInterval);
        Assert.Equal("dijkstra", config.RoutingAlgorithm);
        Assert.False(config.RerouteJitter);
    }
}
