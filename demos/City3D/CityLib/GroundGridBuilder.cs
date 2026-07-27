namespace CityLib;

// docs/EXTERNAL-NET-VIEWER-DESIGN.md §7.2.2: the grey reference grid, BAKED over the net's own extent
// and draped over the TerrainField, replacing the flat line mesh that used to be translated under the
// camera every frame.
//
// Why it could not stay a translation. The old grid was one flat mesh whose Position was snapped to the
// camera's XZ each frame, which is exactly what made it read as an infinite floor -- and exactly what
// makes it impossible to follow terrain, since the mesh never changes. Baking it once over the net's
// bbox is the owner's call ("needs to be built/baken. on road net load"), and has the side benefit that
// the grid now shows WHERE THE CITY IS instead of following you to infinity.
//
// Pure geometry, no Godot type (the CityLib split): Main.cs turns the returned vertex list into an
// ArrayMesh with PrimitiveType.Lines.
public static class GroundGridBuilder
{
    /// Finest line spacing. Matches the pre-terrain grid so a small net looks unchanged.
    public const double MinSpacingMeters = 25.0;

    /// Line-count ceiling per axis: on a large net the spacing grows instead of the vertex count, so a
    /// city-block net and a 30 km net cost the same mesh.
    public const int MaxLinesPerAxis = 240;

    /// Vertical offset below the ground surface, in metres. Just under the zone tint (-0.05) and the
    /// roads (0.0) so both keep drawing on top of it.
    public const double GroundOffsetSumoZ = -0.1;

    /// A baked grid line mesh: `Vertices` is xyz triples in GODOT space, consumed two-at-a-time as
    /// PrimitiveType.Lines. Each grid line is a POLYLINE (one segment per sample step) so it drapes over
    /// the field rather than cutting through it.
    public readonly struct GridMesh
    {
        public GridMesh(float[] vertices, double spacing, int lineCount)
        {
            Vertices = vertices;
            Spacing = spacing;
            LineCount = lineCount;
        }

        public float[] Vertices { get; }

        /// The spacing actually used, after the MaxLinesPerAxis cap (>= MinSpacingMeters).
        public double Spacing { get; }

        /// Total number of grid LINES emitted across both axes (not segments, not vertices).
        public int LineCount { get; }

        public int SegmentCount => Vertices.Length / 6;
    }

    /// Bake the grid over the SUMO-space rectangle [x0,x1] x [y0,y1] (already margin-expanded by the
    /// caller). Every vertex is placed through `frame.GroundToGodot`, so on a flat frame the result is a
    /// planar grid at the datum -- visually what it always was within the net -- and on a terrain frame
    /// it follows the surface.
    public static GridMesh Build(SumoGodotFrame frame, double x0, double y0, double x1, double y1)
    {
        if (x1 < x0)
        {
            (x0, x1) = (x1, x0);
        }

        if (y1 < y0)
        {
            (y0, y1) = (y1, y0);
        }

        var spanX = Math.Max(x1 - x0, MinSpacingMeters);
        var spanY = Math.Max(y1 - y0, MinSpacingMeters);
        var spacing = Math.Max(MinSpacingMeters, Math.Max(spanX, spanY) / MaxLinesPerAxis);

        // Snap the line positions to multiples of the spacing in WORLD coordinates, so the grid does not
        // shift when the extent changes (a re-bake on a different crop lines up with the previous one).
        var startX = Math.Floor(x0 / spacing) * spacing;
        var startY = Math.Floor(y0 / spacing) * spacing;

        // A grid line is only meaningful if it is sampled at least as finely as the field varies; on a
        // flat frame one segment per span would do, but two samples per cell keeps the code single-path
        // and the extra vertices are free at these counts.
        var step = frame.HasTerrain
            ? Math.Max(Math.Min(spacing, frame.Terrain.CellSize * 0.5), 1.0)
            : Math.Max(spanX, spanY); // flat: a single straight segment per line

        var verts = new List<float>();
        var lines = 0;

        for (var x = startX; x <= x1 + (spacing * 0.5); x += spacing)
        {
            EmitPolyline(frame, verts, along: false, fixedCoord: x, from: startY, to: y1, step);
            lines++;
        }

        for (var y = startY; y <= y1 + (spacing * 0.5); y += spacing)
        {
            EmitPolyline(frame, verts, along: true, fixedCoord: y, from: startX, to: x1, step);
            lines++;
        }

        return new GridMesh(verts.ToArray(), spacing, lines);
    }

    // One grid line as a run of Lines-primitive segments. `along == true` means the line runs along X at
    // a fixed Y; false means along Y at a fixed X.
    private static void EmitPolyline(
        SumoGodotFrame frame, List<float> verts, bool along, double fixedCoord, double from, double to, double step)
    {
        var prev = SampleAt(frame, along, fixedCoord, from);
        for (var t = from + step; ; t += step)
        {
            var clamped = Math.Min(t, to);
            var current = SampleAt(frame, along, fixedCoord, clamped);

            verts.Add(prev.X);
            verts.Add(prev.Y);
            verts.Add(prev.Z);
            verts.Add(current.X);
            verts.Add(current.Y);
            verts.Add(current.Z);

            prev = current;
            if (clamped >= to)
            {
                break;
            }
        }
    }

    private static (float X, float Y, float Z) SampleAt(SumoGodotFrame frame, bool along, double fixedCoord, double t)
        => along
            ? frame.GroundToGodot(t, fixedCoord, GroundOffsetSumoZ)
            : frame.GroundToGodot(fixedCoord, t, GroundOffsetSumoZ);
}
