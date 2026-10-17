namespace Sim.Replication.Dds;

// The topic-name constants. ⭐ HROT H11: each name ALSO lives on its type ([DdsTopic("sumo/...")] + [DdsQos]), so every writer and
// reader uses the 1-arg ctor and a generic CycloneDDS.NET tool (ddsmonitor) discovers and decodes the topic. ⛔ Superseded: "the
// [DdsTopic] attribute does NOT carry a usable name" — that was a BARE [DdsTopic] (no name argument). One type ⇒ one topic, so the
// crowd and the activity streams have their own types (DdsPedCrowdFrame, DdsPedActivityLeg). A rail pins each constant to its type.
public static class DdsTopicNames
{
    public const string Vehicles = "sumo/vehicles";
    public const string Lifecycle = "sumo/lifecycle";
    public const string Geometry = "sumo/geometry";
    public const string Tl = "sumo/tl";
    // Reverse channel: a view-only remote publishes commands here; the publisher subscribes and applies them
    // to its EngineHost (pause/speed/restart/inject/...). See DdsViewerCommand + Sim.Viewer.Core command link.
    public const string Commands = "sumo/commands";
    // Forward status: the publisher publishes its engine state (mode/paused/speed/step/time/count) here so a
    // remote can reflect real state and disable inapplicable controls. See DdsViewerStatus.
    public const string Status = "sumo/status";

    // Pedestrian wire (docs/PEDESTRIAN-DDS-TRANSPORT-DESIGN.md §4) -- a SEPARATE topic set from the vehicle
    // stream so an IG can subscribe to peds independently. PedCrowd carries the high-rate DdsWireFrame blob
    // (Kind = FrameCodec.KindPedFreeKinematic); PedPathArc/PedActivity carry the durable per-ped DdsPedLeg
    // (same struct, distinct topic names); PedLifecycle carries the keyed DdsPedLifecycle. Additive: the
    // vehicle names above are unchanged.
    public const string PedCrowd = "sumo/ped-crowd";
    public const string PedPathArc = "sumo/ped-patharc";
    public const string PedActivity = "sumo/ped-activity";
    public const string PedLifecycle = "sumo/ped-lifecycle";
}
