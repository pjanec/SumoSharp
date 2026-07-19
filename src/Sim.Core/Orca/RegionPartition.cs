using System;
using System.Collections.Generic;

namespace Sim.Core.Orca;

// P6-2-1 (docs/PEDESTRIAN-P6-2-REGION-DESIGN.md §3): partitions active agents into a uniform grid of spatial
// REGIONS by frozen position -- the unit of cache-local parallel work that P6-2 will hand OrcaCrowd.Step
// (each worker sweeps one region whose agents + halo stay cache-resident, instead of every worker touching
// the whole SoA).
//
// This class is a PURE partitioning data structure: it groups agent indices, it does not touch velocities,
// neighbours, or any solve, so it is PARITY-INERT (nothing here is reachable from a committed golden). The
// region-local neighbour gather (with a NeighbourDist halo) is P6-2-2; the parallel dispatch is P6-2-3.
//
// Deterministic by construction: agents are iterated in index order, so every region's agent list comes out
// ASCENDING by index (the order the existing spatial hash / brute-force gather already relies on); regions
// are assigned in first-seen order. Same input -> identical partition, independent of anything external.
//
// RegionSize is a caller-chosen multiple of NeighbourDist: a whole region spans many neighbour cells, so the
// per-region halo band (P6-2-2) is a small fraction of the region's working set. Re-Build()ing each Step from
// the frozen positions handles agent movement for free -- an agent that crosses a region boundary simply
// lands in the new region next Step (the vehicle --region "free hand-off" pattern).
public sealed class RegionPartition
{
    private readonly Dictionary<long, int> _cellToRegion = new();
    private int[][] _regionAgents = Array.Empty<int[]>();
    private int[] _regionFill = Array.Empty<int>();
    private int _regionCount;

    // The region cell size used by the most recent Build (a multiple of NeighbourDist).
    public double RegionSize { get; private set; }

    // Number of non-empty regions produced by the most recent Build.
    public int RegionCount => _regionCount;

    // Bucket the alive agents in positions[0..count) into regions of side `regionSize`. `alive[i]` is the
    // caller's include mask (typically active && slotAlive). Reuses pooled per-region arrays across calls;
    // only growth allocates.
    public void Build(ReadOnlySpan<Vec2> positions, ReadOnlySpan<bool> alive, int count, double regionSize)
    {
        if (regionSize <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(regionSize), "region size must be positive.");
        }

        if (count > positions.Length || count > alive.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "count exceeds the positions/alive spans.");
        }

        RegionSize = regionSize;
        _cellToRegion.Clear();
        _regionCount = 0;

        for (var i = 0; i < count; i++)
        {
            if (!alive[i])
            {
                continue;
            }

            var key = CellKey(positions[i], regionSize);
            if (!_cellToRegion.TryGetValue(key, out var r))
            {
                r = _regionCount++;
                EnsureRegion(r);
                _regionFill[r] = 0;
                _cellToRegion[key] = r;
            }

            var arr = _regionAgents[r];
            var f = _regionFill[r];
            if (f == arr.Length)
            {
                Array.Resize(ref arr, arr.Length * 2);
                _regionAgents[r] = arr;
            }

            arr[f] = i;
            _regionFill[r] = f + 1;
        }
    }

    // The ascending-by-index agent list of region `region` (0 <= region < RegionCount).
    public ReadOnlySpan<int> AgentsInRegion(int region)
    {
        if ((uint)region >= (uint)_regionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        return _regionAgents[region].AsSpan(0, _regionFill[region]);
    }

    // The region index that a point `p` maps to under the most recent Build's grid, or -1 if no alive agent
    // occupied that region cell this Build. (An oracle/inspection helper; the solve uses AgentsInRegion.)
    public int RegionOf(Vec2 p) =>
        _cellToRegion.TryGetValue(CellKey(p, RegionSize), out var r) ? r : -1;

    private void EnsureRegion(int r)
    {
        if (r < _regionAgents.Length)
        {
            return;
        }

        var newLen = Math.Max(r + 1, Math.Max(4, _regionAgents.Length * 2));

        var newAgents = new int[newLen][];
        Array.Copy(_regionAgents, newAgents, _regionAgents.Length);
        for (var k = _regionAgents.Length; k < newLen; k++)
        {
            newAgents[k] = new int[8];
        }

        _regionAgents = newAgents;

        var newFill = new int[newLen];
        Array.Copy(_regionFill, newFill, _regionFill.Length);
        _regionFill = newFill;
    }

    // Deterministic cell keying -- identical math to OrcaCrowd's agent grid (FloorDiv + 32-bit cell pack), so
    // a region cell is a clean super-cell of the neighbour grid when regionSize is a multiple of NeighbourDist.
    private static long CellKey(Vec2 p, double size) => PackCell(FloorDiv(p.X, size), FloorDiv(p.Y, size));

    private static int FloorDiv(double v, double cell) => (int)Math.Floor(v / cell);

    private static long PackCell(int cx, int cy) => ((long)cx << 32) | (uint)cy;
}
