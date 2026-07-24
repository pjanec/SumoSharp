using Sim.Pedestrians;
using Sim.Pedestrians.Lod;
using Sim.Viewer.Motion;

namespace Sim.Viz;

internal sealed record VizReplayOptions(
    string Name,
    string Desc,
    double RenderHz = 10.0,           // DR-reconstructed samples; the player Catmull-Rom interpolates between
    int Steps = 160,                  // sim steps to run
    double? PlayoutDelaySeconds = null); // vehicle playout; default = source.Dt (one-step -> DrClock INTERPOLATE)

// The ONE reusable DR-smoothing replay builder. Runs the source's deterministic sim, reconstructs vehicle
// CENTER + emitted body heading via DrClock.ResolveAt + KinematicReconstructor (upcoming-lane look-ahead:
// continuous junction arcs), and peds via PedRemoteReconstructor off the ped wire (analytic, no tick kink).
// Emits a useDataHeading 5-tuple vehicle scene + 4-tuple ped discs. See IGBRIDGE-HTML-REPLAY-GUIDE.md.
internal static class VizReplayBuilder
{
    private static double R(double v) => System.Math.Round(v, 2, System.MidpointRounding.AwayFromZero);

    internal static ScenePayload Build(IVizReplaySource source, VizReplayOptions opts)
    {
        var (x0, y0, x1, y1) = source.View;
        bool In(double x, double y) => x >= x0 && x <= x1 && y >= y0 && y <= y1;

        var renderDt = 1.0 / opts.RenderHz;
        var delay = opts.PlayoutDelaySeconds ?? source.Dt;
        var drClock = new DrClock();
        var recon = new KinematicReconstructor { CoarseFeed = true };
        var lanes = source.Lanes;

        var pedRecon = source.PedSource is not null ? new PedRemoteReconstructor(source.PedSource) : null;
        var pedDelay = PedRemoteReconstructor.DefaultPlayoutDelaySeconds;

        var slotByHandle = new System.Collections.Generic.Dictionary<uint, int>();
        var nameByHandle = new System.Collections.Generic.Dictionary<uint, string>();  // slot id for click-to-identify
        var frames = new System.Collections.Generic.List<FramePayload>();
        var discsKeyedPerFrame = new System.Collections.Generic.List<System.Collections.Generic.List<(string Key, double[] Disc)>>();
        var lastTauByHandle = new System.Collections.Generic.Dictionary<uint, double>();

        var simTime = 0.0;
        var tau = 0.0;
        for (var step = 0; step < opts.Steps; step++)
        {
            source.Step();
            source.VehicleSource.Pump();
            simTime += source.Dt;

            var target = simTime - delay;
            while (tau <= target + 1e-9)
            {
                var poses = new System.Collections.Generic.List<(uint Idx, double X, double Y, double Deg, double Len, double Wid)>();
                foreach (var kv in source.VehicleSource.History)
                {
                    var history = kv.Value;
                    if (history.Count == 0) continue;
                    var handle = kv.Key;
                    var resolved = drClock.ResolveAt(history, tau, lanes);
                    var dims = source.VehicleSource.Dims.TryGetValue(handle, out var d) ? d : (5.0f, 1.8f);
                    var realDt = lastTauByHandle.TryGetValue(handle.Index, out var lt) ? System.Math.Max(1e-3, tau - lt) : renderDt;
                    var result = recon.Resolve(handle, resolved, lanes, dims, (float)realDt);
                    lastTauByHandle[handle.Index] = tau;
                    if (!result.Ok) continue;
                    if (!In(result.CenterX, result.CenterY)) continue;
                    if (!slotByHandle.ContainsKey(handle.Index))
                    {
                        slotByHandle[handle.Index] = slotByHandle.Count;
                        nameByHandle[handle.Index] = source.VehicleSource.Names.TryGetValue(handle, out var nm) && nm.Length > 0
                            ? nm : ("v" + handle.Index);
                    }
                    poses.Add((handle.Index, result.CenterX, result.CenterY, result.HeadingDeg, dims.Item1, dims.Item2));
                }

                var v = new double[slotByHandle.Count][];
                foreach (var (idx, x, y, deg, len, wid) in poses)
                    v[slotByHandle[idx]] = new[] { R(x), R(y), R(deg), R(len), R(wid) };

                var discs = new System.Collections.Generic.List<(string, double[])>();
                if (pedRecon is not null)
                {
                    pedRecon.Pump(tau + pedDelay);
                    foreach (var id in pedRecon.KnownIds)
                    {
                        if (!pedRecon.TryGetRenderPose(id, out var pos, out var visible, out var animTag) || !visible) continue;
                        if (!In(pos.X, pos.Y)) continue;
                        var kind = pedRecon.Ig.ModelOf(id) == PedDrModel.FreeKinematic ? SceneGen.KindPedHighPower
                            : animTag == ActivityTimeline.WalkAnimTag ? SceneGen.KindPedLowPower
                            : SceneGen.KindPedPaused;
                        discs.Add(($"ped{id}", new[] { R(pos.X), R(pos.Y), 0.3, (double)kind }));
                    }
                }

                frames.Add(new FramePayload(v, System.Array.Empty<double[]?>()));
                discsKeyedPerFrame.Add(discs);
                tau += renderDt;
            }
        }

        SceneGen.NormalizeVehicleSlots(frames, slotByHandle.Count);
        SceneGen.AssignStableDiscSlots(frames, discsKeyedPerFrame);

        // Per-slot vehicle ids for click-to-identify (template.js): aligned with the V-array slots.
        var vehIds = new string[slotByHandle.Count];
        foreach (var kv in slotByHandle) vehIds[kv.Value] = nameByHandle.TryGetValue(kv.Key, out var nm) ? nm : ("v" + kv.Key);

        return new ScenePayload(
            opts.Name,
            opts.Desc,
            new double[] { R(x0), R(y0), R(x1), R(y1) },
            source.Network,
            new double[] { 5.0, 1.8 },
            renderDt,
            frames.ToArray(),
            UseDataHeading: true,
            VehIds: vehIds);
    }
}
