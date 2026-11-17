using System;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// H17 (HROT road convoy) -- a convoy's ESCORT handed to SUMO with its beacon and siren on: SpawnExternalVehicle(..., priority: true) spawns the
// vType's PRIORITY TWIN (Engine.PriorityTwin: blue light, red ignored, approaching junction foes ignored -- all features this engine already
// ports), and SetExternalVehicleMaxSpeed caps / restores its top speed. Purely additive: nothing existing calls either; every golden is untouched.
public class HrotConvoyEscortTests
{
    private readonly ITestOutputHelper _out;
    public HrotConvoyEscortTests(ITestOutputHelper output) => _out = output;

    // v1 (x 100..200) -> a traffic light at B that is ALWAYS RED -> v2 (x 200..400), all at y 100.
    private const string RedLightNetXml = """
        <net>
          <edge id=":B_0" function="internal"><lane id=":B_0_0" index="0" speed="13.9" length="0.10" shape="200.00,100.00 200.10,100.00"/></edge>
          <edge id="v1" from="A" to="B"><lane id="v1_0" index="0" speed="13.9" length="100.0" shape="100.00,100.00 200.00,100.00"/></edge>
          <edge id="v2" from="B" to="C"><lane id="v2_0" index="0" speed="13.9" length="199.9" shape="200.10,100.00 400.00,100.00"/></edge>
          <tlLogic id="B" type="static" programID="0" offset="0"><phase duration="99999" state="r"/></tlLogic>
          <junction id="A" type="dead_end" x="100.00" y="100.00" incLanes="" intLanes="" shape=""/>
          <junction id="B" type="traffic_light" x="200.00" y="100.00" incLanes="v1_0" intLanes=":B_0_0" shape=""/>
          <junction id="C" type="dead_end" x="400.00" y="100.00" incLanes="v2_0" intLanes="" shape=""/>
          <connection from="v1" to="v2" fromLane="0" toLane="0" via=":B_0_0" tl="B" linkIndex="0" dir="s" state="O"/>
          <connection from=":B_0" to="v2" fromLane="0" toLane="0" dir="s" state="M"/>
        </net>
        """;

    // one straight 1 km edge, no lights.
    private const string LongNetXml = """
        <net>
          <edge id="u1" from="A" to="B"><lane id="u1_0" index="0" speed="13.9" length="1000.0" shape="100.00,100.00 1100.00,100.00"/></edge>
        </net>
        """;

