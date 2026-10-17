using System.Linq;
using Sim.Core;
using Sim.Ingest;
using Sim.Replication;
using Xunit;

namespace Sim.Host.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H7 — an external type and a colour on the spawn record, carried through
// the transport-neutral in-memory bus into IReplicationSource.Tags, and produced by ReplicationPublisher.TagOf.
public class HrotVehicleTagTests
{
    [Fact]
    public void H7_TheInMemoryBus_CarriesTheSpawnTag_AndForgetsItOnDespawn()
    {
        var bus = new InMemoryReplicationBus();
        var tagged = new VehicleHandle(1, 1);
        var plain = new VehicleHandle(2, 1);

        bus.Sink.PublishLifecycle(new LifecycleRecord(tagged, true, 0, 4.5f, 1.8f, "a", externalType: 0x0102030405L, color: 0xFF8800));
        bus.Sink.PublishLifecycle(new LifecycleRecord(plain, true, 0, 4.5f, 1.8f, "b"));
        bus.Source.Pump();

        Assert.True(bus.Source.Tags.TryGetValue(tagged, out var tag));
        Assert.Equal(0x0102030405L, tag.ExternalType);
        Assert.Equal(0xFF8800u, tag.Color);
        Assert.False(bus.Source.Tags.ContainsKey(plain)); // an empty tag is never stored (absent = (0, 0))

        bus.Sink.PublishLifecycle(new LifecycleRecord(tagged, isSpawn: false, vTypeId: 0, length: 0f, width: 0f));
        bus.Source.Pump();

        Assert.False(bus.Source.Tags.ContainsKey(tagged));
    }

    [Fact]
    public void H7_TheSixArgumentRecord_CarriesNoTag()
    {
        var rec = new LifecycleRecord(new VehicleHandle(3, 1), isSpawn: true, vTypeId: 0, length: 4f, width: 2f, name: "x");
        Assert.Equal(0L, rec.ExternalType);
        Assert.Equal(0u, rec.Color);
        Assert.True(rec.Tag.IsEmpty);
    }

    [Fact]
    public void H7_ThePublishersTagOf_IsCalledWithTheVTypeId_AndItsAnswerReachesTheSource()
    {
        var (engine, runner, network) = Load09();
        var bus = new InMemoryReplicationBus();
        var calls = new List<(string VType, VehicleHandle Handle)>();
        var publisher = new ReplicationPublisher
        {
            TagOf = (vType, h) =>
            {
                calls.Add((vType, h));
                return (4242L, 0x00AA55u);
            },
        };

        publisher.PublishGeometryOnce(network, bus.Sink);
        for (var step = 0; step < 20; step++)
        {
            runner.Tick();
            publisher.PublishStep(runner.Snapshot, bus.Sink);
            bus.Source.Pump();
        }

        Assert.NotEmpty(calls);
        Assert.Equal(calls.Count, calls.Select(c => c.Handle).Distinct().Count()); // once per vehicle
        var vTypeIds = engine.VehicleTypes.ToArray();
        Assert.All(calls, c => Assert.False(string.IsNullOrEmpty(c.VType)));
        Assert.NotEmpty(bus.Source.Tags);
        Assert.All(bus.Source.Tags.Values, t => Assert.Equal(new VehicleTag(4242L, 0x00AA55u), t));
        Assert.All(bus.Source.Tags.Keys, h => Assert.True(bus.Source.Dims.ContainsKey(h)));
        Assert.All(vTypeIds, id => Assert.Contains(id, calls.Select(c => c.VType)));
    }

    [Fact]
    public void H7_WithoutTagOf_NoTagIsPublished()
    {
        var (_, runner, network) = Load09();
        var bus = new InMemoryReplicationBus();
        var publisher = new ReplicationPublisher();

        publisher.PublishGeometryOnce(network, bus.Sink);
        for (var step = 0; step < 20; step++)
        {
            runner.Tick();
            publisher.PublishStep(runner.Snapshot, bus.Sink);
            bus.Source.Pump();
        }

        Assert.NotEmpty(bus.Source.Dims);
        Assert.Empty(bus.Source.Tags);
    }

    private static (Engine Engine, SimulationRunner Runner, NetworkModel Network) Load09()
    {
        var dir = Path.Combine(RepoRoot(), "scenarios", "09-traffic-light");
        var netPath = Path.Combine(dir, "net.net.xml");
        var engine = new Engine();
        engine.LoadScenario(netPath, Path.Combine(dir, "rou.rou.xml"), Path.Combine(dir, "config.sumocfg"));
        return (engine, new SimulationRunner(engine), NetworkParser.Parse(netPath));
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
