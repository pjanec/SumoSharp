namespace Sim.Ingest;

// P1E-2 (HIGH-DENSITY-P1E-DESIGN.md §1C, §9) -- a STANDALONE port of MSRoutingEngine's per-edge
// live edge-speed smoothing (sumo/src/microsim/devices/MSRoutingEngine.cpp:113-167,216-291), the
// prerequisite for P1E-4's periodic congestion-reactive reroute device. This class has NO Engine
// dependency and nothing in the running engine calls it yet -- it is unit-testable in complete
// isolation (construct from a NetworkModel, feed it synthetic per-edge speed samples, read back
// the smoothed effort). Wiring it into the step loop's end-of-step pass is P1E-4's job.
//
// Seeding (MSRoutingEngine::_initEdgeWeights): every NORMAL (non-internal, i.e. not ':'-prefixed)
// edge gets its length (from lane 0 -- MSEdge::getLength()) and free-flow speed (the fastest lane
// on the edge -- MSEdge::getMeanSpeed() when empty returns the edge's speed limit, i.e. the max
// lane speed). `smoothedSpeed` and every slot of the length-N `past` ring buffer are seeded to
// that free-flow speed, exactly as SUMO seeds `myEdgeSpeeds`/`myPastEdgeSpeeds` before any vehicle
// has ever touched the edge.
//
// isDelayed (MSEdge.h:711-713, MSEdge::isDelayed) is a PERMANENT ONE-WAY LATCH: false until the
// first vehicle ever enters a lane on the edge, true forever after (never reset even once the
// edge empties and its live speed climbs back toward free-flow). MarkDelayed below is that latch
// -- calling it on an already-latched edge is a no-op, and an edge whose MarkDelayed is never
// called is NEVER touched by Update, staying at its free-flow seed forever (§8 risk 2: "not
// update occupied edges" -- this is deliberately NOT "is currently occupied").
//
// The incremental moving-average recurrence (MSRoutingEngine::adaptEdgeEfforts, .cpp:245-248) is
// ported EXACTLY -- `smoothed += (curr - past[k]) / N; past[k] = curr;` -- rather than recomputing
// `sum(past)/N` from scratch each time, because SUMO's own float-drift trajectory depends on the
// incremental form (§8 risk 4). The ring index `k` is SHARED across every edge and advances once
// per Update call (after every delayed edge has been folded in that call), matching
// `myAdaptationStepsIndex` being a single class-static counter, not a per-edge one.
public sealed class RerouteEdgeWeights
{
    // SUMO's NUMERICAL_EPS (sumo/src/config.h.cmake: "#define NUMERICAL_EPS 0.001"), the epsilon
    // MSRoutingEngine::getEffort floors the live speed at before dividing
    // (`e->getLength() / MAX2(myEdgeSpeeds[id], NUMERICAL_EPS)`, MSRoutingEngine.cpp:163) so a
    // near-zero/zero smoothed speed can never produce an infinite or negative effort.
    public const double NumericalEps = 0.001;

    private readonly int _n;

    // Fixed, deterministic per-edge iteration order for Update's fold (§4: "deterministic (fixed
    // per-edge order)") -- the network's own Edges list order (parse order), not a Dictionary's
    // enumeration order (which .NET does not contractually guarantee to be insertion order).
    private readonly IReadOnlyList<string> _edgeOrder;

    private readonly Dictionary<string, double> _length = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _freeFlowSpeed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _smoothedSpeed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double[]> _past = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _delayed = new(StringComparer.Ordinal);

    // The shared ring-buffer write index (myAdaptationStepsIndex), advanced once per Update call.
    private int _k;

    // `adaptationSteps` is SUMO's device.rerouting.adaptation-steps (N, the ring-buffer length /
    // moving-average window). Only NORMAL edges (edge.Id not starting with ':') are seeded --
    // internal/junction-interior edges are never routing-graph nodes (NetworkRouter's own
    // convention) and SUMO's edge-speed table is likewise indexed by MSEdge::getNumericalID()
    // over normal edges only.
    public RerouteEdgeWeights(NetworkModel network, int adaptationSteps)
    {
        if (adaptationSteps <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(adaptationSteps), adaptationSteps, "device.rerouting.adaptation-steps must be > 0.");
        }

