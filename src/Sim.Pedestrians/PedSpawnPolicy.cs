using System;
using System.Collections.Generic;

namespace Sim.Pedestrians;

// P8-2 (docs/PEDESTRIAN-P8-2-APPEARANCE-LEGITIMACY-DESIGN.md; docs/COORDINATION-pedestrian-x-subarea.md §2):
// the pedestrian APPEARANCE-LEGITIMACY gate -- the walkable-edge analogue of Sim.Core.RealismMask. It is an
// IMMUTABLE snapshot answering one question: may a ped legitimately appear or disappear (spawn / despawn /
// end-of-route) on walkable edge `e` WITHOUT the viewer seeing an illegal "pop"?
//
//   MaySpawnOrDespawn(e) = isFringe(e) OR isSink(e) OR isOffCamera(e)
//
// - isFringe(e):    e is a crop-boundary walkable stub (manifest.subarea.fringe_edges with ped=true) -- the
//                   only legitimate on-lane entry/exit into the box (audit_nocheat's fringe definition).
// - isSink(e):      e carries a legitimate internal sink a ped is actually using -- building entrance /
//                   transit stop / parking board-alight (liveliness §6/§8). Empty until P8-3 supplies POIs.
// - isOffCamera(e): e is NOT in the visible walkable-edge set -> the viewer cannot see the pop, so it is
//                   always allowed. This is the direct analogue of RealismMask.MayPop (visible => forbidden).
//
// INERT BY DEFAULT: an empty visible set makes every edge off-camera -> fully permissive, so a null/absent
// policy (or one built with no camera) leaves every existing pedestrian scenario/test/golden unchanged --
// exactly mirroring the engine's null-mask default. The gate only ever CONSTRAINS on-camera edges.
//
// Determinism: this is a pure function of (edge, fringe set, sink set, visible set); the visible set is
// captured once per host tick (same discipline as RealismMask's snapshot). String-keyed like the vehicle
// mask -- consulted only at the low-frequency spawn/despawn moments, so HashSet membership is well within
// budget.
public sealed class PedSpawnPolicy
{
    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal);

    private readonly HashSet<string> _fringe;
    private readonly HashSet<string> _sinks;
    private readonly HashSet<string> _visibleWalkable;

    // `fringeEdgeIds`  = the durable walkable fringe (manifest.subarea, ped=true).
    // `visibleWalkableEdgeIds` = the on-camera walkable edges this host tick (sidewalks / crossings /
    //   walkingAreas in the camera frustum). Empty => no camera => fully permissive (inert).
    // `sinkEdgeIds`    = durable legitimate internal sinks (optional; empty until P8-3 POIs).
    public PedSpawnPolicy(
        IReadOnlyCollection<string> fringeEdgeIds,
        IReadOnlyCollection<string> visibleWalkableEdgeIds,
        IReadOnlyCollection<string>? sinkEdgeIds = null)
    {
        if (fringeEdgeIds is null)
        {
            throw new ArgumentNullException(nameof(fringeEdgeIds));
        }

        if (visibleWalkableEdgeIds is null)
        {
            throw new ArgumentNullException(nameof(visibleWalkableEdgeIds));
        }

        _fringe = fringeEdgeIds.Count > 0 ? new HashSet<string>(fringeEdgeIds, StringComparer.Ordinal) : Empty;
        _visibleWalkable = visibleWalkableEdgeIds.Count > 0
            ? new HashSet<string>(visibleWalkableEdgeIds, StringComparer.Ordinal)
            : Empty;
        _sinks = sinkEdgeIds is { Count: > 0 } ? new HashSet<string>(sinkEdgeIds, StringComparer.Ordinal) : Empty;
    }

    // The inert, fully-permissive policy (no camera): every edge is off-camera, so every appearance is
    // legitimate. Semantically identical to having no policy at all -- the default consumers fall back to.
    public static PedSpawnPolicy Permissive { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    // True iff a ped may legitimately appear/disappear on `edgeId` without a visible pop: it is a fringe
    // stub, a legitimate sink, or off-camera. An on-camera edge that is neither fringe nor sink returns
    // false -- the caller must instead route to the nearest fringe/sink or hold the ped until it walks
    // off-camera (see the design doc's deferral rule).
    public bool MaySpawnOrDespawn(string edgeId)
    {
        if (edgeId is null)
        {
            throw new ArgumentNullException(nameof(edgeId));
        }

        // Off-camera is always legitimate (the viewer can't see it); otherwise it must be fringe or sink.
        return !_visibleWalkable.Contains(edgeId) || _fringe.Contains(edgeId) || _sinks.Contains(edgeId);
    }

    // Diagnostics / test introspection.
    public bool IsFringe(string edgeId) => _fringe.Contains(edgeId);

    public bool IsSink(string edgeId) => _sinks.Contains(edgeId);

    public bool IsOnCamera(string edgeId) => _visibleWalkable.Contains(edgeId);
}
