using Sim.Scene;

namespace CityLib;

// docs/HROT-INTEGRATION-DESIGN.md H2: the one place CityLib turns Sim.Scene's Z-up local SUMO frame
// (x east, y north, z up) into Godot's Y-up space. The mapping is exactly the axis swap
// SumoGodotFrame.ToGodot always applied: Godot (x, y, z) = (sumo x, sumo z, -sumo y), applied here to
// the already origin-subtracted floats. Negation is exact in IEEE-754, so `-(float)(v)` is bit-identical
// to the old `(float)-(v)`; the up-normal (0,0,1) maps to (0,1,0) (`0f - ny` keeps +0f, never -0f).
internal static class SceneToGodot
{
    public static RibbonMesh Ribbon(SceneMesh mesh)
    {
        if (mesh.Vertices.Length == 0)
        {
            return new RibbonMesh(mesh.Vertices, mesh.Indices, mesh.Normals, mesh.Area);
        }

        return new RibbonMesh(Swap(mesh.Vertices, negate: true), mesh.Indices, Swap(mesh.Normals, negate: false), mesh.Area);
    }

    public static SceneMesh AsScene(RibbonMesh mesh)
        => new(mesh.Vertices, mesh.Indices, mesh.Normals, mesh.Area);

    public static RibbonMesh AsRibbon(SceneMesh mesh)
        => new(mesh.Vertices, mesh.Indices, mesh.Normals, mesh.Area);

    // (x, y, z) -> (x, z, -y) per xyz triple. `negate` selects true negation (positions) vs `0 - v`
    // (normals, so a zero component stays +0).
    private static float[] Swap(float[] src, bool negate)
    {
        var dst = new float[src.Length];
        for (var i = 0; i + 2 < src.Length; i += 3)
        {
            dst[i] = src[i];
            dst[i + 1] = src[i + 2];
            dst[i + 2] = negate ? -src[i + 1] : 0f - src[i + 1];
        }

        return dst;
    }
}
