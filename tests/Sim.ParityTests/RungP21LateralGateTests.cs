using Sim.Core;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// Phase 2 / P2.1 (lateral state + config gate -- pure inert infrastructure, no engine behavior
// yet). Two additive pieces: (1) ScenarioConfig.LateralResolution parsed from the sumocfg's
// <processing><lateral-resolution> -- SUMO's global sublane master switch (MSGlobals::
// gLateralResolution), default 0 = OFF for every phase-1 scenario; (2) Kinematics.LatSpeed, the
// additive lateral-velocity field, 0 for every lane-centred vehicle. These are the gate the
// downstream sublane rungs (P2.2+) branch on; on their own they change no trajectory, so the
// committed parity suite + determinism hash stay byte-identical.
public class RungP21LateralGateTests
{
    [Fact]
    public void LateralResolution_ParsesFromSumocfg()
    {
        const string cfg = """
            <configuration>
                <time><begin value="0"/><end value="10"/><step-length value="1"/></time>
                <processing><lateral-resolution value="0.8"/></processing>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(cfg);

        Assert.Equal(0.8, config.LateralResolution, precision: 6);
    }

    [Fact]
    public void LateralResolution_DefaultsToZero_WhenAbsent()
    {
        // Every phase-1 sumocfg omits <lateral-resolution> => 0 => sublane model OFF => the engine's
        // lateral state stays lane-centred. This default is the byte-identity guarantee.
        const string cfg = """
            <configuration>
                <time><begin value="0"/><end value="10"/><step-length value="1"/></time>
                <processing><time-to-teleport value="-1"/></processing>
            </configuration>
            """;

        var config = ScenarioConfigParser.ParseXml(cfg);

        Assert.Equal(0.0, config.LateralResolution);
    }

    [Fact]
    public void Kinematics_LatSpeed_DefaultsToZero()
    {
        // The additive lateral-velocity field: a default-constructed Kinematics (every lane-centred
        // vehicle) has LatSpeed 0, so nothing lateral moves until the sublane model drives it.
        var kin = default(Kinematics);

        Assert.Equal(0.0, kin.LatSpeed);
        Assert.Equal(0.0, kin.LatOffset);
    }
}
