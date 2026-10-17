namespace Sim.Scene;

// What a mesh IS, so a host can pick a material / layer / collision class without guessing from the
// geometry. One kind per mesh.
public enum SceneKind
{
    // A lane's road surface (normal and internal/junction-interior lanes alike).
    Asphalt,
    // A junction's own fill polygon (NetworkModel Junction.Shape), triangulated.
    Junction,
    // Painted lane-divider dashes.
    LaneMarking,
    // Painted crossing zebra stripes.
    Crosswalk,
    // The terrain-draped ground surface around/beneath the road network.
    AmbientGround,
}

// Triangle mesh in the SceneFrame's local SUMO frame (x east, y north, z UP, metres, relative to the
// frame origin). Winding is counter-clockwise viewed from above (+z).
public readonly struct SceneMesh
{
    public static readonly SceneMesh Empty =
        new(Array.Empty<float>(), Array.Empty<int>(), Array.Empty<float>(), 0.0);

    public SceneMesh(float[] vertices, int[] indices, float[] normals, double area)
    {
        Vertices = vertices;
        Indices = indices;
        Normals = normals;
        Area = area;
    }

    // xyz triples in the local SUMO frame (Z-up).
    public float[] Vertices { get; }
    public int[] Indices { get; }
    // xyz triples, same length as Vertices; Z-up (0,0,1) for a flat surface.
    public float[] Normals { get; }
    // Sum of the per-triangle/quad planar (ground-plane, SUMO x/y) areas, in square metres.
    public double Area { get; }

    public int VertexCount => Vertices.Length / 3;
    public int TriangleCount => Indices.Length / 3;
    public bool IsEmpty => Indices.Length == 0;

    // Concatenates independently-built meshes into one (index-offset merge), so a host can draw all
    // stripes / dashes of a kind as one draw call.
    public static SceneMesh Merge(IReadOnlyList<SceneMesh> parts)
    {
        var vertexCount = 0;
        var indexCount = 0;
        var area = 0.0;
        foreach (var part in parts)
        {
            vertexCount += part.Vertices.Length;
            indexCount += part.Indices.Length;
            area += part.Area;
        }

        var vertices = new float[vertexCount];
        var normals = new float[vertexCount];
        var indices = new int[indexCount];

        var offset = 0;
        var iOffset = 0;
        var vertBase = 0;
        foreach (var part in parts)
        {
            Array.Copy(part.Vertices, 0, vertices, offset, part.Vertices.Length);
            Array.Copy(part.Normals, 0, normals, offset, part.Normals.Length);
            for (var k = 0; k < part.Indices.Length; k++)
            {
                indices[iOffset + k] = part.Indices[k] + vertBase;
            }

            offset += part.Vertices.Length;
            iOffset += part.Indices.Length;
            vertBase += part.Vertices.Length / 3;
        }

        return new SceneMesh(vertices, indices, normals, area);
    }
}
