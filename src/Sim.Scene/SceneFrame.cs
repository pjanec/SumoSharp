namespace Sim.Scene;

// docs/HROT-INTEGRATION-DESIGN.md H2: the placement frame of a scene, in SUMO's OWN axes -- x east,
// y north, z up, metres -- relative to a caller-chosen origin. This is the engine-agnostic core of what
// CityLib's SumoGodotFrame used to be: the Godot axis swap (x, z, -y) is NOT here; it is a per-host
// mapping applied on top (CityLib does it for Godot, a Z-up host such as HROT's IG applies none).
//
// WHY A FRAME. A georeferenced net keeps its netOffset, so local SUMO coordinates are ~1e5 and a float
// has ~8 mm ULP there. Subtracting a single per-scene origin in DOUBLE precision BEFORE the float cast
// keeps every float a renderer sees small. `Identity` (origin 0,0,0) is a plain `(float)` cast.
//
// THE ORIGIN IS RENDER-SIDE ONLY: it never touches NetworkModel and no sim-facing API takes or
// returns recentred numbers.
public readonly struct SceneFrame
{
    public static readonly SceneFrame Identity = new(0.0, 0.0, 0.0);

    // Null means "flat at OriginZ" -- what Identity, `default` and every terrain-less construction
    // mean. Held as a nullable field (not a non-null property) because this is a struct:
    // `default(SceneFrame)` must remain valid and flat.
    private readonly TerrainField? _terrain;

    public SceneFrame(double originX, double originY, double originZ = 0.0, TerrainField? terrain = null)
    {
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        _terrain = terrain;
    }

    public double OriginX { get; }
    public double OriginY { get; }
    public double OriginZ { get; }

    public bool IsIdentity => OriginX == 0.0 && OriginY == 0.0 && OriginZ == 0.0;

    // The scene's ground field. Never null: a frame built without one is flat at its own elevation
    // datum.
    public TerrainField Terrain => _terrain ?? TerrainField.Flat(OriginZ);

    // True when this frame carries a real, non-degenerate baked field.
    public bool HasTerrain => _terrain is { IsFlat: false };

    // SUMO (x, y, z) -> local frame (x - ox, y - oy, z - oz). The origin is subtracted in DOUBLE
    // precision, before the cast -- that ordering is the entire point.
    public (float X, float Y, float Z) ToLocal(double sumoX, double sumoY, double sumoZ)
        => ((float)(sumoX - OriginX), (float)(sumoY - OriginY), (float)(sumoZ - OriginZ));

    // GROUND-REFERENCED placement: `heightAboveGround` metres above the scene's ground (the baked
    // TerrainField, or the flat OriginZ datum when there is none), for overlays that have no elevation
    // of their own and mean "on the ground". Anything WITH real elevation data (a lane's own ShapeZ)
    // must use ToLocal with that value instead.
    public (float X, float Y, float Z) GroundToLocal(double sumoX, double sumoY, double heightAboveGround)
    {
        var ground = _terrain is null ? OriginZ : _terrain.HeightAt(sumoX, sumoY);
        return ((float)(sumoX - OriginX), (float)(sumoY - OriginY), (float)(ground - OriginZ + heightAboveGround));
    }

    // The inverse horizontal mapping: a local-frame (x, y) back to SUMO world metres.
    public (double X, double Y) ToWorld(float localX, float localY)
        => (localX + OriginX, localY + OriginY);
}
