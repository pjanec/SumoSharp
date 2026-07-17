using System;
using System.Collections.Generic;

namespace Sim.Core;

// X1 (docs/HIGH-DENSITY-X1-DESIGN.md): the attention-aware realism mask -- an IMMUTABLE snapshot of
// which edges are in the high-realism (visible / on-camera) zone, where "popping" is forbidden. The
// host builds a fresh mask from the camera frustum's visible edge set and publishes it to the engine
// via Engine.SetVisibleEdges (a lock-free volatile reference swap); the engine captures it ONCE per
// step so it cannot change mid-step. A null mask (the default) means "no camera" -> every edge is
// off-camera -> fully permissive, so the whole feature is inert and every parity golden is unchanged.
//
// Edges are string-keyed in the network (no dense edge handle), and the gates that read this mask
// (jam teleport, on-lane insertion, off-camera de-jam despawn) are LOW-FREQUENCY serial phases that
// touch only jammed / departing vehicles -- not every vehicle every step -- so a HashSet<string>
// membership test per gated candidate is well within budget.
public sealed class RealismMask
{
    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal);

    private readonly HashSet<string> _teleportForbidden;
    private readonly HashSet<string> _popForbidden;

    // `visibleEdgeIds` = the on-camera / high-realism edges. `forbidTeleport`/`forbidPop` say which
    // cheating actions the visible zone forbids (both true by default: the visible zone is strict
    // no-cheating). A visible edge with the corresponding flag set is forbidden; every other edge is
    // permissive.
    public RealismMask(IReadOnlyCollection<string> visibleEdgeIds, bool forbidTeleport = true, bool forbidPop = true)
    {
        if (visibleEdgeIds is null)
        {
            throw new ArgumentNullException(nameof(visibleEdgeIds));
        }

        _teleportForbidden = forbidTeleport && visibleEdgeIds.Count > 0
            ? new HashSet<string>(visibleEdgeIds, StringComparer.Ordinal)
            : Empty;
        _popForbidden = forbidPop && visibleEdgeIds.Count > 0
            ? new HashSet<string>(visibleEdgeIds, StringComparer.Ordinal)
            : Empty;
    }

    // True iff a jam-teleport is allowed on this edge (i.e. the edge is NOT a teleport-forbidden
    // visible edge). Off-camera / permissive edges always return true.
    public bool MayTeleport(string edgeId) => !_teleportForbidden.Contains(edgeId);

    // True iff on-lane popping (spawn / de-jam despawn) is allowed on this edge (i.e. the edge is NOT
    // a pop-forbidden visible edge). Off-camera / permissive edges always return true.
    public bool MayPop(string edgeId) => !_popForbidden.Contains(edgeId);
}
