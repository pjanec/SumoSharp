using System;
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
        if (_crossings.Length == 0)
        {
            return;
        }

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

    public int QueryNear(double x, double y, double radius, Span<WorldDisc> into)
    {
        if (_occupiedCount == 0)
        {
            return 0; // fast path: nothing is on a crossing -> the vehicle pays ~nothing
        }

        var n = 0;
        for (var i = 0; i < _occupiedCount; i++)
        {
            var d = _occupied[i];
            var rr = radius + d.Radius;
            var dx = d.X - x;
            var dy = d.Y - y;
            if (dx * dx + dy * dy <= rr * rr)
            {
                if (n >= into.Length)
                {
                    break;
                }

                into[n++] = d;
            }
        }

        return n;
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
