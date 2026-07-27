using Sim.Ingest;

namespace CityLib;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §5 (T2): the SUMO -> Godot placement transform WITH A RECENTER
// ORIGIN, and its inverse.
//
// WHY THIS EXISTS. `CoordinateTransform.SumoToGodot` is a bare `(float)` cast with zero offset. That is
// fine for the synthetic demo (coordinates ~2000-2900, float ULP ~0.2 mm) and NOT fine for a real
// georeferenced cut: a SumoData Geneva box keeps the full net's netOffset, so its own local coordinates
// are ~1e5 (the committed `scenarios/_ped/georef_min` fixture sits at ~91850, 73960). Float has 24
// mantissa bits, so at 1e5 the ULP is ~8 mm and at the ~1.4e5 of a larger box ~16 mm -- which, once
// composed with camera and MultiMesh transforms, shows up as jitter, z-fighting between the coplanar
// road/marking quads, and an orbit camera that wobbles as it turns.
//
// The cure is to subtract a single per-scene origin BEFORE the cast, so the floats a renderer ever sees
// are small. `Identity` (origin 0,0,0) is EXACTLY today's arithmetic, so the demo and every existing
// test are unaffected.
//
// THE ORIGIN IS RENDER-SIDE ONLY. It never touches `NetworkModel`, never round-trips into the sim, and
// no sim-facing API takes or returns recentered numbers -- the sim, the wire, and anything a consumer
// converts back to UTM all stay in the net's own absolute frame (design §2: BIG converts SUMO -> UTM
// with `utm = sumo - netOffset`, which only works because we leave the georeference alone).
//
// SCOPE (owner): target areas up to ~20x20 km, where one recenter keeps every coordinate within
// +-10-20 km and float is ~mm. No tiling, no double-precision render path. Loading a whole country
// (~280 km) will show float error and is explicitly out of scope.
public readonly struct SumoGodotFrame
{
    // Zero origin: bitwise-identical to CoordinateTransform.SumoToGodot. The default for the demo and
    // for any caller that has not opted into recentering.
    public static readonly SumoGodotFrame Identity = new(0.0, 0.0, 0.0);

    public SumoGodotFrame(double originX, double originY, double originZ = 0.0)
    {
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
    }

    // The subtracted origin, in SUMO world metres. X/Y are the horizontal recenter; Z is the ELEVATION
    // recenter, which matters for a georeferenced 3-D net for a different reason than precision: a
    // Geneva cut's roads sit at z ~370-400 m, while everything that hardcodes ground level (the
    // realism-zone ring, the zone tint, POI ground marks -- all of which pass sumoZ = 0) would render
    // at Godot Y = 0, i.e. 400 m underground. Subtracting the net's mean elevation puts the whole scene
    // in a +-50 m band around Y = 0 and keeps ground-referenced overlays on the ground.
    public double OriginX { get; }
    public double OriginY { get; }
    public double OriginZ { get; }

    public bool IsIdentity => OriginX == 0.0 && OriginY == 0.0 && OriginZ == 0.0;

    // SUMO (x, y, z) -> Godot (x, y, z), recentered. Same axis mapping as
    // CoordinateTransform.SumoToGodot (Godot.X = Sumo.X, Godot.Y = Sumo.Z, Godot.Z = -Sumo.Y); the
    // origin is subtracted in DOUBLE precision, before the cast -- that ordering is the entire point.
    public (float X, float Y, float Z) ToGodot(double sumoX, double sumoY, double sumoZ)
        => ((float)(sumoX - OriginX), (float)(sumoZ - OriginZ), (float)-(sumoY - OriginY));

    // GROUND-REFERENCED placement: `heightAboveGround` metres above the scene's ground DATUM, rather
    // than at an absolute SUMO elevation.
    //
    // It exists because several overlays have no elevation data of their own and have always passed
    // `sumoZ = 0` meaning "on the ground": the zone tint, POI ground markers, building-entrance doors,
    // the realism-zone ring. On a 2-D net (the demo) "SUMO z = 0" and "on the ground" are the same
    // statement and this is bit-identical to `ToGodot`. On a georeferenced 3-D net they are wildly
    // different: the roads are at z ~370-400 m, so `ToGodot(x, y, 0)` would render those overlays ~385 m
    // UNDERGROUND. Anchoring them to the datum (`OriginZ`, the net's mid-elevation) instead puts them
    // back on the visible surface.
    //
    // LIMITATION, stated rather than hidden: the datum is FLAT, so on hilly terrain a ground overlay
    // can sit tens of metres off the true local surface. Anything that HAS real elevation data --
    // road meshes (Lane.ShapeZ), cars (KinematicReconResult.Z), crosswalk/lane-marking paint (the
    // lane's own interpolated z) -- must use `ToGodot` with that real value and not this.
    // Pedestrians currently use this datum because the ped stack is 2-D; per-pedestrian elevation is
    // being added to the ped engine in a separate workstream, and lands as a `ToGodot` call here.
    public (float X, float Y, float Z) GroundToGodot(double sumoX, double sumoY, double heightAboveGround)
        => ((float)(sumoX - OriginX), (float)heightAboveGround, (float)-(sumoY - OriginY));

    // The inverse, for the places that map a GODOT point back to SUMO -- the camera-driven LC-realism
    // zone being the one in the wild (Main.CameraLcZone). It must use the same origin, or the zone
    // lands somewhere else entirely; this is the call site most easily missed, because the naive
    // `(gx, -gz)` type-checks perfectly while being an origin's distance wrong.
    public (double X, double Y) ToSumo(float godotX, float godotZ)
        => (godotX + OriginX, -godotZ + OriginY);

    // Convenience: the frame centred on a rectangle given in SUMO metres (a crop rect, or a net AABB).
    public static SumoGodotFrame CenteredOn(double x0, double y0, double x1, double y1, double originZ = 0.0)
        => new((x0 + x1) * 0.5, (y0 + y1) * 0.5, originZ);

    // The frame for a whole parsed network: the centre of the AABB over every lane shape point, with
    // the elevation origin set to the midpoint of the net's z range (0 for a 2-D net, so a 2-D net's
    // frame recenters horizontally only). Mirrors LiveCitySim.ComputeNetAabbCentre's own "AABB over
    // every parsed lane shape, every edge, vehicle or pedestrian" definition so the viewer's origin and
    // the sim's own default realism-pocket centre are derived from the same geometry.
    //
    // A network with no lane geometry at all yields `Identity` -- defensive; never happens for a real
    // net.xml.
    public static SumoGodotFrame ForNetwork(NetworkModel network)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var minZ = double.PositiveInfinity;
        var maxZ = double.NegativeInfinity;
        var any = false;

        foreach (var lane in network.LanesById.Values)
        {
            foreach (var (x, y) in lane.Shape)
            {
                any = true;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            if (lane.ShapeZ is { Count: > 0 } zs)
            {
                foreach (var z in zs)
                {
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
            }
        }

        if (!any)
        {
            return Identity;
        }

        var originZ = minZ <= maxZ ? (minZ + maxZ) * 0.5 : 0.0;
        return new SumoGodotFrame((minX + maxX) * 0.5, (minY + maxY) * 0.5, originZ);
    }
}
