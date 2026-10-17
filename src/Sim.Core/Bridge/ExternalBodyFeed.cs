using Sim.Ingest;

namespace Sim.Core.Bridge;

// HROT H12 (docs/HROT-INTEGRATION-DESIGN.md): a host's OWN moving bodies -- military vehicles, soldiers --
// fed into a running lane engine so its cars react to them.
public enum ExternalBodyKind
{
    Vehicle,
    Person,
}

// One host body, in NET coordinates (metres, the net's own x/y frame) with its world velocity (m/s). (X, Y) is the
// body's CENTRE. Length/Width are its footprint; the record carries no heading, so for lane registration the
// footprint is taken as aligned to each lane it is tested against (see ExternalBodyFeed). A person's disc radius
// is max(Length, Width) / 2 (DefaultPersonRadius when both are <= 0).
public readonly record struct ExternalBody(
    long Id,
    ExternalBodyKind Kind,
    double X,
    double Y,
    double Vx,
    double Vy,
    double Length,
    double Width);

// Turns a host's body list into the two shapes the lane engine already understands, and keeps them in sync
// across calls:
//
//   VEHICLE on the road -> a moving lane obstacle (Engine.AddMovingObstacle / UpdateObstacle / RemoveObstacle) on
//       EVERY vehicle lane its footprint overlaps. Overlap, with the footprint lane-aligned:
//           |latOffset| < lane.Width/2 + body.Width/2   AND   overshoot < body.Length/2
//       where (offset, latOffset, distance) = LaneProjection.Project(lane.Shape, X, Y) and
//       overshoot = sqrt(max(0, distance^2 - latOffset^2)) is how far the centre lies beyond the lane's start/end
//       along its axis (0 for any point that projects inside the lane). The obstacle is
//           frontPos = clamp(offset + Length/2, 0, lane.Length), length = Length,
//           speed    = max(0, velocity . laneDirection(offset))   (an oncoming body is a stopped obstacle),
//           latPos   = latOffset, width = Width, latSpeed = velocity . laneLeftNormal(offset),
//           maxDecel = ObstacleMaxDecel.
//   VEHICLE off the road (overlaps no vehicle lane) and PERSON -> crowd discs (WorldDisc), served to
//       Engine.CrowdSource through ICrowdFootprintSource.QueryNear, nearest-first (WorldDiscQuery.InsertNearest).
//       A person is one disc. An off-road vehicle is a chain of radius-Width/2 discs along its heading (its
//       velocity when moving, else the nearest vehicle lane's direction), or one radius-max(L,W)/2 disc when it
//       has neither.
//
// The "within 3 m (+ half the lane width) of a lane centre" on-road test the HROT lean proposed is SUBSUMED by the
// overlap rule: a vehicle that overlaps no lane is a disc whether it is 0.5 m or 50 m off the lane edge, so a
// separate distance threshold would change no outcome. (For a 2.5 m-wide body on a 3.2 m lane, overlap ends at
// 2.85 m from the centre line, inside the 4.6 m that lean named.)
//
// Apply diffs against the previous call by body Id: new bodies are added, existing ones updated in place (an
// obstacle handle per (body, lane) survives across calls), and every obstacle of a body that LEFT a lane, went
// off-road, changed kind or footprint, or disappeared from the list is removed. An empty span removes everything.
//
// PedestrianDiscs is every body as discs (on-road vehicles included -- they are lane obstacles to the cars, but
// still bodies a pedestrian must walk around), for a pedestrian crowd's external-obstacle input.
//
// Call Apply only between engine steps, on the stepping thread (it mutates the engine's obstacle store, which is
// single-threaded -- ObstacleStore's own contract). QueryNear is read-only and safe during the parallel plan.
public sealed class ExternalBodyFeed : ICrowdFootprintSource
{
    public const double ObstacleMaxDecel = 7.5;
    public const double DefaultPersonRadius = 0.3;
    public const int MaxDiscsPerBody = 8;

    // Below this speed a body's velocity gives no usable heading.
    private const double HeadingMinSpeed = 0.1;

    private readonly Engine _engine;
    private readonly NetworkModel _network;
    private readonly LaneSpatialIndex _index;
    private readonly double _maxVehicleLaneHalfWidth;

    private readonly Dictionary<long, BodyState> _bodies = new();
    private readonly List<long> _goneScratch = new();
    private readonly List<int> _nearScratch = new();
    private readonly List<LaneHit> _hitScratch = new();
    private readonly List<WorldDisc> _crowdDiscs = new();
    private readonly List<WorldDisc> _pedDiscs = new();
    private int _generation;
    private int _obstacleCount;

    public ExternalBodyFeed(Engine engine, LaneSpatialIndex? index = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _network = engine.LoadedNetwork
            ?? throw new InvalidOperationException("LoadScenario/LoadNetwork must be called before creating an ExternalBodyFeed.");
        _index = index ?? new LaneSpatialIndex(_network);
        if (_index.LaneCount != _network.LanesByHandle.Count)
        {
            throw new ArgumentException("the LaneSpatialIndex was built from a different network.", nameof(index));
        }

        foreach (var lane in _network.LanesByHandle)
        {
            if (lane.AllowsRoadVehicle)
            {
                _maxVehicleLaneHalfWidth = Math.Max(_maxVehicleLaneHalfWidth, lane.Width * 0.5);
            }
        }
    }