    private static void WithSim(string netXml, Action<LiveCitySim> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "livecity-h17-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "net.xml"), netXml);
        var cfg = LiveCityConfig.ForDataset(dir);
        cfg.X0 = 0; cfg.Y0 = 0; cfg.X1 = 1200; cfg.Y1 = 500;
        cfg.CarTargetConcurrent = 0;
        try
        {
            using var sim = new LiveCitySim(cfg);
            body(sim);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static double? XOf(LiveCitySim sim, string id)
    {
        foreach (var car in sim.SampleCars())
            if (car.Name == id) return car.X;
        return null;
    }

    [Fact]
    public void H17_AnOrdinaryShadowStopsAtTheRed_ThePriorityTwinRunsIt()
    {
        foreach (var priority in new[] { false, true })
        {
            WithSim(RedLightNetXml, sim =>
            {
                Assert.True(sim.SpawnExternalVehicle("hrot:1", 120.0, 100.0, 0.0, 5.0, 350.0, 100.0, vTypeId: null, priority: priority));
                double maxX = 0.0;
                for (var i = 0; i < 300 && !sim.ExternalVehicleArrived("hrot:1"); i++)
                {
                    sim.Step();
                    if (XOf(sim, "hrot:1") is double x) maxX = Math.Max(maxX, x);
                }

                _out.WriteLine($"priority={priority}: furthest x {maxX:F1}, arrived {sim.ExternalVehicleArrived("hrot:1")}");
                if (priority)
                {
                    Assert.True(sim.ExternalVehicleArrived("hrot:1"), $"the priority twin did not run the red (furthest x {maxX:F1})");
                }
                else
                {
                    Assert.False(sim.ExternalVehicleArrived("hrot:1"));
                    Assert.True(maxX <= 200.0 + 1e-6, $"an ordinary shadow crossed a red (x {maxX:F1})");
                }
            });
        }
    }

    [Fact]
    public void H17_APriorityTwinBehindACarHeldAtTheRed_WaitsBehindIt_AndNeverPassesIt()
    {
        WithSim(RedLightNetXml, sim =>
        {
            Assert.True(sim.SpawnExternalVehicle("hrot:front", 170.0, 100.0, 0.0, 5.0, 350.0, 100.0));                        // an ordinary car, near the line
            Assert.True(sim.SpawnExternalVehicle("hrot:escort", 120.0, 100.0, 0.0, 5.0, 350.0, 100.0, vTypeId: null, priority: true));
            for (var i = 0; i < 200; i++)
            {
                sim.Step();
                double? f = XOf(sim, "hrot:front"), e = XOf(sim, "hrot:escort");
                if (f is double fx && e is double ex)
                {
                    Assert.True(ex < fx, $"step {i}: the escort ({ex:F1}) passed the car held at the red ({fx:F1}) on a single lane");
                    Assert.True(fx <= 200.0 + 1e-6, $"step {i}: the car ahead crossed the red ({fx:F1})");
                }
            }

            Assert.False(sim.ExternalVehicleArrived("hrot:escort"));   // it waits: the queue holds it until green
        });
    }

    [Fact]
    public void H17_TheMaxSpeedCap_SlowsTheShadow_AndNullRestoresItExactly()
    {
        WithSim(LongNetXml, sim =>
        {
            Assert.True(sim.SpawnExternalVehicle("hrot:9", 120.0, 100.0, 0.0, 13.0, 1000.0, 100.0, vTypeId: null, priority: true));
            double Speed()
            {
                double x0 = XOf(sim, "hrot:9")!.Value, t0 = sim.Time;
                sim.Step();
                return (XOf(sim, "hrot:9")!.Value - x0) / (sim.Time - t0);
            }

            for (var i = 0; i < 10; i++) sim.Step();
            double free = Speed();
            Assert.True(free > 10.0, $"anti-vacuity: it drives at the lane's speed ({free:F2})");

            Assert.True(sim.SetExternalVehicleMaxSpeed("hrot:9", 3.0));
            for (var i = 0; i < 20; i++) sim.Step();
            double capped = Speed();
            Assert.True(capped <= 3.0 + 1e-6, $"capped speed {capped:F2}");

            Assert.True(sim.SetExternalVehicleMaxSpeed("hrot:9", null));
            for (var i = 0; i < 20; i++) sim.Step();
            double restored = Speed();
            _out.WriteLine($"free {free:F2} · capped {capped:F2} · restored {restored:F2}");
            Assert.Equal(free, restored, 3);                                  // the speed it had before the cap (positions are floats)

            Assert.True(sim.SetExternalVehicleMaxSpeed("hrot:9", null));      // never capped since: nothing to restore, still true
            Assert.False(sim.SetExternalVehicleMaxSpeed("nope", 3.0));        // not a live external vehicle
        });
    }

    [Fact]
    public void H17_PriorityFalse_IsExactlyTheOldCall()
    {
        double[] Run(bool viaNewOverload)
        {
            double[] xs = new double[60];
            WithSim(RedLightNetXml, sim =>
            {
                Assert.True(viaNewOverload
                    ? sim.SpawnExternalVehicle("hrot:3", 120.0, 100.0, 0.0, 5.0, 350.0, 100.0, vTypeId: null, priority: false)
                    : sim.SpawnExternalVehicle("hrot:3", 120.0, 100.0, 0.0, 5.0, 350.0, 100.0));
                for (var i = 0; i < xs.Length; i++) { sim.Step(); xs[i] = XOf(sim, "hrot:3") ?? double.NaN; }
            });
            return xs;
        }

        Assert.Equal(Run(false), Run(true));
    }
}
