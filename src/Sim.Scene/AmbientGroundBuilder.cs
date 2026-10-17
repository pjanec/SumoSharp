namespace Sim.Scene;

// docs/HROT-INTEGRATION-DESIGN.md H2 (NEW): the AMBIENT GROUND surface -- one quad (2 triangles) per
// TerrainField lattice cell, vertex heights straight from the field. It is the terrain-following floor
// under and around the road network; a host that has real terrain data of its own (a heightmap, a
// photogrammetry tile) simply drops the cells it covers, or bends a vertex height to blend the seam.
//
// THE LATTICE. Corners sit at (MinX + i*CellSize, MinY + j*CellSize), i < CountX, j < CountY; a cell is
// the quad between four neighbouring corners. A FLAT field (a 2-D net, or no terrain at all) has no
// lattice of its own, so one is synthesised anchored at (minX, minY) with DefaultCellSizeMeters (the cell
// size grows past MaxCornersPerAxis exactly as TerrainField does) -- a flat net still gets a floor, at
// the field's flat height.
//
// WHICH CELLS. A cell is emitted when its CENTRE lies inside [minX,maxX] x [minY,maxY] (inclusive) and
// `excludeCell(centreX, centreY)` is not true. Only cells of the lattice are ever emitted -- the lattice
// is the net's bounding box, so the rectangle is clipped to it.
//
// Quads do not share vertices, so a cell's corner heights are independent of which neighbours survive
// the exclusion. `heightOverride(x, y, fieldHeight)` is evaluated PER VERTEX POSITION (a corner shared by
// two kept cells gets the same value in both), and returns the SUMO-world height to use instead.
//
// Normals are the per-quad face normal (Z-up, +z component positive). Winding is CCW from above.
public static class AmbientGroundBuilder
{
    public static SceneMesh Build(
        SceneFrame frame,
        double minX,
        double minY,
        double maxX,
        double maxY,
        Func<double, double, bool>? excludeCell = null,
        Func<double, double, double, double>? heightOverride = null)
    {
        var field = frame.Terrain;
        double originX, originY, cell;
        int countX, countY;
        if (field.IsFlat || field.CountX < 2 || field.CountY < 2)
        {
            // Synthesised lattice over the requested rectangle.
            originX = minX;
            originY = minY;
            var spanX = Math.Max(maxX - minX, 0.0);
            var spanY = Math.Max(maxY - minY, 0.0);
            cell = TerrainField.DefaultCellSizeMeters;
            var maxSpan = Math.Max(spanX, spanY);
            var limit = (TerrainField.MaxCornersPerAxis - 1) * cell;
            if (maxSpan > limit)
            {
                cell = maxSpan / (TerrainField.MaxCornersPerAxis - 1);
            }

            countX = (int)Math.Ceiling(spanX / cell) + 1;
            countY = (int)Math.Ceiling(spanY / cell) + 1;
        }
        else
        {
            originX = field.MinX;
            originY = field.MinY;
            cell = field.CellSize;
            countX = field.CountX;
            countY = field.CountY;
        }

        var cellsX = countX - 1;
        var cellsY = countY - 1;
        if (cellsX < 1 || cellsY < 1)
        {
            return SceneMesh.Empty;
        }

        var verts = new List<float>();
        var norms = new List<float>();
        var inds = new List<int>();
        var cells = 0;

        // Row-major, j outer / i inner: a fixed, deterministic emission order.
        for (var j = 0; j < cellsY; j++)
        {
            var y0 = originY + (j * cell);
            var y1 = originY + ((j + 1) * cell);
            var cy = (y0 + y1) * 0.5;
            if (cy < minY || cy > maxY)
            {
                continue;
            }

            for (var i = 0; i < cellsX; i++)
            {
                var x0 = originX + (i * cell);
                var x1 = originX + ((i + 1) * cell);
                var cx = (x0 + x1) * 0.5;
                if (cx < minX || cx > maxX)
                {
                    continue;
                }

                if (excludeCell is not null && excludeCell(cx, cy))
                {
                    continue;
                }

                var p00 = Corner(frame, field, heightOverride, x0, y0);
                var p10 = Corner(frame, field, heightOverride, x1, y0);
                var p11 = Corner(frame, field, heightOverride, x1, y1);
                var p01 = Corner(frame, field, heightOverride, x0, y1);

                // Face normal from the two diagonals (robust for a non-planar quad).
                var d1x = p11.X - p00.X;
                var d1y = p11.Y - p00.Y;
                var d1z = p11.Z - p00.Z;
                var d2x = p01.X - p10.X;
                var d2y = p01.Y - p10.Y;
                var d2z = p01.Z - p10.Z;
                // d1 x d2 points +z for the CCW corner order p00,p10,p11,p01.
                var nx = (d1y * d2z) - (d1z * d2y);
                var ny = (d1z * d2x) - (d1x * d2z);
                var nz = (d1x * d2y) - (d1y * d2x);
                var nl = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                float fnx = 0f, fny = 0f, fnz = 1f;
                if (nl > 1e-12)
                {
                    fnx = (float)(nx / nl);
                    fny = (float)(ny / nl);
                    fnz = (float)(nz / nl);
                }

                var b = verts.Count / 3;
                foreach (var p in new[] { p00, p10, p11, p01 })
                {
                    verts.Add(p.X);
                    verts.Add(p.Y);
                    verts.Add(p.Z);
                    norms.Add(fnx);
                    norms.Add(fny);
                    norms.Add(fnz);
                }

                inds.Add(b);
                inds.Add(b + 1);
                inds.Add(b + 2);
                inds.Add(b);
                inds.Add(b + 2);
                inds.Add(b + 3);
                cells++;
            }
        }

        if (cells == 0)
        {
            return SceneMesh.Empty;
        }

        return new SceneMesh(verts.ToArray(), inds.ToArray(), norms.ToArray(), cells * cell * cell);
    }

    private static (float X, float Y, float Z) Corner(
        SceneFrame frame, TerrainField field, Func<double, double, double, double>? heightOverride, double x, double y)
    {
        var h = field.HeightAt(x, y);
        if (heightOverride is not null)
        {
            h = heightOverride(x, y, h);
        }

        return frame.ToLocal(x, y, h);
    }
}
