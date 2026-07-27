using Sim.Pedestrians.Lod;
using Sim.Replication;

namespace CityLib;

// The ped LOD regime, mirrored into CityLib so neither CityLib nor Viewer depends on Sim.Viewer's own
// PedRegime (the values are the contract, not the type). Low-power = the deterministic PathArc follower;
// high-power = the full FreeKinematic OrcaCrowd agent (docs/DEMO-CITY3D-DESIGN.md "#### Pedestrians (P7-3)":
// "regime from Ig.ModelOf: FreeKinematic -> high-power else low-power").
public enum PedRegime
{
    LowPower = 0,
    HighPower = 1,
}

// One pedestrian's fully-reconstructed render pose, in GODOT coordinates (CoordinateTransform already
// applied) -- the plain struct the Viewer glue turns into a MultiMesh per-instance transform. No Godot type
// here (CityLib stays engine-agnostic); the ped analog of ReconstructedVehicle. Y (Godot up) is the ped's
// SURFACE ELEVATION recentered by the scene frame -- 0 on a flat net, the real road height on a 3-D one.
public readonly struct ReconstructedPed
{
    public ReconstructedPed(int id, float x, float y, float z, PedRegime regime, bool visible)
    {
        Id = id;
        X = x; Y = y; Z = z;
        Regime = regime;
        Visible = visible;
    }

    public int Id { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public PedRegime Regime { get; }
    public bool Visible { get; }

    public bool IsHighPower => Regime == PedRegime.HighPower;
}

// docs/DEMO-CITY3D-DESIGN.md "#### Pedestrians (P7-3)" -- the ped analog of Reconstructor. Wraps a
// Sim.Pedestrians.Lod.PedRemoteReconstructor (the render-side consumer that closes the server -> wire -> IG
// -> render loop off the ped stack, NOT the vehicle DrClock/PoseResolver/DrPoseSmoother stack): Pump(now)
// once per render frame, then for each KnownId TryGetRenderPose -> skip if not visible -> apply the one
// fixed CoordinateTransform.SumoToGodot -> a ReconstructedPed (regime from Ig.ModelOf). All the
// DR/playout-delay/capped-correction smoothing already lives INSIDE PedRemoteReconstructor (the "no
// promotion pop" story), so this layer is thin -- the ped analog of the vehicle Reconstructor being where
// the DR lives.
public sealed class PedReconstructor
{
    private readonly List<ReconstructedPed> _scratch = new();
    private readonly SumoGodotFrame _frame;
    private PedRemoteReconstructor? _reconstructor;
    private IPedReplicationSource? _boundSource;

    // docs/EXTERNAL-NET-VIEWER-DESIGN.md §5 (T2): the SUMO->Godot placement frame. Required rather
    // than defaulted for the same reason Reconstructor's is -- on a georeferenced net an unframed
    // placement lands 90 km from everything else, and a default would let that compile.
    public PedReconstructor(SumoGodotFrame frame)
    {
        _frame = frame;
    }

    // Call once per render frame with the ped-sim's current server time (PedSimSource.Time). Constructs the
    // wrapped PedRemoteReconstructor once, from the first source seen, and reuses it every frame after (its
    // smoothing/known-id state is per-instance and must persist across frames, exactly like the vehicle
    // Reconstructor's DrClock/DrPoseSmoother).
    public IReadOnlyList<ReconstructedPed> Reconstruct(IPedReplicationSource source, double serverTime)
    {
        if (_reconstructor is null)
        {
            _reconstructor = new PedRemoteReconstructor(source);
            _boundSource = source;
        }
        else if (!ReferenceEquals(source, _boundSource))
        {
            throw new InvalidOperationException(
                "PedReconstructor is bound to its first IPedReplicationSource; use one PedReconstructor per source.");
        }

        _reconstructor.Pump(serverTime);

        _scratch.Clear();
        foreach (var id in _reconstructor.KnownIds)
        {
            if (!_reconstructor.TryGetRenderPose(id, out var pos, out var z, out var visible, out _) || !visible)
            {
                continue;
            }

            // Peds now HAVE elevation (engine tasks C1-C5), so they join road meshes, cars and lane paint
            // on the ABSOLUTE path -- `ToGodot`, not the flat `GroundToGodot` ground datum this used while
            // the ped stack was 2-D. It must go through the FRAME, never CoordinateTransform: the frame
            // subtracts the scene's recenter origin, and its OriginZ half matters as much as the
            // horizontal one -- a Geneva cut's ped z is a real ~370-400 m absolute elevation, so bypassing
            // the frame would render peds ~380 m above the road even if the horizontal origin were zero.
            //
            // z is 0.0 on a 2-D net, where this is identical to the ground-datum call it replaces.
            var (gx, gy, gz) = _frame.ToGodot(pos.X, pos.Y, z);
            var regime = _reconstructor.Ig.ModelOf(id) == PedDrModel.FreeKinematic
                ? PedRegime.HighPower
                : PedRegime.LowPower;

            _scratch.Add(new ReconstructedPed(id, gx, gy, gz, regime, visible: true));
        }

        return _scratch;
    }
}