    public LaneSpatialIndex Index => _index;

    // Bodies in the last Apply.
    public int BodyCount => _bodies.Count;

    // Live lane obstacles this feed owns (one per (on-road vehicle, overlapped lane)).
    public int ObstacleCount => _obstacleCount;

    // Discs served to the lane engine via QueryNear (persons + off-road vehicles).
    public int DiscCount => _crowdDiscs.Count;

    public IReadOnlyList<WorldDisc> Discs => _crowdDiscs;

    // Every body as discs, for a pedestrian crowd (see the header).
    public IReadOnlyList<WorldDisc> PedestrianDiscs => _pedDiscs;

    // The lanes (dense Lane.Handle, ascending) body `id` is registered on as an obstacle. Cleared first; empty when
    // the body is unknown, a person, or off-road. Returns whether the body is known.
    public bool LanesOf(long id, List<int> laneHandlesOut)
    {
        laneHandlesOut.Clear();
        if (!_bodies.TryGetValue(id, out var state))
        {
            return false;
        }

        foreach (var o in state.Obstacles)
        {
            laneHandlesOut.Add(o.Lane);
        }

        return true;
    }

    public void Clear() => Apply(ReadOnlySpan<ExternalBody>.Empty);

    public void Apply(ReadOnlySpan<ExternalBody> bodies)
    {
        _generation++;
        _crowdDiscs.Clear();
        _pedDiscs.Clear();

        foreach (var body in bodies)
        {
            if (!_bodies.TryGetValue(body.Id, out var state))
            {
                state = new BodyState();
                _bodies[body.Id] = state;
            }

            // A footprint or kind change re-registers from scratch (UpdateObstacle cannot change length/width).
            if (state.Generation != 0 && (state.Kind != body.Kind || state.Length != body.Length || state.Width != body.Width))
            {
                RemoveAllObstacles(state);
            }

            state.Generation = _generation;
            state.Kind = body.Kind;
            state.Length = body.Length;
            state.Width = body.Width;

            if (body.Kind == ExternalBodyKind.Person)
            {
                RemoveAllObstacles(state);
                var r = Math.Max(body.Length, body.Width) * 0.5;
                var disc = new WorldDisc(body.X, body.Y, body.Vx, body.Vy, r > 0.0 ? r : DefaultPersonRadius);
                _crowdDiscs.Add(disc);
                _pedDiscs.Add(disc);
                continue;
            }

            var (hasLaneHeading, laneHx, laneHy) = FindOverlappedLanes(body);
            SyncObstacles(state, body);

            var (hasHeading, hx, hy) = HeadingOf(body, hasLaneHeading, laneHx, laneHy);
            if (_hitScratch.Count == 0)
            {
                AddDiscChain(_crowdDiscs, body, hasHeading, hx, hy);
            }

            AddDiscChain(_pedDiscs, body, hasHeading, hx, hy);
        }

        // Bodies absent from this call: drop their obstacles and forget them.
        _goneScratch.Clear();
        foreach (var kv in _bodies)
        {
            if (kv.Value.Generation != _generation)
            {
                _goneScratch.Add(kv.Key);
            }
        }

        foreach (var id in _goneScratch)
        {
            RemoveAllObstacles(_bodies[id]);
            _bodies.Remove(id);
        }
    }

    public int QueryNear(double x, double y, double radius, Span<WorldDisc> into)
    {
        if (!(radius >= 0.0))
        {
            return 0;
        }

        var n = 0;
        var rSq = radius * radius;
        foreach (var disc in _crowdDiscs)
        {
            var dx = disc.X - x;
            var dy = disc.Y - y;
            if (dx * dx + dy * dy <= rSq)
            {
                n = WorldDiscQuery.InsertNearest(into, n, disc, x, y);
            }
        }

        return n;
    }

    // Fill _hitScratch with every vehicle lane the body overlaps (ascending lane handle). Also returns the
    // direction of the NEAREST vehicle lane found in the search (overlapped or not), for an off-road heading.
    private (bool Has, double Hx, double Hy) FindOverlappedLanes(in ExternalBody body)
    {
        _hitScratch.Clear();
        var halfLen = Math.Max(body.Length, 0.0) * 0.5;
        var halfWid = Math.Max(body.Width, 0.0) * 0.5;

        // Any overlapping lane's centre line is within sqrt((hw + W/2)^2 + (L/2)^2) <= hw + W/2 + L/2 of the centre.
        _index.LanesNear(body.X, body.Y, _maxVehicleLaneHalfWidth + halfWid + halfLen, _nearScratch);

        var nearestDist = double.PositiveInfinity;
        var has = false;
        double nhx = 0.0, nhy = 0.0;
        foreach (var h in _nearScratch)
        {
            var lane = _network.LanesByHandle[h];
            if (!lane.AllowsRoadVehicle || lane.Shape.Count == 0)
            {
                continue;
            }

            var (offset, lat, dist) = LaneProjection.Project(lane.Shape, body.X, body.Y);
            var (hx, hy) = LaneDirection(lane, offset);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                has = lane.Shape.Count > 1;
                nhx = hx;
                nhy = hy;
            }

            var overshoot = Math.Sqrt(Math.Max(0.0, (dist * dist) - (lat * lat)));
            if (Math.Abs(lat) < (lane.Width * 0.5) + halfWid && overshoot < halfLen)
            {
                var along = (body.Vx * hx) + (body.Vy * hy);
                var latVel = (body.Vx * -hy) + (body.Vy * hx);
                var front = Math.Clamp(offset + halfLen, 0.0, lane.Length);
                _hitScratch.Add(new LaneHit(h, front, Math.Max(0.0, along), lat, latVel));
            }
        }

