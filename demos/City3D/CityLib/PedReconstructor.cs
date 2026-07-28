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

    // Preallocated per-frame buffers for the parallel reconstruction (see Reconstruct). Grown only when the
    // ped population exceeds capacity, i.e. during fill-in => no steady-state allocation on the render path.
    private int[] _idBuf = new int[1024];
    private ReconstructedPed[] _slotBuf = new ReconstructedPed[1024];
    private bool[] _keepBuf = new bool[1024];

    // Leave headroom for the render thread and the display driver: this loop runs ON the render thread, so
    // saturating every core here is self-defeating (the same reasoning as A22's engine-side cap, which
    // resolved to cores-4). The engine's producer thread is also running its own parallel regions.
    private readonly System.Threading.Tasks.ParallelOptions _pedParallelOptions = new()
    {
        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 4),
    };
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

        // PERF: reconstruct peds in PARALLEL. Measured on GPU at 31 890 peds, this loop was 33.0 ms of an
        // 84.5 ms frame (39%, ~1.0 us/ped) -- the single largest render-thread cost -- and it is per-ped
        // independent: every call reads PUBLISHED, immutable state (`HeadlessIg` is only mutated by `Apply`
        // from the serial `Pump` above) and mutates only that ped's own `SmoothState` object.
        //
        // ORDER IS PRESERVED EXACTLY. Results go into a preallocated slot array indexed by position in
        // `KnownIds`, then a serial compaction copies the kept ones out in that same order. Order matters
        // because the MultiMesh assigns instance index by position, so a shuffled list would reshuffle which
        // instance draws which ped every frame (visually: flicker), and it keeps this refactor a pure
        // speed-up rather than a behaviour change.
        //
        // Godot's own API is NOT touched here -- `ToGodot` is pure struct math on the frame's origin, and the
        // MultiMesh writes happen later on the main thread in UpdatePeds. That boundary is why this is legal.
        var idCount = _reconstructor.KnownIds.Count;
        if (_idBuf.Length < idCount)
        {
            _idBuf = new int[Math.Max(idCount, _idBuf.Length * 2)];
            _slotBuf = new ReconstructedPed[_idBuf.Length];
            _keepBuf = new bool[_idBuf.Length];
        }

        var n = 0;
        foreach (var id in _reconstructor.KnownIds)
        {
            _idBuf[n++] = id;
        }

        var ids = _idBuf;
        var slots = _slotBuf;
        var keep = _keepBuf;
        var frame = _frame;
        var recon = _reconstructor;

        System.Threading.Tasks.Parallel.For(0, n, _pedParallelOptions, i =>
        {
            keep[i] = false;
            var id = ids[i];
            if (!recon.TryGetRenderPose(id, out var p, out var pz, out var vis, out _) || !vis)
            {
                return;
            }

            var (px, py, pgz) = frame.ToGodot(p.X, p.Y, pz);
            var reg = recon.Ig.ModelOf(id) == PedDrModel.FreeKinematic
                ? PedRegime.HighPower
                : PedRegime.LowPower;
            slots[i] = new ReconstructedPed(id, px, py, pgz, reg, visible: true);
            keep[i] = true;
        });

        for (var i = 0; i < n; i++)
        {
            if (keep[i])
            {
                _scratch.Add(slots[i]);
            }
        }

        return _scratch;
    }

}
