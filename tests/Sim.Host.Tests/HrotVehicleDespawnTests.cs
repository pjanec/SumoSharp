using Sim.Core;
using Sim.Ingest;
using Sim.Replication;
using Xunit;

namespace Sim.Host.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H15 -- ReplicationPublisher.PublishDespawn: one vehicle's tombstone published between
// steps, exactly once (the next PublishStep's snapshot diff does not repeat it), and nothing for a handle never announced.
public class HrotVehicleDespawnTests
{
    private sealed class LifecycleRecorder : IReplicationSink
    {
        public List<LifecycleRecord> Records { get; } = new();
        public void PublishGeometry(IReadOnlyList<GeometryCodec.LaneGeo> lanes) { }
        public void PublishLifecycle(in LifecycleRecord record) => Records.Add(record);
        public void PublishFrame(uint step, double time, ReadOnlySpan<VehicleRecord> movers) { }
        public void PublishTrafficLights(uint step, double time, IReadOnlyList<TlCodec.TlEntry> lights) { }
        public void Dispose() { }
    }

    [Fact]
    public void H15_PublishDespawn_AnnouncesOnce_AndTheNextStepDoesNotRepeatIt()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "09-traffic-light");
        var netPath = Path.Combine(dir, "net.net.xml");
        var engine = new Engine();
        engine.LoadScenario(netPath, Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));
        var runner = new SimulationRunner(engine);
        var rec = new LifecycleRecorder();
        var publisher = new ReplicationPublisher();
        publisher.PublishGeometryOnce(NetworkParser.Parse(netPath), rec);

        for (var step = 0; step < 20; step++)
        {
            runner.Tick();
            publisher.PublishStep(runner.Snapshot, rec);
        }

        var victim = runner.Snapshot.Handles[0];
        Assert.False(publisher.PublishDespawn(new VehicleHandle(9999, 0), rec)); // never announced
        var before = rec.Records.Count;

        Assert.True(engine.Despawn(victim));
        Assert.True(publisher.PublishDespawn(victim, rec));
        Assert.Equal(before + 1, rec.Records.Count);
        Assert.False(rec.Records[^1].IsSpawn);
        Assert.Equal(victim, rec.Records[^1].Handle);
        Assert.False(publisher.PublishDespawn(victim, rec)); // already announced gone

        for (var step = 0; step < 5; step++)
        {
            runner.Tick();
            publisher.PublishStep(runner.Snapshot, rec);
        }

        Assert.Single(rec.Records, r => !r.IsSpawn && r.Handle.Equals(victim));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Traffic.sln not found above the test assembly.");
    }
}
