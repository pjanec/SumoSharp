using Sim.Core.Orca;
using Sim.Pedestrians.Navigation;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Navigation;

// PEDCROSS Q1 (docs/PEDCROSS-OPTIONS.md §8, §2.3): do ORCA pedestrians leave the pavement under crowd
// pressure, and does feeding the walkable boundary into the crowd stop it?
//
// THE SETUP THIS PROBES. PedLodManager's high-power OrcaCrowd is constructed with NO static obstacles
// -- nothing calls AddObstacle on it. The owner reports peds "running away completely… not afraid of
// jumping into the car lanes just because they need to avoid others". An unbounded reciprocal solve
// would produce exactly that.
//
// ⚠ WHY THIS IS NOT SIMPLY "CALL AddObstacle(BoundarySegments)". SumoWalkableSpace's own header says
// its boundary set is per-polygon, so an edge shared by two abutting walkable polygons is emitted
// TWICE and "walling it off would block agents from ever crossing it" -- a shared edge is a navigation
// PORTAL. It names the union-boundary computation as future work. This probe therefore implements the
// dedupe (an edge seen from both sides is interior; keep only edges seen once) and measures BOTH
// halves of the trade:
//
//      (a) containment  -- agent-steps outside the walkable union, and
//      (b) mobility     -- do the agents still get anywhere, or are they walled in?
//
// Reporting only (a) would make a wall-everything bug look like a total success.
public class OrcaWalkableContainmentTests
{
    private readonly ITestOutputHelper _out;

    public OrcaWalkableContainmentTests(ITestOutputHelper output) => _out = output;

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static (SumoNavMesh Nav, SumoWalkableSpace Space, IReadOnlyList<BakedPolygon> Polys) LoadPoc0()
    {
        var net = PedNetworkParser.Load(FixturePath("net.net.xml"), FixturePath("walkable.add.xml"));
        var polys = WalkablePolygonBaker.Bake(net);
        var space = new SumoWalkableSpace(polys);
        return (new SumoNavMesh(polys, space, net.PedConnections), space, polys);
    }

    // The union boundary: every polygon edge that is NOT shared with another walkable polygon.
    // Quantised endpoint keys, orientation-insensitive, so an edge traversed A->B by one polygon and
    // B->A by its neighbour collapses to the same bucket. This is the "future work" SumoWalkableSpace's
    // header defers; whether exact-endpoint matching is ENOUGH (abutting polygons may share only PART
    // of an edge) is one of the things this probe measures, via the mobility half.
    private static List<WallSegment> UnionBoundary(IReadOnlyList<BakedPolygon> polys, double quant = 1e-6)
    {
        static (long, long) Key(Vec2 v, double q) =>
            ((long)Math.Round(v.X / q), (long)Math.Round(v.Y / q));

        var buckets = new Dictionary<((long, long), (long, long)), (WallSegment Seg, int Count)>();
        foreach (var poly in polys)
        {
            var v = poly.Vertices;
            for (var i = 0; i < v.Count; i++)
            {
                var a = v[i];
                var b = v[(i + 1) % v.Count];
                var ka = Key(a, quant);
                var kb = Key(b, quant);
                var key = ka.CompareTo(kb) <= 0 ? (ka, kb) : (kb, ka);
                buckets[key] = buckets.TryGetValue(key, out var cur)
                    ? (cur.Seg, cur.Count + 1)
                    : (new WallSegment(a, b), 1);
            }
        }

        return buckets.Values.Where(e => e.Count == 1).Select(e => e.Seg).ToList();
    }

    // Exact-endpoint dedupe is not enough, and this is why: abutting walkable polygons frequently share
    // only PART of an edge (a sidewalk strip meeting a wider walkingarea), so neither edge is a
    // duplicate of the other and BOTH survive -- leaving a wall straight across a navigation portal.
    // The stronger test is geometric rather than combinatorial: an edge whose MIDPOINT lies strictly
    // inside some OTHER walkable polygon is interior by definition, whoever emitted it.
    private static List<WallSegment> UnionBoundaryV2(IReadOnlyList<BakedPolygon> polys)
    {
        var kept = new List<WallSegment>();
        foreach (var seg in UnionBoundary(polys))
        {
            var mid = (seg.A + seg.B) * 0.5;
            var interior = false;
            foreach (var poly in polys)
            {
                if (PointInPolygon(poly.Vertices, mid))
                {
                    interior = true;
                    break;
                }
            }

            if (!interior)
            {
                kept.Add(seg);
            }
        }

        return kept;
    }

