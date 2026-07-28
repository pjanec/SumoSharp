using System;
using System.Buffers;
using System.Collections.Generic;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;

namespace Sim.Pedestrians.Crossing;

// Deterministic per-crossing occupancy footprint source (docs/LIVE-CITY-CROSSING-YIELD-DESIGN.md, Phase 2).
// Makes cars yield to LOW-POWER (un-promoted) pedestrians standing on a crosswalk WITHOUT promoting them:
// each tick the ped side calls Update(...) with the low-power ped positions; a ped inside a crossing
// polygon becomes a virtual "closed-gate" WorldDisc, and the engine's CrowdSource query
// (CrowdLongitudinalConstraint) brakes the approaching car for it -- the same seam a promoted ped uses.
//
// Cost split (the whole point):
//   * Update() is O(low-power peds) and runs ONCE per tick on the PED side. A cheap per-crossing bbox
//     pre-filter means each ped effectively tests the one crossing it might be on. No car involved.
//   * QueryNear() is what a VEHICLE pays: an empty fast-path when nothing is occupied, else a walk of the
//     small currently-occupied set. It does NOT recompute occupancy. So adding this source cannot make
//     the per-vehicle step meaningfully slower, and it is never queried at all when Engine.CrowdSource is
//     null (every committed golden) -> parity-inert, zero cost there.
//
// Velocity is 0 (a stopped gate) so the car predicts the ped stays and brakes to a stop; because Update
// refreshes the disc to the ped's current position every tick, the gate tracks the crossing ped and
// clears the moment it steps off. Deterministic: occupancy is a pure function of the (pure-function-of-
// time) low-power poses; no RNG.
public sealed class CrossingOccupancySource : ICrowdFootprintSource
{
    private readonly struct CrossingPoly
    {
        public readonly Vec2[] Verts;
        public readonly double MinX;
        public readonly double MinY;
        public readonly double MaxX;
        public readonly double MaxY;

        public CrossingPoly(Vec2[] verts)
        {
            Verts = verts;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var v in verts)
            {
                if (v.X < minX) minX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.X > maxX) maxX = v.X;
                if (v.Y > maxY) maxY = v.Y;
            }

            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }
    }

    private readonly CrossingPoly[] _crossings;
    private readonly double _pedRadius;
    private WorldDisc[] _occupied = new WorldDisc[32];
    private int _occupiedCount;

    // ---- Spatial index over `_occupied` (rebuilt every Update -- mirrors OrcaCrowd.RebuildGrid /
    // OrcaCrowd.GridCandidates, ~OrcaCrowd.cs:950-1030) ----
    //
    // Cell size is a fixed constant, NOT sized from a query radius the way InterestField sizes its
    // (single-cell-lookup) grid from the largest DemoteRadius: InterestField can get away with a
    // single-cell query because every one of ITS queries reuses the same fixed radius family
    // (Promote/DemoteRadius). Here QueryNear's `radius` is a PER-CALL argument that ranges from 0.01 m
    // (the yield-metric probe) to tens of metres (Engine.cs's crowd-brake radius, ~speed*3+len+5), so
    // no single fixed cell size could ever bound it to "just this cell". Instead this follows
    // OrcaCrowd.GatherObstacleCandidates' generalisation (~OrcaCrowd.cs:1118-1174): expand the search
    // to a `ring` of cells sized from the ACTUAL (radius + disc-radius) reach of THIS call, so
    // correctness never depends on the constant below -- only performance does. 5.0 m is a rough
    // crossing-footprint scale (keeps simultaneous occupants of one crossing in the same/adjacent
    // cell); it can be retuned freely without touching correctness.
    private const double CellSize = 5.0;
    private readonly Dictionary<long, int> _cellToBucket = new();
    private int[][] _bucketDiscs = Array.Empty<int[]>();
    private int[] _bucketFill = Array.Empty<int>();
    private int _bucketCount;

    public CrossingOccupancySource(IEnumerable<BakedPolygon> polygons, double pedRadius = 0.3)
    {
        var list = new List<CrossingPoly>();
        foreach (var p in polygons)
        {
            if (p.Kind != BakedPolygonKind.Crossing || p.Vertices.Count < 3)
            {
                continue;
            }

            var verts = new Vec2[p.Vertices.Count];
            for (var i = 0; i < verts.Length; i++)
            {
                verts[i] = p.Vertices[i];
            }

            list.Add(new CrossingPoly(verts));
        }

        _crossings = list.ToArray();
        _pedRadius = pedRadius;
    }

    // How many crossings this source watches (diagnostic).
    public int CrossingCount => _crossings.Length;

    // How many virtual gate discs the last Update produced (diagnostic: crossings currently occupied).
    public int OccupiedCount => _occupiedCount;

    // Diagnostic: how many peds passed a crossing's bbox in the last Update (before the point-in-polygon
    // test). If this is >0 but OccupiedCount is 0, the polygon test (not the bbox / geometry) is the miss.
    public int LastBboxHits { get; private set; }

    // Recompute the occupied-crossing gate discs from this tick's LOW-POWER pedestrian positions. Call
    // ONCE per tick, before Engine.Step(), so the vehicle's CrowdSource query sees the current gates.
    public void Update(IReadOnlyList<Vec2> lowPowerPedPositions)
    {
        _occupiedCount = 0;
        LastBboxHits = 0;
        if (_crossings.Length != 0)
        {
            for (var pi = 0; pi < lowPowerPedPositions.Count; pi++)
            {
                var p = lowPowerPedPositions[pi];
                for (var ci = 0; ci < _crossings.Length; ci++)
                {
                    ref readonly var c = ref _crossings[ci];
                    if (p.X < c.MinX || p.X > c.MaxX || p.Y < c.MinY || p.Y > c.MaxY)
                    {
                        continue; // cheap bbox reject -- a ped is inside at most one crossing's box
                    }

                    LastBboxHits++;
                    if (!PointInPolygon(p, c.Verts))
                    {
                        continue;
                    }

                    if (_occupiedCount >= _occupied.Length)
                    {
                        Array.Resize(ref _occupied, _occupied.Length * 2);
                    }

                    _occupied[_occupiedCount++] = new WorldDisc(p.X, p.Y, 0.0, 0.0, _pedRadius);
                    break; // this ped is accounted for; move to the next ped
                }
            }
        }

        RebuildGrid();
    }

    // Rebuilds the uniform grid from `_occupied[0.._occupiedCount)` -- called once per Update, exactly
    // like OrcaCrowd.RebuildGrid is called once per OrcaCrowd.Step, so every QueryNear this tick sees
    // the same frozen index. Each disc is inserted by its centre into exactly ONE cell (discs are
    // points, not segments spanning several cells like OrcaCrowd's obstacle grid) -- that single-cell
    // membership is what lets QueryNear restore visit order with a plain sort, no de-dup (see
    // QueryNear remarks).
    private void RebuildGrid()
    {
        _cellToBucket.Clear();
        _bucketCount = 0;
        for (var i = 0; i < _occupiedCount; i++)
        {
            var d = _occupied[i];
            var key = CellKey(d.X, d.Y);
            if (!_cellToBucket.TryGetValue(key, out var bi))
            {
                bi = _bucketCount++;
                EnsureBucket(bi);
                _bucketFill[bi] = 0;
                _cellToBucket[key] = bi;
            }

            var arr = _bucketDiscs[bi];
            var f = _bucketFill[bi];
            if (f == arr.Length)
            {
                Array.Resize(ref arr, arr.Length * 2);
                _bucketDiscs[bi] = arr;
            }

            arr[f] = i; // store the ORIGINAL _occupied index, not the disc value -- QueryNear needs it
                        // both to re-fetch the disc and to restore ascending visit order via sort.
            _bucketFill[bi] = f + 1;
        }
    }

    private void EnsureBucket(int bi)
    {
        if (_bucketDiscs.Length <= bi)
        {
            var newLen = Math.Max(bi + 1, Math.Max(8, _bucketDiscs.Length * 2));
            Array.Resize(ref _bucketDiscs, newLen);
            Array.Resize(ref _bucketFill, newLen);
        }

        _bucketDiscs[bi] ??= new int[8];
    }

    private static int FloorDiv(double v, double cell) => (int)Math.Floor(v / cell);

    private static long PackCell(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    private static long CellKey(double x, double y) => PackCell(FloorDiv(x, CellSize), FloorDiv(y, CellSize));

    public int QueryNear(double x, double y, double radius, Span<WorldDisc> into)
    {
        if (_occupiedCount == 0)
        {
            return 0; // fast path: nothing is on a crossing -> the vehicle pays ~nothing
        }

        // Every occupied disc shares the SAME fixed `_pedRadius` (see Update, above), so `radius +
        // _pedRadius` is the exact (not approximate) worst-case reach of the per-disc test below --
        // mirrors InterestField.RebuildIndex sizing its search from the largest DemoteRadius, except
        // here it bounds a per-call RING (OrcaCrowd.GatherObstacleCandidates' scheme) rather than a
        // fixed cell, because `radius` varies per call (see the CellSize field comment).
        var range = radius + _pedRadius;
        var ring = Math.Max(1, (int)Math.Ceiling(range / CellSize));
        var cx = FloorDiv(x, CellSize);
        var cy = FloorDiv(y, CellSize);

        // Rented, not a shared instance field: QueryNear is a read-only query on the ICrowdFootprintSource
        // contract, and Engine.cs calls it from ComputeMoveIntent, which Engine.UseParallelPlan (auto-on
        // above ParallelPlanThreshold=256 vehicles -- i.e. ON for the very mega-scenario this change
        // targets) runs concurrently across Parallel.For workers. A shared mutable scratch array would
        // race under that path exactly the way OrcaCrowd's per-worker ScratchSet exists to prevent
        // (OrcaCrowd.cs:574-601) -- but QueryNear's signature (fixed by ICrowdFootprintSource, shared with
        // OrcaCrowd/VehicleMover/CompositeFootprintSource) has no room to thread a caller-owned scratch
        // through. ArrayPool<int>.Shared is the thread-safe pooled equivalent: no heap growth in steady
        // state, safe to Rent/Return from any thread.
        var candidateBuffer = ArrayPool<int>.Shared.Rent(_occupiedCount);
        try
        {
            var n = 0;
            for (var dx = -ring; dx <= ring; dx++)
            {
                for (var dy = -ring; dy <= ring; dy++)
                {
                    if (!_cellToBucket.TryGetValue(PackCell(cx + dx, cy + dy), out var bi))
                    {
                        continue;
                    }

                    var fill = _bucketFill[bi];
                    var arr = _bucketDiscs[bi];
                    for (var t = 0; t < fill; t++)
                    {
                        candidateBuffer[n++] = arr[t];
                    }
                }
            }

            // Restores the brute-force scan's ascending-index visit order. This is load-bearing, not
            // cosmetic: WorldDiscQuery.InsertNearest breaks a distance TIE by keeping the INCUMBENT (its
            // guard is `>=`, not `>` -- see WorldDiscQuery's own remarks), so a different visit order can
            // silently change which disc survives truncation when `into` is full. A plain ascending sort
            // (no de-dup needed) suffices here -- unlike OrcaCrowd.GatherObstacleCandidates -- because
            // RebuildGrid puts each disc in exactly ONE cell (a point), never several (a segment).
            Array.Sort(candidateBuffer, 0, n);

            // Keeps the NEAREST occupied-crossing discs when more are in range than fit -- see
            // ICrowdFootprintSource.QueryNear's contract. (No early exit once `into` is full: a later,
            // closer crossing must still displace an earlier, farther one.)
            var result = 0;
            for (var k = 0; k < n; k++)
            {
                var d = _occupied[candidateBuffer[k]];
                var rr = radius + d.Radius;
                var ddx = d.X - x;
                var ddy = d.Y - y;
                if (ddx * ddx + ddy * ddy <= rr * rr)
                {
                    result = Sim.Core.Bridge.WorldDiscQuery.InsertNearest(into, result, d, x, y);
                }
            }

            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(candidateBuffer);
        }
    }

    // Standard ray-casting point-in-polygon over the implicitly-closed ring (crossing polygons are small
    // convex quads, so this is a few crossings' worth of cheap work).
    private static bool PointInPolygon(Vec2 p, Vec2[] v)
    {
        var inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            if (((v[i].Y > p.Y) != (v[j].Y > p.Y)) &&
                (p.X < (v[j].X - v[i].X) * (p.Y - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X))
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
