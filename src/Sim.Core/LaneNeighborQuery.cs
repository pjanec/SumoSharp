namespace Sim.Core;

// Seam 1 (DESIGN.md "The four seams" -- "neighbor discovery behind an interface"): car-following
// consumes leaders through ONE query rather than scattered getLeader() calls, so laneless
// (phase 2) can swap in a fixed-radius spatial-hash query returning multiple shadow-lane
// leaders without touching the constraint-reducer call sites. Phase 1 walks the lane's sorted
// vehicle list and returns a single leader (ported from sumo/src/microsim/MSLane.cpp's
// getLeader, same-lane branch, lines ~2796-2843).
//
// Plan/execute contract (DESIGN.md): built ONCE per step from START-OF-STEP kinematics, before
// PlanMovements runs, and never mutated afterward -- every vehicle's plan phase reads this same
// frozen snapshot, so a follower never sees its leader's updated-this-step position.
//
// D2 (FastDataPlane readiness): the per-lane buckets are keyed by the dense int LaneHandle
// (Sim.Ingest.Lane.Handle / VehicleRuntime.LaneHandle) rather than the LaneId string -- this is
// the hottest string-keyed structure in the step loop (built TWICE per step: PlanMovements'
// pre-move snapshot and DecideSpeedGainChanges' post-move snapshot), so replacing the
// `Dictionary<string,List<>>` hash+compare with a plain array index removes a per-vehicle string
// hash from the hottest per-step path. Buckets are still allocated fresh every Build call (NOT
// yet reused across steps -- that reuse is D4's job; D2 only switches the key type).
internal sealed class LaneNeighborQuery
{
    private readonly List<VehicleRuntime>?[] _byLaneHandle;

    private LaneNeighborQuery(int laneCount)
    {
        _byLaneHandle = new List<VehicleRuntime>?[laneCount];
    }

    // `laneCount` is `network.LanesByHandle.Count` -- the size of the dense handle space, so
    // every lane's bucket is a plain array slot rather than a hashed dictionary entry.
    public static LaneNeighborQuery Build(IEnumerable<VehicleRuntime> vehicles, int laneCount)
    {
        var query = new LaneNeighborQuery(laneCount);

        foreach (var v in vehicles)
        {
            if (!v.Inserted || v.Arrived)
            {
                continue;
            }

            var list = query._byLaneHandle[v.LaneHandle];
            if (list is null)
            {
                list = new List<VehicleRuntime>();
                query._byLaneHandle[v.LaneHandle] = list;
            }

            list.Add(v);
        }

        foreach (var list in query._byLaneHandle)
        {
            list?.Sort((a, b) => a.Kinematics.Pos.CompareTo(b.Kinematics.Pos));
        }

        return query;
    }

    // MSLane::getLeader (same-lane branch): the nearest inserted, not-arrived vehicle strictly
    // ahead of ego (pos > egoPos) on ego's own lane, or null if none -- O(log n) via binary
    // search over the lane's pos-sorted list (the "sorted lane list" DESIGN.md's seam 1 calls
    // for), then a short linear scan past any pos ties to skip ego itself and any co-located
    // vehicle. Cross-lane/consecutive-lane lookup (MSLane.cpp's getLeaderOnConsecutive, for when
    // no leader exists on the current lane) is out of scope until a multi-edge-route scenario
    // needs it -- rung 4 is single-lane.
    public VehicleRuntime? GetLeader(VehicleRuntime ego) => GetLeaderOnLane(ego, ego.LaneHandle);

    // Rung A2 (speed-gain lane change): the same "nearest ahead" lookup as GetLeader, but
    // against an ADJACENT lane's pos-sorted list rather than ego's own -- ported from the
    // neighLead half of MSLCM_LC2013::_wantsChange's getLeader/getFollower pair for the
    // considered target lane (MSLane.cpp's getLeader, same-lane branch, applied to
    // `neighborLaneHandle` instead of ego.LaneHandle). Ego is never present in an adjacent
    // lane's list under this engine's discrete (lanechange.duration=0) lane model, but the
    // ReferenceEquals skip is kept for symmetry with GetLeader and future robustness. Null when
    // the neighbor lane has no vehicles, or none strictly ahead of ego's position.
    public VehicleRuntime? GetNeighborLeader(VehicleRuntime ego, int neighborLaneHandle) =>
        GetLeaderOnLane(ego, neighborLaneHandle);

    private VehicleRuntime? GetLeaderOnLane(VehicleRuntime ego, int laneHandle)
    {
        var list = _byLaneHandle[laneHandle];
        if (list is null)
        {
            return null;
        }

        var egoPos = ego.Kinematics.Pos;

        // Manual binary search for the first element with Pos > egoPos (upper bound), over the
        // lane's pos-sorted list -- the O(log n) "sorted lane list" lookup DESIGN.md's seam 1
        // calls for. Then a short linear scan past any pos ties to skip ego itself (or any other
        // co-located vehicle) and reach the actual nearest leader.
        var lo = 0;
        var hi = list.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (list[mid].Kinematics.Pos <= egoPos)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (var index = lo; index < list.Count; index++)
        {
            if (!ReferenceEquals(list[index], ego))
            {
                return list[index];
            }
        }

        return null;
    }

    // Rung A2: the nearest vehicle strictly BEHIND ego's position (Pos < egoPos) on the given
    // adjacent lane -- ported from the neighFollow half of the same MSLCM_LC2013 lookup pair,
    // needed by the target-lane safety veto (A2-iii). Null when the neighbor lane has no
    // vehicles, or none strictly behind ego's position.
    public VehicleRuntime? GetNeighborFollower(VehicleRuntime ego, int neighborLaneHandle)
    {
        var list = _byLaneHandle[neighborLaneHandle];
        if (list is null)
        {
            return null;
        }

        var egoPos = ego.Kinematics.Pos;

        // First index with Pos >= egoPos (lower bound); the nearest follower is just before it.
        var lo = 0;
        var hi = list.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (list[mid].Kinematics.Pos < egoPos)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (var index = lo - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(list[index], ego))
            {
                return list[index];
            }
        }

        return null;
    }
}