        _n = adaptationSteps;

        var order = new List<string>();
        foreach (var edge in network.Edges)
        {
            if (IsInternal(edge.Id))
            {
                continue;
            }

            var length = edge.Lanes[0].Length;
            var freeFlow = edge.Lanes.Max(l => l.Speed);

            _length[edge.Id] = length;
            _freeFlowSpeed[edge.Id] = freeFlow;
            _smoothedSpeed[edge.Id] = freeFlow;
            _past[edge.Id] = CreateSeededRingBuffer(freeFlow, _n);
            _delayed[edge.Id] = false;
            order.Add(edge.Id);
        }

        _edgeOrder = order;
    }

    private static double[] CreateSeededRingBuffer(double seed, int n)
    {
        var buf = new double[n];
        for (var i = 0; i < n; i++)
        {
            buf[i] = seed;
        }

        return buf;
    }

    private static bool IsInternal(string edgeId) => edgeId.StartsWith(':');

    // The free-flow (speed-limit) seed for an edge -- exposed so callers/tests can independently
    // compute the expected floor (length / min(freeFlow, vehicleMaxSpeed)) without reaching into
    // private state.
    public double FreeFlowSpeed(string edgeId) => _freeFlowSpeed[edgeId];

    public double Length(string edgeId) => _length[edgeId];

    public double SmoothedSpeed(string edgeId) => _smoothedSpeed[edgeId];

    // MSEdge::isDelayed(): true forever after the first MarkDelayed call, false until then.
    public bool IsDelayed(string edgeId) => _delayed.TryGetValue(edgeId, out var delayed) && delayed;

    // MSEdge::mySpeedByVSSAndCF-independent latch set the first time any vehicle enters a lane on
    // this edge (MSEdge.h:711-713's actual trigger; ported here as an explicit caller-driven call
    // since P1E-2 has no vehicle/lane occupancy concept of its own). A no-op on an edge that is
    // already latched, or on an id this table doesn't know (internal edge / not in this network) --
    // never throws, matching "set it, don't assert an edge exists" semantics for a device that
    // only ever calls this for edges it has already resolved from the network.
    public void MarkDelayed(string edgeId)
    {
        if (_delayed.ContainsKey(edgeId))
        {
            _delayed[edgeId] = true;
        }
    }

    // MSRoutingEngine::adaptEdgeEfforts, ported exactly (.cpp:216-269): for every delayed edge (in
    // fixed order), sample `curr = currentMeanSpeed(edgeId)` once, fold it into the incremental
    // moving average, and overwrite the ring slot the average just consumed. Only AFTER every
    // delayed edge has been folded does the shared ring index advance -- an edge that has never
    // been MarkDelayed'd is skipped entirely (stays at its free-flow seed, per the latch's
    // contract) and its ring buffer / smoothedSpeed are left completely untouched.
    public void Update(Func<string, double> currentMeanSpeed)
    {
        foreach (var edgeId in _edgeOrder)
        {
            if (!_delayed[edgeId])
            {
                continue;
            }

            var curr = currentMeanSpeed(edgeId);
            var past = _past[edgeId];
            _smoothedSpeed[edgeId] += (curr - past[_k]) / _n;
            past[_k] = curr;
        }

        _k = (_k + 1) % _n;
    }

    // MSRoutingEngine::getEffort (.cpp:161-165): max(length / max(smoothedSpeed, EPS),
    // minimumTravelTime), where minimumTravelTime = length / vehicleMaxSpeed (MSEdge::
    // getMinimumTravelTime -- the "+ myTimePenalty" term is 0 for every SumoSharp vType, which has
    // no time-penalty concept yet; §8 risk 6). The floor keeps the router's A* heuristic admissible
    // (effort can never fall below the free-flow-at-vehicle-max-speed travel time).
    public double Effort(string edgeId, double vehicleMaxSpeed)
    {
        var length = _length[edgeId];
        var smoothed = _smoothedSpeed[edgeId];
        var minimumTravelTime = length / vehicleMaxSpeed;
        return Math.Max(length / Math.Max(smoothed, NumericalEps), minimumTravelTime);
    }
}
