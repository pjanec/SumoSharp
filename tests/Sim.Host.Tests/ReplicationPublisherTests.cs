using System.IO;
using Sim.Core;
using Sim.Ingest;
using Sim.Replication;
using Xunit;

namespace Sim.Host.Tests;

// T0.1 success condition 2 (docs/DEMO-CITY3D-TASKS.md): drive a real Engine on scenarios/09-traffic-light
// for ~30 steps, publish each step to an InMemoryReplicationBus via ReplicationPublisher, pump the
// source, and assert: (a) published geometry lane count == network lane count; (b) every stepped vehicle
// appears with monotonic non-decreasing Pos on a given lane; (c) at least one TlEntry with a non-'g'/'G'
// state is observed at some step (lights change).
public class ReplicationPublisherTests
{
    private static readonly string ScenarioDir = Path.Combine(RepoRoot(), "scenarios", "09-traffic-light");

    [Fact]
    public void PublishStep_OverATrafficLightScenario_MeetsAllThreeConditions()
    {
        var netPath = Path.Combine(ScenarioDir, "net.net.xml");
        var rouPath = Path.Combine(ScenarioDir, "rou.rou.xml");
        var cfgPath = Path.Combine(ScenarioDir, "config.sumocfg");

        // NetworkModel is parsed independently of the stepping Engine (mirrors EngineHost's own pattern:
        // Network = NetworkParser.Parse(netPath) alongside a separately-loaded Engine) -- ReplicationPublisher
        // takes a NetworkModel, not an Engine, so any host can supply its own parse.
        var network = NetworkParser.Parse(netPath);

        var engine = new Engine();
        engine.LoadScenario(netPath, rouPath, cfgPath);
        var runner = new SimulationRunner(engine);

        var bus = new Sim.Replication.InMemoryReplicationBus();
        var publisher = new Sim.Host.ReplicationPublisher();

        // Per (vehicle handle, lane handle) observed Pos sequence across the WHOLE run (not just the bus's
        // capacity-bounded rolling history), so the monotonicity check in (b) covers every step, not just
        // the last few samples.
        var posByHandleLane = new Dictionary<(VehicleHandle Handle, int Lane), List<double>>();
        var sawNonGreenTl = false;

        publisher.PublishGeometryOnce(network, bus.Sink);
        bus.Source.Pump();

        for (var step = 0; step < 30; step++)
        {
            runner.Tick();
            publisher.PublishStep(runner.Snapshot, bus.Sink);
            bus.Source.Pump();

            foreach (var kv in bus.Source.History)
            {
                var hist = kv.Value;
                if (hist.Count == 0)
                {
                    continue;
                }

                var latest = hist[hist.Count - 1];
                var rec = latest.Record;
                var key = (kv.Key, rec.LaneHandle);
                if (!posByHandleLane.TryGetValue(key, out var seq))
                {
                    seq = new List<double>();
                    posByHandleLane[key] = seq;
                }

                // A vehicle not gated this step re-reads the same latest sample -- only record on change.
                if (seq.Count == 0 || seq[^1] != rec.Pos)
                {
                    seq.Add(rec.Pos);
                }
            }

            foreach (var kv in bus.Source.TlStateByLane)
            {
                var signal = (char)kv.Value;
                if (signal != 'g' && signal != 'G')
                {
                    sawNonGreenTl = true;
                }
            }
        }

        // (a) published geometry lane count == network lane count.
        Assert.True(bus.Source.GeometryComplete);
        Assert.Equal(network.LanesByHandle.Count, bus.Source.Geometry.Count);

        // (b) monotonic non-decreasing Pos on a given lane, for at least one vehicle with more than one
        // recorded sample on that lane.
        Assert.True(posByHandleLane.Count > 0, "no vehicle/lane samples were published across the run");
        var sawMultiSampleSequence = false;
        foreach (var kv in posByHandleLane)
        {
            var seq = kv.Value;
            for (var i = 1; i < seq.Count; i++)
            {
                Assert.True(
                    seq[i] >= seq[i - 1],
                    $"Pos went backward for vehicle {kv.Key.Handle} on lane {kv.Key.Lane}: [{string.Join(", ", seq)}]");
            }

            if (seq.Count > 1)
            {
                sawMultiSampleSequence = true;
            }
        }

        Assert.True(sawMultiSampleSequence, "no vehicle/lane pair had more than one recorded Pos sample");

        // (c) at least one TlEntry with a non-'g'/'G' state observed at some step.
        Assert.True(sawNonGreenTl, "no red/amber traffic-light state observed across the 30-step run");
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
