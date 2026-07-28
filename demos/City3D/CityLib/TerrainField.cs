using Sim.Ingest;

namespace CityLib;

// docs/EXTERNAL-NET-VIEWER-DESIGN.md §7.2: the viewer's ground datum, which used to be a single flat
// number (the net's mid-elevation) shared by every overlay that has no height of its own.
//
// The road network is the only elevation truth the viewer has -- `Lane.ShapeZ` is a real surveyed
// height at every lane vertex. This turns that scattered sample set into a function defined
// everywhere, so "the ground under this (x, y)" has an answer whether or not a road runs through it.
//
// Pure geometry: no Godot type, no Sim.Core type beyond the NetworkModel convenience entry point
// (mirrors RoadMeshBuilder/ZoneGroundBuilder's "CityLib stays engine-agnostic" split).
//
// DETERMINISTIC by construction (§7.2.4): fixed index order everywhere, no Random, no parallelism, no
// dictionary iteration. Baking the same net twice is bitwise identical -- asserted in TerrainFieldTests.
public sealed class TerrainField
{
    /// Lattice spacing before the per-axis cap kicks in. 40 m is finer than the grid's 25 m line
    /// spacing is coarse, and comfortably finer than the scale on which real terrain moves under a
    /// city net; below this the lattice mostly interpolates between the same handful of lane vertices.
    public const double DefaultCellSizeMeters = 40.0;

    /// Hard cap on lattice corners per axis. A Switzerland-sized net (~350 km) at 40 m would be 8750
    /// corners per axis (76 M corners); growing the cell size instead keeps the bake bounded in both
    /// memory and time regardless of net size, at the cost of resolution on the very largest nets.
    public const int MaxCornersPerAxis = 512;

    /// Jacobi passes applied to FILLED corners only (measured corners are pinned), turning the flood
    /// fill's terraces into a smooth surface. Two is enough to remove the visible stepping; more just
    /// flattens the fill towards the global mean.
    private const int RelaxationPasses = 2;

    private readonly double[] _heights; // row-major, (CountX * CountY), corner lattice
    private readonly double _flatHeight;

    private TerrainField(double flatHeight)
    {
        _flatHeight = flatHeight;
        _heights = Array.Empty<double>();
        IsFlat = true;
        MinX = MinY = 0.0;
        CellSize = DefaultCellSizeMeters;
        CountX = CountY = 0;
        MinHeight = MaxHeight = flatHeight;
        MeasuredCorners = 0;
    }

    private TerrainField(double[] heights, double minX, double minY, double cellSize, int countX, int countY, int measured)
    {
        _heights = heights;
        _flatHeight = 0.0;
        IsFlat = false;
        MinX = minX;
        MinY = minY;
        CellSize = cellSize;
        CountX = countX;
        CountY = countY;
        MeasuredCorners = measured;

        var lo = double.PositiveInfinity;
        var hi = double.NegativeInfinity;
        foreach (var h in heights)
        {
            if (h < lo) lo = h;
            if (h > hi) hi = h;
        }

        MinHeight = lo;
        MaxHeight = hi;
    }

    /// The degenerate field: `HeightAt` returns `z` everywhere. This is what a 2-D net bakes to, and
    /// what `SumoGodotFrame.Identity` carries, so every ground overlay on a 2-D net is bit-identical
    /// to what it was before terrain existed.
    public static TerrainField Flat(double z) => new(z);

    /// True only for a `Flat` field. Callers use it to skip subdivision work that would produce the
    /// same planar surface anyway (ZoneGroundBuilder) -- never to decide whether to sample.
    public bool IsFlat { get; }

    public double MinX { get; }
    public double MinY { get; }
    public double CellSize { get; }
    public int CountX { get; }
    public int CountY { get; }

    /// How many lattice corners had at least one lane vertex contribute to them. The rest were filled
    /// (§7.2.1 step 3). Reported at load so a net whose roads cover almost nothing is visible rather
    /// than silently mostly-interpolated.
    public int MeasuredCorners { get; }

    public double MinHeight { get; }
    public double MaxHeight { get; }

    /// Bake from a road network's lane geometry. Lanes with no `ShapeZ` contribute nothing; a net where
    /// NO lane has one bakes to `Flat(0.0)`, which is exactly the pre-terrain behaviour.
    public static TerrainField FromNetwork(NetworkModel network, double cellSize = DefaultCellSizeMeters)
        => FromLaneGeometry(
            network.LanesById.Values.Select(l => (l.Shape, l.ShapeZ)),
            cellSize);

