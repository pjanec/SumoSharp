using CycloneDDS.Runtime;
using Sim.Replication.Dds;

namespace Sim.Viewer.Core;

// docs/SUMOSHARP-NATIVE-VIEWER.md P2 ("DDS topics + loopback DR") — the publish side of the native
// viewer's DDS data path. Reads an EngineHost's authoritative Snapshot each step and writes it out over
// four topics (vehicles/geometry/lifecycle/TL), reusing the same FrameCodec/GeometryCodec/TlCodec bytes a
// TCP/UDP host would send. No rendering, no dead reckoning here — this is the write side only; DdsSubscriber
// (+ a later DrClock) is the read side.
//
// docs/DEMO-CITY3D-DESIGN.md "DRY rewire (parity-neutral)": this class is now a THIN COMPOSITION — the
// snapshot -> wire-record translation (lifecycle bookkeeping, the DR-error publish gate, the LaneWindow ->
// UpcomingLanes projection, TL low-rate gating) lives in Sim.Host.ReplicationPublisher, and the DDS encode
// + write half lives in Sim.Replication.Dds.DdsReplicationSink. This class's job is only to read the
// EngineHost's Network/Snapshot and hand them to the publisher + sink. Public entry points
// (PublishGeometryOnce/PublishStep/Reset/Dispose) and the exact bytes on the wire are UNCHANGED from
// before this rewire.
public sealed class DdsPublisher : IDisposable
{
    private readonly EngineHost _host;
    private readonly DdsReplicationSink _sink;
    private readonly Sim.Host.ReplicationPublisher _publisher = new();

    public DdsPublisher(EngineHost host, DdsParticipant participant)
    {
        _host = host;
        _sink = new DdsReplicationSink(participant);
    }

    // Publish the network's static lane geometry ONCE (durable-intent: see the topic's own comment for the
    // QoS caveat this phase doesn't yet set). Call this once, before the step loop starts, after readers
    // have had time to discover the writer (DDS discovery is async — the caller sleeps first).
    public void PublishGeometryOnce() => _publisher.PublishGeometryOnce(_host.Network, _sink);

    // Call once per sim step: publishes lifecycle deltas, the adaptive-rate-gated vehicle state, and (at a
    // low rate) TL state.
    public void PublishStep() => _publisher.PublishStep(_host.Snapshot, _sink);

    // Forget all per-vehicle publish state, for when the sim is rebuilt at t=0 (EngineHost.Restart). The
    // adaptive scheduler and known-vehicle registry live in ReplicationPublisher now; the DDS sink itself
    // carries no cross-frame state, so there is nothing to reset there.
    public void Reset() => _publisher.Reset();

    public void Dispose() => _sink.Dispose();
}
