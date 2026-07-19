using System;
using System.Collections.Generic;
using Sim.Core;
using Sim.Core.Orca;

namespace Sim.Pedestrians;

// P8-3a (docs/PEDESTRIAN-P8-3-DEMAND-DESIGN.md): the weighted O->D endpoint set for auto-deduced sub-area
// demand. Endpoints are the union of the walkable FRINGE (external entry/exit, uniform base weight) and the
// deduced POIs (internal sources/sinks, weighted by PedPoi.Weight). A ped draws an origin and a destination
// from this set weighted by endpoint weight -- and because every endpoint is a fringe or POI edge, its
// spawn and arrival are appearance-LEGITIMATE by construction (the P8-3 x P8-2 synergy).
//
// Deterministic: the endpoint array is built once in a fixed order; DrawWeighted is a cumulative-weight
// lookup on VehicleRng.NextDouble(), so a ped's O/D depends only on its own seeded stream.
public sealed class SubareaDemand
{
    public readonly record struct Endpoint(string EdgeId, Vec2 Pos, double Weight, bool IsFringe);

    private readonly Endpoint[] _endpoints;
    private readonly double[] _cumWeight; // prefix sums; _cumWeight[^1] == _total
    private readonly double _total;

    private SubareaDemand(Endpoint[] endpoints)
    {
        _endpoints = endpoints;
        _cumWeight = new double[endpoints.Length];
        var acc = 0.0;
        for (var i = 0; i < endpoints.Length; i++)
        {
            acc += Math.Max(0.0, endpoints[i].Weight);
            _cumWeight[i] = acc;
        }

        _total = acc;
    }

    public int Count => _endpoints.Length;

    public double TotalWeight => _total;

    public IReadOnlyList<Endpoint> Endpoints => _endpoints;

    public Endpoint this[int index] => _endpoints[index];

    // Build the endpoint set from the deduced POIs (internal, per-POI weight) + the walkable fringe
    // (external, `fringeWeight` each). Order is POIs (in list order) then fringe (in list order) -- fixed
    // and deterministic. At least one endpoint is required.
    public static SubareaDemand Build(
        IReadOnlyList<PedPoi> pois,
        IReadOnlyList<(string EdgeId, Vec2 Pos)> fringeEndpoints,
        double fringeWeight = 1.0)
    {
        if (pois is null)
        {
            throw new ArgumentNullException(nameof(pois));
        }

        if (fringeEndpoints is null)
        {
            throw new ArgumentNullException(nameof(fringeEndpoints));
        }

        if (fringeWeight < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fringeWeight), "fringe weight must be >= 0.");
        }

        var list = new List<Endpoint>(pois.Count + fringeEndpoints.Count);
        foreach (var p in pois)
        {
            list.Add(new Endpoint(p.Edge, p.Pos, p.Weight, IsFringe: false));
        }

        foreach (var f in fringeEndpoints)
        {
            list.Add(new Endpoint(f.EdgeId, f.Pos, fringeWeight, IsFringe: true));
        }

        if (list.Count == 0)
        {
            throw new ArgumentException("SubareaDemand needs at least one endpoint (no POIs and no fringe).", nameof(pois));
        }

        return new SubareaDemand(list.ToArray());
    }

    // Weighted draw -> endpoint index. Cumulative-weight binary search on NextDouble()*total; falls back to
    // a uniform draw if every weight is zero (deterministic either way).
    public int DrawWeightedIndex(ref VehicleRng rng)
    {
        if (_total <= 0.0)
        {
            var u = (int)(rng.NextDouble() * _endpoints.Length);
            return u >= _endpoints.Length ? _endpoints.Length - 1 : u;
        }

        var r = rng.NextDouble() * _total;
        var lo = 0;
        var hi = _cumWeight.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_cumWeight[mid] > r)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    public Endpoint DrawWeighted(ref VehicleRng rng) => _endpoints[DrawWeightedIndex(ref rng)];

    // Resolve each walkable fringe edge (manifest.subarea.fringe_edges, ped=true) to a point on its sidewalk
    // lane -- the arc-length midpoint of the lane polyline, so a spawned ped starts on the walkable surface.
    // A fringe edge with no matching sidewalk lane is skipped (defensive; P8-1 pins that all 48 are sidewalks).
    public static IReadOnlyList<(string EdgeId, Vec2 Pos)> FringeEndpointsFromNetwork(
        PedNetwork network, IReadOnlyCollection<string> fringeEdgeIds)
    {
        if (network is null)
        {
            throw new ArgumentNullException(nameof(network));
        }

        if (fringeEdgeIds is null)
        {
            throw new ArgumentNullException(nameof(fringeEdgeIds));
        }

        var laneByEdge = new Dictionary<string, PedLane>(StringComparer.Ordinal);
        foreach (var lane in network.Sidewalks)
        {
            laneByEdge[lane.EdgeId] = lane; // one sidewalk per fringe stub in practice; last wins if not
        }

        var result = new List<(string, Vec2)>();
        foreach (var edge in fringeEdgeIds)
        {
            if (laneByEdge.TryGetValue(edge, out var lane) && lane.Shape.Count > 0)
            {
                result.Add((edge, ArcLengthMidpoint(lane.Shape)));
            }
        }

        return result;
    }

    // The point at half the total arc length along a polyline (stays ON the polyline, unlike an endpoint
    // average, which matters for a bent sidewalk).
    private static Vec2 ArcLengthMidpoint(IReadOnlyList<Vec2> shape)
    {
        if (shape.Count == 1)
        {
            return shape[0];
        }

        var total = 0.0;
        for (var i = 1; i < shape.Count; i++)
        {
            total += (shape[i] - shape[i - 1]).Abs;
        }

        var half = total * 0.5;
        var acc = 0.0;
        for (var i = 1; i < shape.Count; i++)
        {
            var seg = (shape[i] - shape[i - 1]).Abs;
            if (acc + seg >= half)
            {
                var t = seg > 0.0 ? (half - acc) / seg : 0.0;
                return shape[i - 1] + (shape[i] - shape[i - 1]) * t;
            }

            acc += seg;
        }

        return shape[^1];
    }
}
