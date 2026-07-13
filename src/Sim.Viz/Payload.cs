using Sim.Ingest;

namespace Sim.Viz;

// ---------------------------------------------------------------------------------------------
// The UNIFIED multi-scene replay payload (VIZ_SPEC.md extension: one self-contained HTML player
// showing MANY laneless scenes). REPLAY_DATA is now `{ scenes: [ SCENE, ... ] }`; the front-end
// (template.js) renders one scene at a time and lets the user switch between them.
//
// A SCENE is deliberately compact (floats rounded to 2 dp by the builders): it carries the camera
// view box, an OPTIONAL network (null for pure open-space crowd scenes), a single shared vehicle
// box dimension, the timestep, and a flat per-frame list of vehicles (oriented boxes) and discs
// (crowd/pedestrian agents). See FramePayload for the frame encoding.
// ---------------------------------------------------------------------------------------------

// Network geometry -- identical shape to the original single-scenario payload so template.js's
// proven lane-band / lane-marking / traffic-light / signal drawing is reused verbatim. Shapes are
// FLAT [x0,y0,x1,y1,...] arrays (what the existing draw code indexes as shape[i*2]).
internal sealed record LanePayload(string Id, string EdgeId, int Index, double Width, double[] Shape);

internal sealed record JunctionPayload(string Id, double[] Shape);

internal sealed record TlPhasePayload(double Duration, string State);

internal sealed record TlLogicPayload(string Id, double Offset, TlPhasePayload[] Phases);

internal sealed record SignalHeadPayload(string Tl, int LinkIndex, double X, double Y, double Angle);

internal sealed record NetworkPayload(
    LanePayload[] Lanes,
    JunctionPayload[] Junctions,
    TlLogicPayload[] Tls,
    SignalHeadPayload[] Signals);

// One timestep of a scene.
//   v = vehicles as oriented boxes: each entry is [x, y, angleDeg] (front-centre reference point,
//       naviDegree 0 = +Y clockwise). Entries use FIXED SLOTS across all frames so a given index is
//       always the same vehicle; a vehicle absent this frame is `null` in its slot (vehicles enter /
//       leave in FCD scenarios). Empty array for pure-crowd scenes.
//   d = discs as [x, y, radius, kind]. kind: 0 = stream/agent A, 1 = stream/agent B, 2 = pedestrian.
internal sealed record FramePayload(double[]?[] V, double[][] D);

internal sealed record ScenePayload(
    string Name,
    string Desc,
    double[] View,           // [minX, minY, maxX, maxY] world bbox for camera fit
    NetworkPayload? Network, // null for pure open-space crowd scenes
    double[] Vdim,           // [length, width] shared vehicle box dims (0,0 if no vehicles)
    double Dt,               // seconds between successive frames
    FramePayload[] Frames,
    string[]? Labels = null);// optional per-scene legend labels indexed by disc kind (overrides the
                             // global DISC_LABELS -- e.g. the mixed-traffic scene labels kinds by
                             // vehicle class rather than stream/pedestrian)

internal sealed record ReplayData(ScenePayload[] Scenes);

// Shared payload helpers used by both the FCD exporter (Program.cs) and the programmatic
// crowd/engine scene generator (SceneGen.cs).
internal static class PayloadBuilder
{
    // Round to 2 dp -- the whole payload's compaction rule (VIZ_SPEC: "round floats to 2 dp").
    internal static double R(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    // Build the network geometry payload from a parsed NetworkModel. This is exactly the
    // lane/junction/TL/signal derivation the original single-scenario exporter did, factored out so
    // scene C (which reuses a real SUMO net) shares it. Coordinates rounded to 2 dp.
    internal static NetworkPayload BuildNetwork(NetworkModel network)
    {
        var lanes = new LanePayload[network.LanesByHandle.Count];
        for (var i = 0; i < network.LanesByHandle.Count; i++)
        {
            var lane = network.LanesByHandle[i];
            var flat = new double[lane.Shape.Count * 2];
            for (var p = 0; p < lane.Shape.Count; p++)
            {
                var (x, y) = lane.Shape[p];
                flat[p * 2] = R(x);
                flat[p * 2 + 1] = R(y);
            }

            lanes[i] = new LanePayload(lane.Id, lane.EdgeId, lane.Index, lane.Width, flat);
        }

        var junctions = new List<JunctionPayload>();
        foreach (var junction in network.Junctions)
        {
            if (junction.Shape.Count == 0)
            {
                continue;
            }

            var flat = new double[junction.Shape.Count * 2];
            for (var p = 0; p < junction.Shape.Count; p++)
            {
                var (x, y) = junction.Shape[p];
                flat[p * 2] = R(x);
                flat[p * 2 + 1] = R(y);
            }

            junctions.Add(new JunctionPayload(junction.Id, flat));
        }

        var tls = network.TlLogicsById.Values
            .Select(tl => new TlLogicPayload(
                tl.Id,
                tl.Offset,
                tl.Phases.Select(p => new TlPhasePayload(p.Duration, p.State)).ToArray()))
            .ToArray();

        // Signal heads: one per TL-controlled <connection>, placed at the from-lane's stop-line end.
        var signals = new List<SignalHeadPayload>();
        foreach (var connection in network.Connections)
        {
            if (connection.Tl is null || connection.LinkIndex is null)
            {
                continue;
            }

            if (!network.EdgesById.TryGetValue(connection.From, out var fromEdge))
            {
                continue;
            }

            var fromLane = fromEdge.Lanes.FirstOrDefault(l => l.Index == connection.FromLane);
            if (fromLane is null)
            {
                continue;
            }

            var (x, y, angle) = LaneGeometry.PositionAtOffset(fromLane.Shape, fromLane.Length);
            signals.Add(new SignalHeadPayload(connection.Tl, connection.LinkIndex.Value, R(x), R(y), R(angle)));
        }

        return new NetworkPayload(lanes, junctions.ToArray(), tls, signals.ToArray());
    }
}
