using Sim.Ingest;
using Sim.Replication;
using SceneTl = Sim.Scene.TrafficLightPlacer;

namespace CityLib;

// docs/DEMO-CITY3D-DESIGN.md "Procedural scene generation -> Traffic lights" / task T1.6 -- signal-head
// placement. docs/HROT-INTEGRATION-DESIGN.md H2: the placement math and the signal-byte -> colour mapping
// now live in Sim.Scene.TrafficLightPlacer (SUMO's Z-up frame); this wrapper keeps CityLib's public API
// (SignalHead / SignalColor / TrafficLightPlacer) and returns Godot-space heads via (x, y, z) -> (x, z, -y).
// Static geometry (the pole/head POSITIONS never move once placed); only the head's material colour
// changes per frame, driven by `ColorFor` off the live replicated signal byte.
public readonly struct SignalHead
{
    public SignalHead(int laneHandle, float poleX, float poleY, float poleZ, float headX, float headY, float headZ)
    {
        LaneHandle = laneHandle;
        PoleX = poleX;
        PoleY = poleY;
        PoleZ = poleZ;
        HeadX = headX;
        HeadY = headY;
        HeadZ = headZ;
    }

    // The TL-controlled approach lane this head signals for -- the same key
    // Sim.Replication.IReplicationSource.TlStateByLane is indexed by, so a renderer looks the live signal
    // byte up as `tlStateByLane[head.LaneHandle]`.
    public int LaneHandle { get; }

    // Pole base, already in GODOT space -- sits on the ground under the head (PoleY ~= the lane's own
    // ground elevation; 0 on a flat net).
    public float PoleX { get; }
    public float PoleY { get; }
    public float PoleZ { get; }

    // Signal head, already in GODOT space -- directly above the pole base, raised HeadHeightMeters.
    public float HeadX { get; }
    public float HeadY { get; }
    public float HeadZ { get; }
}

// Pure mapping of a signal byte ('r'/'y'/'g'/'G'/other) to a renderer-agnostic colour bucket -- the
// Viewer project maps each bucket to an actual emissive Color.
public enum SignalColor
{
    Red,
    Yellow,
    Green,
    Off,
}

public static class TrafficLightPlacer
{
    // Design "Traffic lights": a believable eye-level-and-above signal mast height.
    public const float HeadHeightMeters = SceneTl.HeadHeightMeters;

    // Pure signal-byte -> colour-bucket mapping: 'r' -> Red, 'y' -> Yellow, 'g'/'G' -> Green, anything
    // else -> Off (Sim.Scene.TrafficLightPlacer.ColorFor).
    public static SignalColor ColorFor(byte signalByte) => SceneTl.ColorFor(signalByte) switch
    {
        Sim.Scene.SignalColor.Red => SignalColor.Red,
        Sim.Scene.SignalColor.Yellow => SignalColor.Yellow,
        Sim.Scene.SignalColor.Green => SignalColor.Green,
        _ => SignalColor.Off,
    };

    // For each TL-controlled approach lane, one SignalHead at the lane's downstream end nudged to its right
    // edge (Sim.Scene.TrafficLightPlacer.Place), swapped into Godot space.
    public static IReadOnlyList<SignalHead> Place(
        SumoGodotFrame frame, NetworkModel network, IEnumerable<int> controlledLaneHandles)
        => ToGodot(SceneTl.Place(frame.Scene, network, controlledLaneHandles));

    // docs/DEMO-CITY3D-DESIGN.md "Data path -> Remote mode" / task T2.2b: the REMOTE-shaped counterpart,
    // from the received wire geometry (ground-referenced like the original: the wire carries no ground z).
    public static IReadOnlyList<SignalHead> Place(
        SumoGodotFrame frame,
        IReadOnlyDictionary<int, GeometryCodec.LaneGeo> geometry,
        IEnumerable<int> controlledLaneHandles)
        => ToGodot(SceneTl.Place(frame.Scene, geometry, controlledLaneHandles));

    // Local SUMO (x, y, z-up) -> Godot (x, z, -y), for both the pole base and the head.
    private static IReadOnlyList<SignalHead> ToGodot(IReadOnlyList<Sim.Scene.SignalHead> heads)
    {
        var result = new List<SignalHead>(heads.Count);
        foreach (var h in heads)
        {
            result.Add(new SignalHead(h.LaneHandle, h.PoleX, h.PoleZ, -h.PoleY, h.HeadX, h.HeadZ, -h.HeadY));
        }

        return result;
    }
}