        return (has, nhx, nhy);
    }

    // Make `state`'s obstacles exactly _hitScratch: update kept lanes, add new ones, remove left ones. Both lists
    // are in ascending lane order, so this is one merge pass and the result stays sorted.
    private void SyncObstacles(BodyState state, in ExternalBody body)
    {
        var old = state.Obstacles;
        var merged = state.Spare;
        merged.Clear();
        var i = 0;
        foreach (var hit in _hitScratch)
        {
            while (i < old.Count && old[i].Lane < hit.Lane)
            {
                _engine.RemoveObstacle(old[i].Handle);
                _obstacleCount--;
                i++;
            }

            if (i < old.Count && old[i].Lane == hit.Lane)
            {
                _engine.UpdateObstacle(old[i].Handle, hit.FrontPos, hit.Speed, hit.LatPos, hit.LatSpeed);
                merged.Add(old[i]);
                i++;
            }
            else
            {
                var handle = _engine.AddMovingObstacle(
                    hit.Lane, hit.FrontPos, body.Length, hit.Speed, ObstacleMaxDecel,
                    latPos: hit.LatPos, width: body.Width, latSpeed: hit.LatSpeed);
                merged.Add(new LaneObstacle(hit.Lane, handle));
                _obstacleCount++;
            }
        }

        for (; i < old.Count; i++)
        {
            _engine.RemoveObstacle(old[i].Handle);
            _obstacleCount--;
        }

        state.Spare = old;
        state.Obstacles = merged;
    }

    private void RemoveAllObstacles(BodyState state)
    {
        foreach (var o in state.Obstacles)
        {
            _engine.RemoveObstacle(o.Handle);
            _obstacleCount--;
        }

        state.Obstacles.Clear();
    }

    private static (bool Has, double Hx, double Hy) HeadingOf(in ExternalBody body, bool hasLane, double laneHx, double laneHy)
    {
        var speed = Math.Sqrt((body.Vx * body.Vx) + (body.Vy * body.Vy));
        if (speed > HeadingMinSpeed)
        {
            return (true, body.Vx / speed, body.Vy / speed);
        }

        return (hasLane, laneHx, laneHy);
    }

    // The body's footprint as discs: radius Width/2 centres spread evenly over the length (inset so the end discs
    // touch the bumpers), along the heading; one circumscribing disc when there is no heading or L <= W.
    private static void AddDiscChain(List<WorldDisc> into, in ExternalBody body, bool hasHeading, double hx, double hy)
    {
        var length = Math.Max(body.Length, 0.0);
        var width = Math.Max(body.Width, 0.0);
        if (!hasHeading || length <= width || width <= 0.0)
        {
            var r = Math.Max(length, width) * 0.5;
            into.Add(new WorldDisc(body.X, body.Y, body.Vx, body.Vy, r > 0.0 ? r : DefaultPersonRadius));
            return;
        }

        var half = width * 0.5;
        var span = length - width;                          // distance between the first and last centre
        var count = Math.Clamp((int)Math.Ceiling(span / half) + 1, 2, MaxDiscsPerBody);
        var spacing = span / (count - 1);
        for (var d = 0; d < count; d++)
        {
            var s = (span * 0.5) - (d * spacing);
            into.Add(new WorldDisc(body.X + (hx * s), body.Y + (hy * s), body.Vx, body.Vy, half));
        }
    }

    // Unit direction of travel of `lane` at arc-length `offset` (LaneGeometry's naviDegree, 0 = +Y, clockwise).
    private static (double Hx, double Hy) LaneDirection(Lane lane, double offset)
    {
        var (_, _, naviDeg) = LaneGeometry.PositionAtOffset(lane.Shape, offset);
        var navi = naviDeg * Math.PI / 180.0;
        return (Math.Sin(navi), Math.Cos(navi));
    }

    private readonly record struct LaneHit(int Lane, double FrontPos, double Speed, double LatPos, double LatSpeed);

    private readonly record struct LaneObstacle(int Lane, ObstacleHandle Handle);

    private sealed class BodyState
    {
        public int Generation;
        public ExternalBodyKind Kind;
        public double Length;
        public double Width;
        public List<LaneObstacle> Obstacles = new();
        public List<LaneObstacle> Spare = new();
    }
}
