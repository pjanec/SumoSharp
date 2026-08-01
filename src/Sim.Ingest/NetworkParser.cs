using System.Globalization;
using System.Xml.Linq;

namespace Sim.Ingest;

// Parses the rung-1 subset of SUMO's post-netconvert .net.xml: <edge> containing one or more
// <lane>, plus (rung 9a) internal (junction-interior) edges/lanes and top-level <connection>
// elements. Tolerant of missing optional attributes (documented defaults below); required
// attributes throw a clear error rather than silently defaulting, since a missing id/shape
// signals a parser-subset gap, not a legitimate omission.
public static class NetworkParser
{
    // sumo/src/utils/common/StdDefs.h:48 -- #define SUMO_const_laneWidth 3.2. Default lane
    // width when a <lane>'s `width` attribute is absent (rung 9b-ii).
    private const double SumoConstLaneWidth = 3.2;

    public static NetworkModel Parse(string path)
    {
        using var stream = File.OpenRead(path);
        return ParseDocument(XDocument.Load(stream));
    }

    public static NetworkModel ParseXml(string xml) => ParseDocument(XDocument.Parse(xml));

    private static NetworkModel ParseDocument(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("net.xml has no root element.");

        var edges = new List<Edge>();
        var edgesById = new Dictionary<string, Edge>();
        var lanesById = new Dictionary<string, Lane>();

        // D2: the global dense lane-handle assignment -- every lane (including internal `:`
        // lanes) gets the next sequential handle in PARSE order, so LanesByHandle[h] == the
        // lane whose Handle is h. nextLaneHandle is shared across every <edge>'s <lane> loop
        // below (it is NOT reset per edge).
        var nextLaneHandle = 0;
        var lanesByHandle = new List<Lane>();

        foreach (var edgeEl in root.Elements("edge"))
        {
            // Rung 9a: internal (junction-interior) edges are now parsed too -- a multi-edge
            // route's lane sequence passes through them (e.g. ":J_2_0"). They have no from/to
            // (tolerated: From/To default to "" below, same as any edge missing the attribute).
            var edgeId = RequireAttribute(edgeEl, "id");
            var from = edgeEl.Attribute("from")?.Value ?? string.Empty;
            var to = edgeEl.Attribute("to")?.Value ?? string.Empty;

            var lanes = new List<Lane>();
            foreach (var laneEl in edgeEl.Elements("lane"))
            {
                // Rung 9b-ii: `width` defaults to SUMO_const_laneWidth (3.2, StdDefs.h:48) when
                // absent -- this net's <lane> elements never specify it.
                var width = laneEl.Attribute("width") is { } widthAttr
                    ? double.Parse(widthAttr.Value, CultureInfo.InvariantCulture)
                    : SumoConstLaneWidth;

                var lane = new Lane(
                    Id: RequireAttribute(laneEl, "id"),
                    EdgeId: edgeId,
                    Index: int.Parse(RequireAttribute(laneEl, "index"), CultureInfo.InvariantCulture),
                    Speed: double.Parse(RequireAttribute(laneEl, "speed"), CultureInfo.InvariantCulture),
                    Length: double.Parse(RequireAttribute(laneEl, "length"), CultureInfo.InvariantCulture),
                    Shape: ParseShape(RequireAttribute(laneEl, "shape")),
                    Width: width,
                    Handle: nextLaneHandle++,
                    ShapeZ: ParseShapeZ(RequireAttribute(laneEl, "shape")),
                    AllowsRoadVehicle: LaneAllowsRoadVehicle(laneEl.Attribute("allow")?.Value));

                lanes.Add(lane);
                lanesById[lane.Id] = lane;
                lanesByHandle.Add(lane);
            }

            // D4: precompute each lane's same-edge left/right neighbor HANDLE (Index+1/Index-1)
            // once here, at ingest -- cold path (parse time, O(n^2) over one edge's small lane
            // count), so the per-step keep-right/speed-gain decision (Engine.cs) never has to
            // scan `edge.Lanes` itself. `lanesById`/`lanesByHandle` are updated in place (via
            // `with`) since `Lane` is immutable and neighbor handles are only knowable once every
            // lane on this edge has already been assigned its own Handle above.
            for (var i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var leftHandle = -1;
                var rightHandle = -1;
                foreach (var sibling in lanes)
                {
                    // A road vehicle never changes onto a non-vehicular (pedestrian-only) lane -- MSLane
                    // forbids it. Excluding such siblings here means keep-right / lateral placement (which
                    // read Left/RightNeighbor) can't move a car onto a sidewalk on a [sidewalk, car] edge.
                    // Inert on a pure-car net (every golden): AllowsRoadVehicle is true for every lane there.
                    if (!sibling.AllowsRoadVehicle)
                    {
                        continue;
                    }

                    if (sibling.Index == lane.Index + 1)
                    {
                        leftHandle = sibling.Handle;
                    }
                    else if (sibling.Index == lane.Index - 1)
                    {
                        rightHandle = sibling.Handle;
                    }
                }

                if (leftHandle != -1 || rightHandle != -1)
                {
                    var updated = lane with { LeftNeighbor = leftHandle, RightNeighbor = rightHandle };
                    lanes[i] = updated;
                    lanesById[updated.Id] = updated;
                    lanesByHandle[updated.Handle] = updated;
                }
            }

            // R3 (rail bidi): netconvert marks a shared-track rail edge pair with `bidi="<other>"`
            // on each edge. Parsed here (null when absent, i.e. every road edge) so the engine's
            // rail insertion check can tell whether this edge's track is shared with an opposing one.
            var bidi = edgeEl.Attribute("bidi")?.Value;
            var edge = new Edge(edgeId, from, to, lanes, bidi);
            edges.Add(edge);
            edgesById[edgeId] = edge;
        }

        var connections = new List<Connection>();
        var connectionsByFromLaneTo = new Dictionary<(string, int, string), Connection>();
        var connectionsByFromEdgeLane = new Dictionary<(string, int), List<Connection>>();

        foreach (var connEl in root.Elements("connection"))
        {
            // A <connection>'s from/to are always present in netconvert output; fromLane/toLane
            // default to "0" only for parser robustness (every connection in scope for this
            // rung specifies them explicitly). `via` (the internal lane traversed at a
            // junction) is absent for connections that cross no junction interior. Rung 10:
            // `tl`/`linkIndex` are present together only on connections controlled by a
            // <tlLogic> (e.g. this scenario's WJ->JE connection); absent (null) otherwise.
            var from = RequireAttribute(connEl, "from");
            var to = RequireAttribute(connEl, "to");
            var fromLane = int.Parse(connEl.Attribute("fromLane")?.Value ?? "0", CultureInfo.InvariantCulture);
            var toLane = int.Parse(connEl.Attribute("toLane")?.Value ?? "0", CultureInfo.InvariantCulture);
            var via = connEl.Attribute("via")?.Value;
            var tl = connEl.Attribute("tl")?.Value;
            var linkIndex = connEl.Attribute("linkIndex") is { } linkIndexAttr
                ? int.Parse(linkIndexAttr.Value, CultureInfo.InvariantCulture)
                : (int?)null;
            var state = connEl.Attribute("state")?.Value;

            var connection = new Connection(from, fromLane, to, toLane, via, tl, linkIndex, state);
            connections.Add(connection);
            // Last-wins on a duplicate key is a non-issue for this rung's straight-through,
            // single-connection-per-(fromEdge,fromLane,toEdge) network.
            connectionsByFromLaneTo[(from, fromLane, to)] = connection;

            if (!connectionsByFromEdgeLane.TryGetValue((from, fromLane), out var list))
            {
                list = new List<Connection>();
                connectionsByFromEdgeLane[(from, fromLane)] = list;
            }

            list.Add(connection);
        }

        var tlLogicsById = new Dictionary<string, TlLogic>();
        foreach (var tlLogicEl in root.Elements("tlLogic"))
        {
            var id = RequireAttribute(tlLogicEl, "id");
            var offset = double.Parse(tlLogicEl.Attribute("offset")?.Value ?? "0", CultureInfo.InvariantCulture);
            // C6-ii: default "static" when absent (netconvert always emits type, but hand-written
            // .tll.xml may omit it). Only "static" and "actuated" are handled downstream.
            var type = tlLogicEl.Attribute("type")?.Value ?? "static";

            var phases = new List<TlPhase>();
            foreach (var phaseEl in tlLogicEl.Elements("phase"))
            {
                var duration = double.Parse(RequireAttribute(phaseEl, "duration"), CultureInfo.InvariantCulture);
                var state = RequireAttribute(phaseEl, "state");
                // C6-ii: per-phase actuated bounds (absent for static programs and for the fixed
                // yellow/all-red phases of an actuated program -- left null, resolved to `duration`).
                var minDurAttr = phaseEl.Attribute("minDur")?.Value;
                var maxDurAttr = phaseEl.Attribute("maxDur")?.Value;
                double? minDur = minDurAttr is null ? null : double.Parse(minDurAttr, CultureInfo.InvariantCulture);
                double? maxDur = maxDurAttr is null ? null : double.Parse(maxDurAttr, CultureInfo.InvariantCulture);
                phases.Add(new TlPhase(duration, state, minDur, maxDur));
            }

            // Last-wins on a duplicate id (multiple <tlLogic> programs for the same junction id,
            // e.g. an alternate programID) is a non-issue for this rung's single-program network.
            tlLogicsById[id] = new TlLogic(id, offset, phases, type);
        }

        var connectionsByFromEdgeLaneReadOnly = connectionsByFromEdgeLane
            .ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<Connection>)kvp.Value);

        var junctions = new List<Junction>();
        var junctionsById = new Dictionary<string, Junction>();
        var linkByInternalLane = new Dictionary<string, (Junction Junction, JunctionLink Link)>();

        foreach (var junctionEl in root.Elements("junction"))
        {
            var junction = ParseJunction(junctionEl, connections, lanesById);
            junctions.Add(junction);
            junctionsById[junction.Id] = junction;

            foreach (var link in junction.Links)
            {
                linkByInternalLane[link.InternalLaneId] = (junction, link);
            }
        }

        // F3/cont-turn (docs/NEED-contturn-stuck-in-junction.md): "which junction does this internal
        // lane belong to", for EVERY internal lane -- the port of SUMO's MSLane::isInternal()
        // (sumo/src/microsim/MSLane.cpp:2498 -> MSEdge::isInternal(), MSEdge.h:264), which is a LANE
        // PROPERTY true for every internal lane of every STAGE of a junction.
        //
        // `LinkByInternalLane` above CANNOT answer this: it is keyed off `Junction.IntLanes`, and
        // netconvert writes only the LINK-CONTROLLING lane there -- for a `cont` turn (one split by an
        // internal junction) it emits the SECOND-stage lane and omits the first
        // (NWWriter_SUMO.cpp:634-649: `haveVia ? viaID + "_0" : getInternalLaneID()`). So a vehicle on
        // the FIRST-stage lane (e.g. :C_3_0, with only :C_16_0 in intLanes) is invisible to any
        // "am I inside the junction" test written against IntLanes -- see the NEED doc for the
        // 95-step mid-junction freeze that caused.
        //
        // Recovering the first-stage lanes needs no new parsing: for a cont turn the link's own
        // connection STARTS on the first-stage internal edge (`<connection from=":C_3" to="CE"
        // via=":C_16_0"/>`), so walking `Connection.From` backwards while it is an internal (':')
        // edge enumerates every earlier stage. The walk is bounded (guard 8, as in the pool's
        // via-chain walk) and terminates as soon as `From` is a normal edge.
        // F3/isLeader T2.1: `LinkIndexByInternalLane` and `EntryConnectionByLink` are built by the
        // SAME back-walk as `junctionByInternalLane` above -- it already visits exactly the lanes
        // (both cont stages) these two new maps need, so this extends that traversal rather than
        // adding a second one (per the design doc's explicit instruction).
        //
        // `entryConnection` starts as `link.Connection` (correct already for a non-cont link, whose
        // `Connection` IS the entry hop) and is overwritten by each `previousHop` found while
        // walking back through internal stages; the LAST such overwrite -- the hop whose `From` is
        // finally a normal edge -- is SUMO's `getCorrespondingEntryLink()` result
        // (MSLink.cpp:1331-1339: "walks back while laneBefore->isInternal()").
        var junctionByInternalLane = new Dictionary<string, Junction>(StringComparer.Ordinal);
        var linkIndexByInternalLane = new Dictionary<string, (Junction Junction, int LinkIndex)>(StringComparer.Ordinal);
        var entryConnectionByLink = new Dictionary<(string JunctionId, int LinkIndex), Connection>();
        foreach (var junction in junctions)
        {
            foreach (var link in junction.Links)
            {
                junctionByInternalLane[link.InternalLaneId] = junction;
                linkIndexByInternalLane[link.InternalLaneId] = (junction, link.Index);

                var entryConnection = link.Connection;

                // The back-walk follows ONE LANE per stage -- the lane this link's own path actually
                // occupies, i.e. the hop's `fromLane` on the internal edge it came from -- never the
                // whole edge.
                //
                // It used to map every lane of each internal edge it passed through, and to find the
                // previous hop by matching the edge rather than the lane. On a SINGLE-lane internal
                // edge (every cont bay in every committed net before scenarios/_ped/georef_min) the two
                // readings coincide, which is why this stood. On a MULTI-lane internal bay they do not:
                // at georef_min's junction 'n00', `:n00_2` has lanes 0 and 1 and only lane 1 continues
                // through the internal junction to link 3's second stage. The edge-wide loop therefore
                // also stamped `:n00_2_0` -- which is link 2's OWN controlling lane -- as belonging to
                // link 3, silently overwriting a correct entry, and the edge-wide previous-hop search
                // could then walk back along the wrong lane's connection. Caught by
                // JunctionLinkLaneMapTests' every-committed-net sweep the moment a net with a
                // multi-lane cont bay was committed.
                var fromEdgeId = link.Connection.From;
                var fromLaneIndex = link.Connection.FromLane;
                for (var guard = 0; guard < 8 && fromEdgeId.Length > 0 && fromEdgeId[0] == ':'; guard++)
                {
                    if (!edgesById.TryGetValue(fromEdgeId, out var internalEdge))
                    {
                        break;
                    }

                    Lane? traversed = null;
                    foreach (var lane in internalEdge.Lanes)
                    {
                        if (lane.Index == fromLaneIndex)
                        {
                            traversed = lane;
                            break;
                        }
                    }

                    if (traversed is null)
                    {
                        // A hop naming a lane index the internal edge does not have -- malformed. Stop
                        // the walk rather than guess a lane, which is what produced the bug above.
                        break;
                    }

                    junctionByInternalLane[traversed.Id] = junction;
                    linkIndexByInternalLane[traversed.Id] = (junction, link.Index);

                    // Step one stage further back: the connection whose `via` is THIS LANE (not merely
                    // some lane of this edge).
                    Connection? previousHop = null;
                    foreach (var c in connections)
                    {
                        if (c.Via is { } via && string.Equals(via, traversed.Id, StringComparison.Ordinal))
                        {
                            previousHop = c;
                            break;
                        }
                    }

                    if (previousHop is null)
                    {
                        break;
                    }

                    entryConnection = previousHop;
                    fromEdgeId = previousHop.From;
                    fromLaneIndex = previousHop.FromLane;
                }

                entryConnectionByLink[(junction.Id, link.Index)] = entryConnection;
            }
        }

        // D2: LaneHandleById mirrors lanesById's keys 1:1 (every lane got exactly one handle
        // above), just projecting Id -> Handle instead of Id -> Lane.
        var laneHandleById = new Dictionary<string, int>(lanesById.Count, StringComparer.Ordinal);
        foreach (var lane in lanesByHandle)
        {
            laneHandleById[lane.Id] = lane.Handle;
        }

        // F3/internal-junction-foes T3.1 (docs/F3-INTERNAL-JUNCTION-DESIGN.md §4, §7 T3.1; ported
        // from MSInternalJunction.cpp's `postloadInit`, lines ~60-95). A second, SEPARATE pass over
        // every `<junction>` element -- `type="internal"` junctions parse into `InternalJunction`
        // here regardless of their (always empty) `<request>` set, rather than through
        // `ParseJunction`'s bail-out above (which would otherwise discard them).
        var internalJunctions = new List<InternalJunction>();
        var internalJunctionByBayLane = new Dictionary<string, InternalJunction>(StringComparer.Ordinal);
        var internalLaneFoes = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        // JUNCTION-APPROACH-ARM T1: `InternalLinkFoes`, a SIBLING of `internalLaneFoes` above, built
        // in the same pass over `<junction type="internal">` -- see NetworkModel.InternalLinkFoes's
        // doc comment for the construction rule and MSInternalJunction.cpp:96-110 for the source.
        var internalLinkFoes = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);

        foreach (var junctionEl in root.Elements("junction"))
        {
            if (junctionEl.Attribute("type")?.Value != "internal")
            {
                continue;
            }

            var internalId = RequireAttribute(junctionEl, "id");
            var incLanesAttr = junctionEl.Attribute("incLanes")?.Value ?? string.Empty;
            var incLanes = incLanesAttr.Length == 0
                ? new List<string>()
                : incLanesAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var candidateIntLanesAttr = junctionEl.Attribute("intLanes")?.Value ?? string.Empty;
            var candidateIntLanes = candidateIntLanesAttr.Length == 0
                ? new List<string>()
                : candidateIntLanesAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            var internalJunction = new InternalJunction(internalId, incLanes, candidateIntLanes);
            internalJunctions.Add(internalJunction);

            // MSInternalJunction.cpp:60-61: "the first lane in the list of incoming lanes is
            // special. It defines the link that needs to do all the checking for this internal
            // junction" -- keyed on IncLanes[0] ONLY (last-wins on a duplicate key is a non-issue:
            // no committed net has two internal junctions sharing the same checker lane).
            if (incLanes.Count > 0)
            {
                internalJunctionByBayLane[incLanes[0]] = internalJunction;
            }

            // MSInternalJunction.cpp:62-70: resolve the parent (real) junction and `ownLinkIndex` --
            // the special lane's OWN link index at the parent, exactly what `LinkIndexByInternalLane`
            // (T2.1) already maps both cont stages to. SUMO returns early (no foes at all) when the
            // parent has no right-of-way logic (`traffic_light_unregulated`, MSInternalJunction.cpp:
            // 64-65) -- mirrored here by simply leaving `internalLaneFoes` unset for this junction id
            // when the special lane doesn't resolve to a parent link + matching `<request>` row.
            if (incLanes.Count == 0
                || !linkIndexByInternalLane.TryGetValue(incLanes[0], out var own))
            {
                continue;
            }

            var ownLinkIndex = own.LinkIndex;
            var parentJunction = own.Junction;

            JunctionRequest? ownRequest = null;
            foreach (var r in parentJunction.Requests)
            {
                if (r.Index == ownLinkIndex)
                {
                    ownRequest = r;
                    break;
                }
            }

            if (ownRequest is null)
            {
                continue;
            }

            // MSInternalJunction.cpp:66-90 `postloadInit`'s outer loop, the two-branch rule (design
            // §1's correction): for each candidate `lane` in this internal junction's OWN
            // `IntLanes`, look at the candidate's OWN outgoing link.
            //   - `link->getViaLane() != nullptr` (candidate leads to another internal lane, i.e. it
            //     is itself a cont turn's STAGE-1 bay of a DIFFERENT internal junction): conditional
            //     add of the candidate itself (`response.test(foeIndex)`), UNCONDITIONAL add of its
            //     stage-2 lane (`link->getViaLane()`).
            //   - otherwise (a PLAIN internal lane): UNCONDITIONAL add of the candidate itself.
            //
            // A candidate "leads to another internal lane" iff it is NOT itself the link-controlling
            // (final-stage) lane for its own link index -- i.e. `LinkIndexByInternalLane[candidate]`
            // resolves to a link index `i` at the SAME parent junction whose `IntLanes[i] != candidate`
            // (design §3a's exact shape, applied here to a FOE candidate rather than to ego).
            var foeLaneIdsOrdered = new List<string>();
            var foeLaneIdSeen = new HashSet<string>(StringComparer.Ordinal);

            void AddFoeIfAbsent(string laneId)
            {
                if (foeLaneIdSeen.Add(laneId))
                {
                    foeLaneIdsOrdered.Add(laneId);
                }
            }

            foreach (var candidate in candidateIntLanes)
            {
                var isBay = false;
                var candLinkIndex = -1;

                if (linkIndexByInternalLane.TryGetValue(candidate, out var cand)
                    && ReferenceEquals(cand.Junction, parentJunction)
                    && cand.LinkIndex >= 0
                    && cand.LinkIndex < parentJunction.IntLanes.Count
                    && parentJunction.IntLanes[cand.LinkIndex] != candidate)
                {
                    isBay = true;
                    candLinkIndex = cand.LinkIndex;
                }

                if (isBay)
                {
                    // MSInternalJunction.cpp:78: `response.test(foeIndex)` -- ownRequest IS
                    // `response` (the parent's `Requests[ownLinkIndex]`); `candLinkIndex` IS
                    // `foeIndex` (`lane->getIncomingLanes()[0].viaLink->getIndex()`, i.e. the
                    // candidate's own entry link index at the parent -- exactly what
                    // `LinkIndexByInternalLane` already resolves for a cont bay).
                    if (ownRequest.RespondsTo(candLinkIndex))
                    {
                        AddFoeIfAbsent(candidate);
                    }

                    // MSInternalJunction.cpp:83-86 `addIfAbsent(myInternalLaneFoes,
                    // link->getViaLane())`: the candidate's stage-2 lane is ALWAYS a foe, regardless
                    // of the response test above.
                    AddFoeIfAbsent(parentJunction.IntLanes[candLinkIndex]);
                }
                else
                {
                    // MSInternalJunction.cpp:87-89 `addIfAbsent(myInternalLaneFoes, lane)`.
                    AddFoeIfAbsent(candidate);
                }
            }

            var foeHandles = new List<int>(foeLaneIdsOrdered.Count);
            foreach (var foeLaneId in foeLaneIdsOrdered)
            {
                if (laneHandleById.TryGetValue(foeLaneId, out var handle))
                {
                    foeHandles.Add(handle);
                }
            }

            internalLaneFoes[internalId] = foeHandles;

            // JUNCTION-APPROACH-ARM T1 (design §2, §5; MSInternalJunction.cpp:96-110): the SECOND
            // foe set `postloadInit` builds. Unlike `myInternalLaneFoes` above (which walks this
            // internal junction's OWN candidate `IntLanes`), this one walks the internal junction's
            // `IncLanes` starting at index 1 -- index 0 is the checker/bay lane (`incLanes[0]`,
            // already resolved above as `own`/`ownLinkIndex`/`thisLink`'s own lane), and re-including
            // it here would test its single link's own index against `ownRequest`, which is never
            // set (a link never yields to itself) -- but is EXCLUDED here regardless of whether that
            // matters for any given fixture, exactly as SUMO's `myIncomingLanes.begin() + 1` excludes
            // it unconditionally (InternalLinkFoeTests pins this exclusion on a synthetic fixture
            // where skipping it changes the result, so the skip is exercised, not incidental).
            var linkFoeLaneIdsOrdered = new List<string>();
            var linkFoeLaneIdSeen = new HashSet<string>(StringComparer.Ordinal);

            void AddLinkFoeIfAbsent(string viaLaneId)
            {
                if (linkFoeLaneIdSeen.Add(viaLaneId))
                {
                    linkFoeLaneIdsOrdered.Add(viaLaneId);
                }
            }

            for (var incIndex = 1; incIndex < incLanes.Count; incIndex++)
            {
                if (!lanesById.TryGetValue(incLanes[incIndex], out var incLane)
                    || !connectionsByFromEdgeLane.TryGetValue((incLane.EdgeId, incLane.Index), out var outgoingLinks))
                {
                    continue;
                }

                foreach (var link in outgoingLinks)
                {
                    // MSLink::getCorrespondingEntryLink()->getIndex() (MSLink.cpp:1331-1339): walks
                    // back while the lane before the link is internal. `LinkIndexByInternalLane`
                    // already performs exactly this walk (T2.1, above) keyed on a link's VIA lane,
                    // for both a plain link (the walk is a no-op -- `incLane` isn't internal) and a
                    // cont chain (every stage already resolves to the SAME final link index) --
                    // reused here, not re-derived, per the task brief.
                    //
                    // "links that target a shared walkingarea always have index -1"
                    // (MSInternalJunction.cpp:101) and a link with no `via` at all (never observed on
                    // a committed net -- InternalLinkFoeTests' corpus sweep) both fall through this
                    // same TryGetValue miss and are skipped, matching SUMO's `linkIndex != -1` guard.
                    // The `ReferenceEquals` guard additionally rejects a lookup that resolved to some
                    // OTHER junction's response bitset (`entry.LinkIndex` is only meaningful against
                    // `ownRequest`, which belongs to `parentJunction` specifically).
                    if (link.Via is not { } viaLaneId
                        || !linkIndexByInternalLane.TryGetValue(viaLaneId, out var entry)
                        || !ReferenceEquals(entry.Junction, parentJunction))
                    {
                        continue;
                    }

                    if (!ownRequest.RespondsTo(entry.LinkIndex))
                    {
                        continue;
                    }

                    AddLinkFoeIfAbsent(viaLaneId);

                    // MSInternalJunction.cpp:104-108: "we added the entry link, also use the
                    // internalJunctionLink that follows" -- when the just-kept link's via lane's own
                    // FIRST outgoing link itself has a via lane (i.e. this via lane is itself a cont
                    // chain's stage-1 bay), add that follow-on link's via lane too, unconditionally
                    // (no second response test).
                    if (lanesById.TryGetValue(viaLaneId, out var via)
                        && connectionsByFromEdgeLane.TryGetValue((via.EdgeId, via.Index), out var viaOutgoingLinks)
                        && viaOutgoingLinks.Count > 0
                        && viaOutgoingLinks[0].Via is { } followOnViaLaneId)
                    {
                        AddLinkFoeIfAbsent(followOnViaLaneId);
                    }
                }
            }

            var linkFoeHandles = new List<int>(linkFoeLaneIdsOrdered.Count);
            foreach (var viaLaneId in linkFoeLaneIdsOrdered)
            {
                if (laneHandleById.TryGetValue(viaLaneId, out var handle))
                {
                    linkFoeHandles.Add(handle);
                }
                else
                {
                    // Design §4.1 / T1 success condition 3: a link foe that fails to resolve a real
                    // lane handle here would otherwise be silently dropped -- the task brief is
                    // explicit that this must STOP and report, not vanish quietly. No committed net
                    // has ever hit this (InternalLinkFoeTests' corpus sweep asserts it directly), so
                    // this is a loud failure, never a guessed fallback.
                    throw new InvalidDataException(
                        $"internal junction '{internalId}': link foe via-lane '{viaLaneId}' does not "
                        + "resolve to a known lane handle (NetworkModel.InternalLinkFoes requires a "
                        + "non-null via lane for every entry -- design doc §4.1).");
                }
            }

            internalLinkFoes[internalId] = linkFoeHandles;
        }

        return new NetworkModel(
            edges,
            edgesById,
            lanesById,
            connections,
            connectionsByFromLaneTo,
            tlLogicsById,
            connectionsByFromEdgeLaneReadOnly,
            junctions,
            junctionsById,
            linkByInternalLane,
            lanesByHandle,
            laneHandleById,
            junctionByInternalLane,
            linkIndexByInternalLane,
            entryConnectionByLink,
            internalJunctions,
            internalJunctionByBayLane,
            internalLaneFoes,
            internalLinkFoes);
    }

    // Rung 9b-i: parses one <junction> -- id/type/intLanes are always present (netconvert
    // output); only junctions with a nonempty intLanes AND at least one child <request> get a
    // populated Links/Requests/Conflicts (dead_end/internal junctions have neither and parse to
    // empty lists, which is harmless -- see Junction's doc comment).
    private static Junction ParseJunction(
        XElement junctionEl,
        IReadOnlyList<Connection> connections,
        IReadOnlyDictionary<string, Lane> lanesById)
    {
        var id = RequireAttribute(junctionEl, "id");
        var type = RequireAttribute(junctionEl, "type");
        var intLanesAttr = junctionEl.Attribute("intLanes")?.Value ?? string.Empty;
        var intLanes = intLanesAttr.Length == 0
            ? new List<string>()
            : intLanesAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // VB-1: `shape` is absent for some internal junctions -- an empty polygon is tolerated
        // (the viz simply has nothing to fill for that junction), never a parse error.
        var shape = junctionEl.Attribute("shape") is { } shapeAttr
            ? ParseShape(shapeAttr.Value)
            : Array.Empty<(double X, double Y)>();

        var requestEls = junctionEl.Elements("request").ToList();
        if (intLanes.Count == 0 || requestEls.Count == 0)
        {
            return new Junction(id, type, intLanes, Array.Empty<JunctionLink>(), Array.Empty<JunctionRequest>(),
                Array.Empty<JunctionConflict>(), Array.Empty<MergeConflict>(), shape, Array.Empty<BayConflict>());
        }

        // Links: for each link index i, the top-level <connection> whose `via` equals
        // intLanes[i] (the incoming-lane -> outgoing-lane move this link represents). A missing
        // match (shouldn't happen for a real request row) is skipped gracefully -- the request
        // row is kept regardless, so right-of-way bits are never silently dropped.
        var links = new List<JunctionLink>();
        var linksByIndex = new Dictionary<int, JunctionLink>();
        for (var i = 0; i < intLanes.Count; i++)
        {
            var internalLaneId = intLanes[i];
            var connection = connections.FirstOrDefault(c => c.Via == internalLaneId);
            if (connection is null)
            {
                continue;
            }

            var link = new JunctionLink(i, internalLaneId, connection);
            links.Add(link);
            linksByIndex[i] = link;
        }

        var requests = new List<JunctionRequest>();
        var requestsByIndex = new Dictionary<int, JunctionRequest>();
        foreach (var requestEl in requestEls)
        {
            var index = int.Parse(RequireAttribute(requestEl, "index"), CultureInfo.InvariantCulture);
            var response = RequireAttribute(requestEl, "response");
            var foes = RequireAttribute(requestEl, "foes");
            var cont = (requestEl.Attribute("cont")?.Value ?? "0") == "1";

            var request = new JunctionRequest(index, response, foes, cont);
            requests.Add(request);
            requestsByIndex[index] = request;
        }

        // Conflicts: for every ordered pair (i, j), i != j, where link i's request marks link j
        // as a physical foe and both links resolved to an internal lane, compute the crossing
        // of the two internal-lane shapes (see PolylineGeometry's doc comment for what counts
        // as a "crossing" -- merges that only share an endpoint are skipped).
        var conflicts = new List<JunctionConflict>();
        // C4-v: sameTarget MERGE geometry (per ego link) -- the two links whose connections feed the
        // same downstream lane converge instead of crossing, so they produce a MergeConflict (lbc/
        // flbc) rather than a JunctionConflict.
        var merges = new List<MergeConflict>();
        foreach (var request in requests)
        {
            if (!linksByIndex.TryGetValue(request.Index, out var egoLink))
            {
                continue;
            }

            for (var j = 0; j < intLanes.Count; j++)
            {
                if (j == request.Index || !request.FoeWith(j))
                {
                    continue;
                }

                if (!linksByIndex.TryGetValue(j, out var foeLink))
                {
                    continue;
                }

                var egoLane = lanesById[egoLink.InternalLaneId];
                var foeLane = lanesById[foeLink.InternalLaneId];

                // C4-v: a sameTarget MERGE (connections share the destination edge + lane) does NOT
                // cross (TryIntersect below fails, they touch only at the shared end) -- compute its
                // lengthBehindCrossing from MSLink::setRequestInformation's sameTarget arm instead.
                if (egoLink.Connection.To == foeLink.Connection.To
                    && egoLink.Connection.ToLane == foeLink.Connection.ToLane)
                {
                    // minDist = MIN2(DIVERGENCE_MIN_WIDTH=2.5, 0.5*(egoW+foeW)) (MSLink.cpp:306).
                    var minDist = Math.Min(2.5, 0.5 * (egoLane.Width + foeLane.Width));
                    double egoLbc;
                    double foeLbc;
                    // Lanes ending >= minDist apart => CONFLICT_DUMMY_MERGE (lbc 0); else compute the
                    // divergence point (MSLink.cpp:307-330). computeDistToDivergence is symmetric in
                    // its (lane, sibling) roles, so one call serves both lanes' lbc via their own
                    // length/shape factor (InterpolateGeometryPosToLanePos = geomPos * laneLen / shapeLen).
                    var egoEnd = egoLane.Shape[^1];
                    var foeEnd = foeLane.Shape[^1];
                    if (Math.Sqrt(((egoEnd.X - foeEnd.X) * (egoEnd.X - foeEnd.X)) + ((egoEnd.Y - foeEnd.Y) * (egoEnd.Y - foeEnd.Y))) >= minDist)
                    {
                        egoLbc = 0.0;
                        foeLbc = 0.0;
                    }
                    else
                    {
                        var dtd = PolylineGeometry.ComputeDistToDivergence(
                            egoLane.Shape, foeLane.Shape, egoLane.Length, foeLane.Length, minDist);
                        egoLbc = dtd * egoLane.Length / PolylineGeometry.PolylineLength(egoLane.Shape);
                        foeLbc = dtd * foeLane.Length / PolylineGeometry.PolylineLength(foeLane.Shape);
                    }

                    merges.Add(new MergeConflict(egoLink.Index, foeLink.Index, egoLbc, foeLbc));
                    continue;
                }

                if (PolylineGeometry.TryIntersect(egoLane.Shape, foeLane.Shape, out var intersection))
                {
                    // Rung 9b-ii: MSLink.cpp:358-366 -- widthFactor widens (or leaves unchanged)
                    // the conflict size for shallow-angle crossings; angleDiff is the acute angle
                    // between the two internal lanes' travel DIRECTIONS at the crossing
                    // (GeomHelper::getMinAngleDiff, folded to [0,90] for these straight lanes).
                    var egoDirection = LaneDirection(egoLane.Shape);
                    var foeDirection = LaneDirection(foeLane.Shape);
                    var angleDiffDeg = MinAngleDiffDegrees(egoDirection, foeDirection);
                    var widthFactor = (1.0 / Math.Max(Math.Sin(DegToRad(angleDiffDeg)), 0.2) * 2.0) - 1.0;

                    // MSLink.cpp:365-366/380-382: conflictSize = MIN2(foeLane->getWidth() *
                    // widthFactor, lane->getLength()); myConflicts.push_back(ConflictInfo(
                    // lane->getLength() - MAX2(0, crossingArc - conflictSize/2), conflictSize)).
                    // Each conflict record here is built once per (ego, foe) ordered pair, so the
                    // "ego" / "foe" roles below always match this record's own EgoLink/FoeLink.
                    var egoConflictSize = Math.Min(foeLane.Width * widthFactor, egoLane.Length);
                    var foeConflictSize = Math.Min(egoLane.Width * widthFactor, foeLane.Length);
                    var egoLengthBehindCrossing = egoLane.Length - Math.Max(0.0, intersection.ArcA - (egoConflictSize / 2.0));
                    var foeLengthBehindCrossing = foeLane.Length - Math.Max(0.0, intersection.ArcB - (foeConflictSize / 2.0));

                    conflicts.Add(new JunctionConflict(
                        egoLink.Index, foeLink.Index,
                        intersection.ArcA, intersection.ArcB,
                        intersection.Point,
                        egoConflictSize, foeConflictSize,
                        egoLengthBehindCrossing, foeLengthBehindCrossing));
                }
            }
        }

        // JUNCTION-FOE-LANE F2.1b: bay-corridor conflicts -- see BayConflict's own doc comment for
        // the full WHY (beyond-SUMO honesty deviation; bay lanes are in no foes row). For each cont
        // link, its FIRST-stage bay lane is the FROM lane of the link's (second-hop, internal)
        // connection. Corridor overlap is PROXIMITY-sampled, not centerline-crossed: a bay hugging
        // its sibling movement (shared source lane, shapes departing from the same point) never
        // crosses centerlines, which is exactly why TryIntersect cannot see this class. The
        // threshold is body-overlap distance for two default-width (1.8 m) vehicles plus a small
        // margin -- corridors closer than this can hold physically-overlapping bodies.
        var bayConflicts = new List<BayConflict>();
        const double bodyOverlapThreshold = 2.0;
        // Entry 36 (the traced city-organic junction-359 wedge): a row is emitted only when the
        // ego-side overlap is at least a metre. The threshold above is CENTERLINE proximity, and
        // 2.0 m exceeds the 1.8 m at which two default-width bodies actually touch -- so two
        // corridors that merely BRUSH in passing (opposite-arm left turns whose tips pass within
        // ~1.9 m: ego-side sliver 0.27 m at junction 359, where SUMO's non-foes verdict is
        // geometrically CORRECT) produced a row that parked a vehicle 0.1 m before the sliver
        // forever, deadlocked cross-arm against the bay occupant it was "yielding" to (jy7 one way,
        // the SUMO-faithful inTheWay follow the other -- no tie-break can span those two arms). A
        // genuine shared corridor measures metres of overlap (4-8 m for the sibling-bay pairs and
        // along-bay movements; ~4 m even for a perpendicular true crossing at this threshold), so
        // one metre separates the classes with margin on both sides.
        const double minEgoOverlapLen = 1.0;
        foreach (var request in requests)
        {
            if (!request.Cont || !linksByIndex.TryGetValue(request.Index, out var contLink))
            {
                continue;
            }

            // The cont link's JunctionLink.Connection is the INTERNAL second-hop connection
            // (via == intLanes[index]); its From edge/lane is the first-stage bay.
            if (contLink.Connection.From.Length == 0 || contLink.Connection.From[0] != ':')
            {
                continue;
            }

            var bayLaneId = contLink.Connection.From + "_" + contLink.Connection.FromLane.ToString(CultureInfo.InvariantCulture);
            if (!lanesById.TryGetValue(bayLaneId, out var bayLane) || bayLane.Shape.Count < 2)
            {
                continue;
            }

            foreach (var egoLink in links)
            {
                if (egoLink.Index == request.Index)
                {
                    continue;
                }

                if (!lanesById.TryGetValue(egoLink.InternalLaneId, out var egoLane) || egoLane.Shape.Count < 2)
                {
                    continue;
                }

                if (PolylineGeometry.TryCorridorOverlap(
                        egoLane.Shape, bayLane.Shape, bodyOverlapThreshold,
                        out var egoGeomStart, out var egoGeomEnd, out var bayGeomStart, out var bayGeomEnd)
                    && egoGeomEnd - egoGeomStart >= minEgoOverlapLen)
                {
                    // Geometry-arc -> lane-position frame (InterpolateGeometryPosToLanePos).
                    var egoScale = egoLane.Length / PolylineGeometry.PolylineLength(egoLane.Shape);
                    var bayScale = bayLane.Length / PolylineGeometry.PolylineLength(bayLane.Shape);
                    bayConflicts.Add(new BayConflict(
                        egoLink.Index, bayLaneId,
                        egoGeomStart * egoScale, egoGeomEnd * egoScale,
                        bayGeomStart * bayScale, bayGeomEnd * bayScale));
                }

                // Entry 36: the FIRST-stage bay of a cont EGO link, compared against the same foe
                // bay -- the piece the stage-2 comparison above cannot see. Two sibling turns from
                // one approach lane have bays SHARING a start point (junction 301's :301_24_0 vs
                // :301_25_0, both shorter than a car); with only the stage-2 row, ego's hold point
                // lands beyond its own bay -- INSIDE the overlap -- and the pair stops
                // interpenetrated, which is one half of the traced dwell-634 mutual wedge. Ego arcs
                // are emitted RELATIVE TO THE STAGE-2 START (negative: bay-frame arc minus bay
                // length) so the engine's `egoDistToEntry + EgoArcStart` -- whose walk already
                // includes the bay -- lands the hold at the stop line unchanged.
                JunctionRequest? egoRequest = null;
                foreach (var r in requests)
                {
                    if (r.Index == egoLink.Index)
                    {
                        egoRequest = r;
                        break;
                    }
                }

                if (egoRequest is null || !egoRequest.Cont
                    || egoLink.Connection.From.Length == 0 || egoLink.Connection.From[0] != ':')
                {
                    continue;
                }

                var egoBayLaneId = egoLink.Connection.From + "_" + egoLink.Connection.FromLane.ToString(CultureInfo.InvariantCulture);
                if (egoBayLaneId == bayLaneId
                    || !lanesById.TryGetValue(egoBayLaneId, out var egoBayLane) || egoBayLane.Shape.Count < 2)
                {
                    continue;
                }

                if (PolylineGeometry.TryCorridorOverlap(
                        egoBayLane.Shape, bayLane.Shape, bodyOverlapThreshold,
                        out var egoBayGeomStart, out var egoBayGeomEnd, out var bayGeomStart2, out var bayGeomEnd2)
                    && egoBayGeomEnd - egoBayGeomStart >= minEgoOverlapLen)
                {
                    var egoBayScale = egoBayLane.Length / PolylineGeometry.PolylineLength(egoBayLane.Shape);
                    var bayScale2 = bayLane.Length / PolylineGeometry.PolylineLength(bayLane.Shape);
                    bayConflicts.Add(new BayConflict(
                        egoLink.Index, bayLaneId,
                        (egoBayGeomStart * egoBayScale) - egoBayLane.Length,
                        (egoBayGeomEnd * egoBayScale) - egoBayLane.Length,
                        bayGeomStart2 * bayScale2, bayGeomEnd2 * bayScale2));
                }
            }
        }

        // JUNCTION-FOE-LANE F1.1 (journal Entry 39): NON-FOES internal-lane pairs. The crossing/
        // merge pass above only sees pairs netconvert marked as foes; the measured driven-through
        // class (a STOPPED vehicle on a plain internal lane, ~15 of L2's 18 gate-ON stopXmove
        // pair-steps: j=1150 :1150_2_0 x :1150_0_1, j=123 :123_11_0 x :123_9_1, ...) lives in
        // ordered pairs whose corridors genuinely overlap but are in NEITHER foes row. Same
        // sanctioned beyond-SUMO honesty deviation as the bay rows (SUMO 1.20 drives through these
        // too; the artefact ladder forbids copying that), same machinery: proximity-sampled
        // corridor overlap, the same non-negotiable 1.0 m brush filter, rows consumed ONLY by the
        // gate-scoped bay-occupancy arm (the physical-occupancy index covers every internal lane,
        // so the engine needs no changes). Foe side of a row is the foe link's internal lane; ego
        // arcs stay in the ego stage-lane frame the arm already uses.
        foreach (var request in requests)
        {
            if (!linksByIndex.TryGetValue(request.Index, out var egoLink)
                || !lanesById.TryGetValue(egoLink.InternalLaneId, out var egoLane)
                || egoLane.Shape.Count < 2)
            {
                continue;
            }

            for (var j = 0; j < intLanes.Count; j++)
            {
                if (j == request.Index || request.FoeWith(j))
                {
                    continue;
                }

                if (!linksByIndex.TryGetValue(j, out var foeLink)
                    || foeLink.InternalLaneId == egoLink.InternalLaneId
                    || !lanesById.TryGetValue(foeLink.InternalLaneId, out var foeLane)
                    || foeLane.Shape.Count < 2)
                {
                    continue;
                }

                if (PolylineGeometry.TryCorridorOverlap(
                        egoLane.Shape, foeLane.Shape, bodyOverlapThreshold,
                        out var egoGeomStart, out var egoGeomEnd, out var foeGeomStart, out var foeGeomEnd)
                    && egoGeomEnd - egoGeomStart >= minEgoOverlapLen)
                {
                    var egoScale = egoLane.Length / PolylineGeometry.PolylineLength(egoLane.Shape);
                    var foeScale = foeLane.Length / PolylineGeometry.PolylineLength(foeLane.Shape);
                    bayConflicts.Add(new BayConflict(
                        egoLink.Index, foeLink.InternalLaneId,
                        egoGeomStart * egoScale, egoGeomEnd * egoScale,
                        foeGeomStart * foeScale, foeGeomEnd * foeScale));
                }
            }
        }

        return new Junction(id, type, intLanes, links, requests, conflicts, merges, shape, bayConflicts);
    }

    // Rung 9b-ii: a straight 2-point internal lane's travel direction, normalized -- ported
    // from the (unit-vector) reading of GeomHelper::naviDegree(shape.rotationAtOffset(...)) for
    // the straight-through internal lanes this scenario has (no curved internal-lane shapes are
    // in scope here).
    private static (double X, double Y) LaneDirection(IReadOnlyList<(double X, double Y)> shape)
    {
        var first = shape[0];
        var last = shape[^1];
        var dx = last.X - first.X;
        var dy = last.Y - first.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return (dx / length, dy / length);
    }

    // Ported from GeomHelper::getMinAngleDiff (sumo/src/utils/geom/GeomHelper.cpp) for the two
    // straight lanes this rung's net has: the acute angle between two direction vectors, folded
    // to [0, 90] degrees via acos(|dot|/(|a||b|)) -- equivalent to getMinAngleDiff's own
    // fmod/180-wrap for this scenario's perpendicular/straight crossing.
    private static double MinAngleDiffDegrees((double X, double Y) a, (double X, double Y) b)
    {
        var dot = (a.X * b.X) + (a.Y * b.Y);
        var magA = Math.Sqrt((a.X * a.X) + (a.Y * a.Y));
        var magB = Math.Sqrt((b.X * b.X) + (b.Y * b.Y));
        var cos = Math.Clamp(Math.Abs(dot) / (magA * magB), -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    // A lane is a valid target for a road/rail vehicle unless its `allow` attribute permits ONLY
    // pedestrians (a sidewalk: `<lane allow="pedestrian">`). No `allow` attribute (or a `disallow="..."`
    // instead) leaves every vehicle class permitted -> true. Any non-pedestrian token in `allow` (rail,
    // bus, passenger, ...) means a vehicle may use the lane -> true. Only `allow` listing pedestrian alone
    // returns false. Mirrors MSLane's SVCPermissions notion of a lane no vehicle class may enter.
    private static bool LaneAllowsRoadVehicle(string? allowAttr)
    {
        if (string.IsNullOrEmpty(allowAttr))
        {
            return true;
        }

        foreach (var tok in allowAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok != "pedestrian")
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<(double X, double Y)> ParseShape(string shape)
    {
        var points = new List<(double, double)>();
        foreach (var pair in shape.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var coords = pair.Split(',');
            var x = double.Parse(coords[0], CultureInfo.InvariantCulture);
            var y = double.Parse(coords[1], CultureInfo.InvariantCulture);
            points.Add((x, y));
        }

        return points;
    }

    // SUMOSHARP-API.md §6: parse the optional 3rd (z / elevation) component of each shape vertex. Returns
    // null when the shape is 2-D (any vertex lacks a z) -- the common case, leaving Lane.ShapeZ null so the
    // read surface reports PosZ = 0 exactly as before. Index-aligned with ParseShape's output.
    private static IReadOnlyList<double>? ParseShapeZ(string shape)
    {
        var zs = new List<double>();
        foreach (var pair in shape.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var coords = pair.Split(',');
            if (coords.Length < 3)
            {
                return null; // 2-D shape -> no elevation profile
            }

            zs.Add(double.Parse(coords[2], CultureInfo.InvariantCulture));
        }

        return zs.Count > 0 ? zs : null;
    }

    private static string RequireAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"<{element.Name}> is missing required attribute '{name}'.");
}