    /// The geometry-only entry point (testable without a NetworkModel). `lanes` is walked in the order
    /// given and each shape in vertex order -- that order is part of the determinism guarantee, so a
    /// caller must not hand over an unordered collection.
    public static TerrainField FromLaneGeometry(
        IEnumerable<(IReadOnlyList<(double X, double Y)> Shape, IReadOnlyList<double>? ShapeZ)> lanes,
        double cellSize = DefaultCellSizeMeters)
    {
        var samples = new List<(double X, double Y, double Z)>();
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var (shape, shapeZ) in lanes)
        {
            if (shapeZ is not { Count: > 0 })
            {
                continue;
            }

            var n = Math.Min(shape.Count, shapeZ.Count);
            for (var i = 0; i < n; i++)
            {
                var (x, y) = shape[i];
                samples.Add((x, y, shapeZ[i]));
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (samples.Count == 0)
        {
            return Flat(0.0);
        }

        // A net whose lanes all sit at one height is flat in fact, not just in form -- bake the
        // degenerate field so it takes the cheap, provably-identical path.
        var firstZ = samples[0].Z;
        if (samples.All(s => s.Z == firstZ))
        {
            return Flat(firstZ);
        }

        var spanX = Math.Max(maxX - minX, 1.0);
        var spanY = Math.Max(maxY - minY, 1.0);
        var step = Math.Max(cellSize, 1e-6);

        // Grow the cell until neither axis exceeds the corner cap (§7.2.1 step 1).
        var needed = Math.Max(spanX, spanY) / step;
        if (needed > MaxCornersPerAxis - 1)
        {
            step = Math.Max(spanX, spanY) / (MaxCornersPerAxis - 1);
        }

        var countX = (int)Math.Ceiling(spanX / step) + 1;
        var countY = (int)Math.Ceiling(spanY / step) + 1;

        var sum = new double[countX * countY];
        var weight = new double[countX * countY];

        // Scatter: bilinear-weighted deposit into the four surrounding corners (§7.2.1 step 2). The
        // transpose of the sampling operator, so a corner ringed by road lands on the road's height
        // rather than on whichever single vertex happened to be nearest.
        foreach (var (x, y, z) in samples)
        {
            var fx = (x - minX) / step;
            var fy = (y - minY) / step;
            var ix = Math.Clamp((int)Math.Floor(fx), 0, countX - 2);
            var iy = Math.Clamp((int)Math.Floor(fy), 0, countY - 2);
            var tx = Math.Clamp(fx - ix, 0.0, 1.0);
            var ty = Math.Clamp(fy - iy, 0.0, 1.0);

            Deposit(sum, weight, countX, ix, iy, (1.0 - tx) * (1.0 - ty), z);
            Deposit(sum, weight, countX, ix + 1, iy, tx * (1.0 - ty), z);
            Deposit(sum, weight, countX, ix, iy + 1, (1.0 - tx) * ty, z);
            Deposit(sum, weight, countX, ix + 1, iy + 1, tx * ty, z);
        }

        var heights = new double[countX * countY];
        var known = new bool[countX * countY];
        var measured = 0;
        for (var i = 0; i < heights.Length; i++)
        {
            if (weight[i] > 0.0)
            {
                heights[i] = sum[i] / weight[i];
                known[i] = true;
                measured++;
            }
        }

        var pinned = (bool[])known.Clone();
        FloodFill(heights, known, countX, countY);
        Relax(heights, pinned, countX, countY);

        return new TerrainField(heights, minX, minY, step, countX, countY, measured);
    }

    private static void Deposit(double[] sum, double[] weight, int countX, int ix, int iy, double w, double z)
    {
        if (w <= 0.0)
        {
            return;
        }

        var k = (iy * countX) + ix;
        sum[k] += w * z;
        weight[k] += w;
    }

    // Deterministic BFS from the measured corners: each newly-reached corner takes the mean of the
    // 4-neighbours that were already known when it was reached. Seeded in raster order and drained
    // FIFO, so the result depends on nothing but the measured set (§7.2.1 step 3, §7.2.4).
    private static void FloodFill(double[] heights, bool[] known, int countX, int countY)
    {
        var queue = new Queue<int>();
        for (var i = 0; i < known.Length; i++)
        {
            if (known[i])
            {
                queue.Enqueue(i);
            }
        }

        if (queue.Count == 0)
        {
            return; // caller guarantees at least one sample, so this cannot happen -- but never loop forever
        }

        var enqueued = (bool[])known.Clone();

        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            var cx = k % countX;
            var cy = k / countX;

            for (var d = 0; d < 4; d++)
            {
                var nx = cx + (d == 0 ? -1 : d == 1 ? 1 : 0);
                var ny = cy + (d == 2 ? -1 : d == 3 ? 1 : 0);
                if (nx < 0 || ny < 0 || nx >= countX || ny >= countY)
                {
                    continue;
                }

                var nk = (ny * countX) + nx;
                if (known[nk])
                {
                    continue;
                }

                heights[nk] = MeanOfKnownNeighbours(heights, known, countX, countY, nx, ny);
                known[nk] = true;
                if (!enqueued[nk])
                {
                    enqueued[nk] = true;
                    queue.Enqueue(nk);
                }
            }
        }
    }