    private static bool PointInPolygon(IReadOnlyList<Vec2> v, Vec2 p)
    {
        var inside = false;
        for (int i = 0, j = v.Count - 1; i < v.Count; j = i++)
        {
            if (v[i].Y > p.Y != v[j].Y > p.Y &&
                p.X < (v[j].X - v[i].X) * (p.Y - v[i].Y) / (v[j].Y - v[i].Y) + v[i].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    // ⚠ THE FIRST VERSION OF THIS HARNESS WAS INVALID, and the correction is the point.
    //
    // It gave every agent a straight-line `SetGoal` to a point across the junction, with no router. The
    // agents then walked STRAIGHT OVER THE CARRIAGEWAY because that is where they were aimed, and the
    // resulting "37% of agent-steps off-walkable" measured the harness's own goal assignment, not ORCA
    // shoving anyone anywhere. Adding walls then "fixed" containment purely by stopping agents dead --
    // mobility collapsed because they had no route around, not because a portal was walled.
    //
    // That is precisely CLAUDE.md §Measurement discipline item 6's "a metric that selects its own
    // answer", and it would have produced a confident, wrong input to the PEDCROSS decision.
    //
    // The valid form drives agents the way the real system does: a routed path from SumoNavMesh, followed
    // by PedRouteController + WaypointFollower -- the same pair PedLodManager uses (PedLodManager.cs:220).
    // Every agent therefore has a LEGAL route that stays on pavement by construction (Q5 proves the router
    // only ever crosses at crossings), so any off-walkable sample is ORCA deviating from a legal path,
    // which is the actual question.
    private enum Boundary { None, EndpointDedupe, MidpointFiltered }

    private (int OffWalkable, int Steps, double MeanProgress, double MaxDepth, double P95Depth) RunCrowd(
        Boundary mode, int agents = 40)
    {
        var (nav, space, polys) = LoadPoc0();

        var crowd = new OrcaCrowd { MaxNeighbours = 8 };
        var walls = mode switch
        {
            Boundary.EndpointDedupe => UnionBoundary(polys),
            Boundary.MidpointFiltered => UnionBoundaryV2(polys),
            _ => new List<WallSegment>(),
        };
        foreach (var w in walls)
        {
            crowd.AddObstacle(new[] { w.A, w.B });
        }

        // Spawn on sidewalk strips, goal on the sidewalk strip "opposite" in bake order, so routes
        // genuinely traverse the junction rather than milling in place.
        var sidewalks = polys.Where(p => p.Kind == BakedPolygonKind.SidewalkSegment).ToList();
        Assert.True(sidewalks.Count >= 2, "fixture has too few sidewalk polygons to build a crossing flow.");

        var controller = new PedRouteController(crowd, new WaypointFollower(), arriveRadius: 0.6);

        var handles = new List<(OrcaHandle H, IReadOnlyList<Vec2> Path)>();
        for (var i = 0; i < agents; i++)
        {
            var src = sidewalks[i % sidewalks.Count];
            var dst = sidewalks[(i + sidewalks.Count / 2) % sidewalks.Count];

            // Spread starts along the source spine so agents are not co-located at t=0 (ORCA resolves
            // that by shoving, which would manufacture the very effect being measured).
            var spine = src.Spine ?? src.Vertices;
            var t = (double)(i / sidewalks.Count + 1) / (agents / sidewalks.Count + 2);
            var start = SampleAlong(spine, t);
            if (!space.Contains(start))
            {
                start = space.ClampToWalkable(start);
            }

            var path = nav.FindPath(start, dst.Centroid, out _);
            if (path is not { Count: >= 2 })
            {
                continue;
            }

            var h = crowd.Add(start, radius: 0.3, maxSpeed: 1.4, path[^1]);
            controller.AddRoute(h, path, maxSpeed: 1.4);
            handles.Add((h, path));
        }

        Assert.True(handles.Count >= agents / 2,
            $"only {handles.Count}/{agents} agents got a route -- too few to load the junction.");

        var offWalkable = 0;
        var steps = 0;
        // EXCURSION DEPTH is the believability question, not excursion COUNT. Clipping a kerb corner by
        // 0.1 m and standing in a live traffic lane 2 m out are the same event by frequency and utterly
        // different to look at. Depth = distance from the off-pavement position back to the nearest
        // point of the walkable union (SumoWalkableSpace.ClampToWalkable).
        var depths = new List<double>();
        const double dt = 0.1;
        for (var s = 0; s < 900; s++)
        {
            controller.Update();
            crowd.Step(dt);
            steps++;
            foreach (var (h, _) in handles)
            {
                var p = crowd.Position(h);
                if (!space.Contains(p))
                {
                    offWalkable++;
                    depths.Add(Math.Sqrt((space.ClampToWalkable(p) - p).AbsSq));
                }
            }
        }

        depths.Sort();
        var maxDepth = depths.Count == 0 ? 0.0 : depths[^1];
        var p95Depth = depths.Count == 0 ? 0.0 : depths[(int)(depths.Count * 0.95)];

        // Mobility measured against the ROUTE, not a straight line: fraction of agents that completed
        // their path. A walled-in population scores ~0 with perfect containment, which is exactly the
        // failure the SumoWalkableSpace header warns about.
        var done = handles.Count(x => controller.IsRouteComplete(x.H));

        return (offWalkable, steps * handles.Count, (double)done / handles.Count, maxDepth, p95Depth);
    }

    private static Vec2 SampleAlong(IReadOnlyList<Vec2> line, double t)
    {
        if (line.Count == 1)
        {
            return line[0];
        }

        var total = 0.0;
        for (var i = 0; i + 1 < line.Count; i++)
        {
            total += Math.Sqrt((line[i + 1] - line[i]).AbsSq);
        }

        var target = total * Math.Clamp(t, 0.0, 1.0);
        var acc = 0.0;
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var seg = Math.Sqrt((line[i + 1] - line[i]).AbsSq);
            if (acc + seg >= target && seg > 1e-9)
            {
                return line[i] + (line[i + 1] - line[i]) * ((target - acc) / seg);
            }

            acc += seg;
        }

        return line[^1];
    }

    [Fact]
    public void Q1_BoundaryWiring_ContainmentAndMobility()
    {
        var (_, _, polys) = LoadPoc0();

        // Density sweep on the UNBOUNDED crowd -- the configuration PedLodManager actually ships. The
        // owner's report is specifically about crowd pressure, so a single population size cannot answer
        // it: the question is whether excursions APPEAR as density rises.
        foreach (var n in new[] { 20, 40, 80, 160, 240 })
        {
            var r = RunCrowd(Boundary.None, n);
            _out.WriteLine($"Q1 density n={n,3}  off-walkable {(double)r.OffWalkable / r.Steps:P3}  "
                           + $"depth p95 {r.P95Depth:F3} m  max {r.MaxDepth:F3} m  "
                           + $"routesCompleted {r.MeanProgress:P1}");
        }

        var none = RunCrowd(Boundary.None);
        var v1 = RunCrowd(Boundary.EndpointDedupe);
        var v2 = RunCrowd(Boundary.MidpointFiltered);

        var edges = polys.Sum(p => p.Vertices.Count);
        var u1 = UnionBoundary(polys);
        var u2 = UnionBoundaryV2(polys);

        _out.WriteLine($"Q1: polygons={polys.Count} rawEdges={edges}");
        _out.WriteLine($"Q1: endpoint-dedupe kept {u1.Count}; midpoint-filtered kept {u2.Count} "
                       + $"(a further {u1.Count - u2.Count} were interior portals the dedupe missed)");
        _out.WriteLine($"Q1: NO boundary        off-walkable {(double)none.OffWalkable / none.Steps:P2}  routesCompleted {none.MeanProgress:P1}");
        _out.WriteLine($"Q1: endpoint-dedupe    off-walkable {(double)v1.OffWalkable / v1.Steps:P2}  routesCompleted {v1.MeanProgress:P1}");
        _out.WriteLine($"Q1: midpoint-filtered  off-walkable {(double)v2.OffWalkable / v2.Steps:P2}  routesCompleted {v2.MeanProgress:P1}");

        // Reported, not asserted, on the first run: this test exists to PRODUCE the numbers that decide
        // PEDCROSS Q1. Once the owner has read them the thresholds get pinned here.
        Assert.True(none.Steps > 0 && v1.Steps > 0 && v2.Steps > 0);
    }
}