    private static double MeanOfKnownNeighbours(double[] heights, bool[] known, int countX, int countY, int cx, int cy)
    {
        var total = 0.0;
        var n = 0;
        for (var d = 0; d < 4; d++)
        {
            var nx = cx + (d == 0 ? -1 : d == 1 ? 1 : 0);
            var ny = cy + (d == 2 ? -1 : d == 3 ? 1 : 0);
            if (nx < 0 || ny < 0 || nx >= countX || ny >= countY)
            {
                continue;
            }

            var nk = (ny * countX) + nx;
            if (known[nk])
            {
                total += heights[nk];
                n++;
            }
        }

        return n > 0 ? total / n : 0.0;
    }

    // Jacobi smoothing on the FILLED corners only -- measured ones are pinned, so the field still
    // passes exactly through the road heights (§7.2.1 step 4).
    private static void Relax(double[] heights, bool[] pinned, int countX, int countY)
    {
        var next = new double[heights.Length];
        for (var pass = 0; pass < RelaxationPasses; pass++)
        {
            Array.Copy(heights, next, heights.Length);
            for (var cy = 0; cy < countY; cy++)
            {
                for (var cx = 0; cx < countX; cx++)
                {
                    var k = (cy * countX) + cx;
                    if (pinned[k])
                    {
                        continue;
                    }

                    var total = heights[k];
                    var n = 1;
                    for (var d = 0; d < 4; d++)
                    {
                        var nx = cx + (d == 0 ? -1 : d == 1 ? 1 : 0);
                        var ny = cy + (d == 2 ? -1 : d == 3 ? 1 : 0);
                        if (nx < 0 || ny < 0 || nx >= countX || ny >= countY)
                        {
                            continue;
                        }

                        total += heights[(ny * countX) + nx];
                        n++;
                    }

                    next[k] = total / n;
                }
            }

            Array.Copy(next, heights, heights.Length);
        }
    }

    /// The ground height at a SUMO (x, y), bilinear over the containing lattice cell. The query is
    /// clamped to the lattice, so the field is defined -- and continuous -- over the whole plane: a
    /// point outside the net's bbox gets the height of the nearest edge of it, which is the only
    /// honest answer available from road data alone.
    public double HeightAt(double sumoX, double sumoY)
    {
        if (IsFlat)
        {
            return _flatHeight;
        }

        var fx = Math.Clamp((sumoX - MinX) / CellSize, 0.0, CountX - 1.0);
        var fy = Math.Clamp((sumoY - MinY) / CellSize, 0.0, CountY - 1.0);
        var ix = Math.Min((int)Math.Floor(fx), CountX - 2);
        var iy = Math.Min((int)Math.Floor(fy), CountY - 2);
        if (ix < 0) ix = 0;
        if (iy < 0) iy = 0;
        var tx = fx - ix;
        var ty = fy - iy;

        var h00 = _heights[(iy * CountX) + ix];
        var h10 = _heights[(iy * CountX) + ix + 1];
        var h01 = _heights[((iy + 1) * CountX) + ix];
        var h11 = _heights[((iy + 1) * CountX) + ix + 1];

        var a = h00 + ((h10 - h00) * tx);
        var b = h01 + ((h11 - h01) * tx);
        return a + ((b - a) * ty);
    }

    public override string ToString()
        => IsFlat
            ? $"TerrainField(flat z={_flatHeight:F2})"
            : $"TerrainField({CountX}x{CountY} @ {CellSize:F1}m, {MeasuredCorners} measured, z {MinHeight:F1}..{MaxHeight:F1})";
}
