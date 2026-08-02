using Sim.Core.Orca;
using Sim.Pedestrians.Navigation.Bake;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Navigation;

// PEDCROSS Q5 (docs/PEDCROSS-OPTIONS.md §8, §5b.4): does the baked LOW-POWER route graph ever put a
// pedestrian on the carriageway outside a crossing?
//
// WHY THIS IS THE LOAD-BEARING TEST FOR OPTION D. A low-power ped's pose is a PURE FUNCTION of its
// path (PathArcMotion: arc length = speed * max(0, now - startTime)) -- there is no solver and no
// force, so it cannot deviate. Its safety therefore rests entirely on the PATH: cars brake for a ped
// on a crossing (CrossingOccupancySource -> binder 13/16), but nothing whatsoever protects a ped that
// is scripted across bare asphalt. If the router can emit such a path, a low-power ped walks into
// traffic deterministically and no downstream mechanism saves it.
//
// THE FORMULATION IS CHEAPER THAN IT LOOKS, and deliberately needs no vehicle geometry. Crossings are
// themselves baked walkable polygons (BakedPolygonKind.Crossing), so
//
//      "crosses the road only at a crosswalk"   ==   "never leaves the walkable union"
//
// A legitimate road traversal is inside a Crossing polygon and therefore inside the union; a
// sidewalk-to-sidewalk shortcut over asphalt is outside every polygon. So the invariant is a dense
// point-sampling of every path against SumoWalkableSpace.Contains, with the polygon KIND reported so
// road traversals can be confirmed to happen only on Crossing polygons.
//
// Sampling is dense (SampleStep) rather than per-vertex on purpose: a path is a polyline, and a
// shortcut is a SEGMENT whose endpoints can both be legally on a sidewalk while its middle is on the
// carriageway. Testing only vertices would report clean on exactly the failure this exists to find.
public class LowPowerRouteContainmentTests
{
    // Dense enough that a 3.2 m carriageway cannot be stepped over between samples.
    private const double SampleStep = 0.25;

    private readonly ITestOutputHelper _out;

    public LowPowerRouteContainmentTests(ITestOutputHelper output) => _out = output;

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static (SumoNavMesh Nav, SumoWalkableSpace Space, IReadOnlyList<BakedPolygon> Polys) LoadPoc0()
    {
        var net = PedNetworkParser.Load(FixturePath("net.net.xml"), FixturePath("walkable.add.xml"));
        var polys = WalkablePolygonBaker.Bake(net);
        var space = new SumoWalkableSpace(polys);
        return (new SumoNavMesh(polys, space, net.PedConnections), space, polys);
    }

    // Origin/destination candidates: every baked polygon's centroid. That is a deterministic, geometry-
    // derived O/D set covering every sidewalk strip, walkingarea and crossing in the net -- so the path
    // set exercises every arm-to-arm and corner-to-corner journey the router can produce, not a
    // hand-picked pair that might dodge the interesting case.
    private static List<(int From, int To, IReadOnlyList<Vec2> Path)> AllPaths(
        SumoNavMesh nav, IReadOnlyList<BakedPolygon> polys)
    {
        var paths = new List<(int, int, IReadOnlyList<Vec2>)>();
        for (var i = 0; i < polys.Count; i++)
        {
            for (var j = 0; j < polys.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var path = nav.FindPath(polys[i].Centroid, polys[j].Centroid, out _);
                if (path is { Count: >= 2 })
                {
                    paths.Add((i, j, path));
                }
            }
        }

        return paths;
    }

    private static IEnumerable<Vec2> DenseSamples(IReadOnlyList<Vec2> path)
    {
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            var len = Math.Sqrt((b - a).AbsSq);
            var steps = Math.Max(1, (int)Math.Ceiling(len / SampleStep));
            for (var s = 0; s <= steps; s++)
            {
                yield return a + (b - a) * ((double)s / steps);
            }
        }
    }

    [Fact]
    public void EveryRoutedPath_StaysInsideTheWalkableUnion()
    {
        var (nav, space, polys) = LoadPoc0();
        var paths = AllPaths(nav, polys);

        Assert.True(paths.Count > 0, "no paths were routed -- the fixture or the navmesh is broken, and a "
                                     + "vacuous pass here would be worse than a failure.");

        var totalSamples = 0;
        var offWalkable = 0;
        var worstPath = (From: -1, To: -1, Count: 0);
        Vec2 firstBad = default;

        foreach (var (from, to, path) in paths)
        {
            var badHere = 0;
            foreach (var p in DenseSamples(path))
            {
                totalSamples++;
                if (!space.Contains(p))
                {
                    if (offWalkable == 0)
                    {
                        firstBad = p;
                    }

                    offWalkable++;
                    badHere++;
                }
            }

            if (badHere > worstPath.Count)
            {
                worstPath = (from, to, badHere);
            }
        }

        _out.WriteLine($"Q5: {paths.Count} paths, {totalSamples} samples at {SampleStep} m");
        _out.WriteLine($"Q5: off-walkable samples = {offWalkable}");
        if (offWalkable > 0)
        {
            _out.WriteLine($"Q5: worst path polys {worstPath.From}->{worstPath.To} with {worstPath.Count} bad samples");
            _out.WriteLine($"Q5: first off-walkable point = ({firstBad.X:F3}, {firstBad.Y:F3})");
        }

        Assert.Equal(0, offWalkable);
    }

    // The companion half of the invariant, and the one that actually says "only at a crosswalk":
    // report which polygon KIND each sample falls in. A path that legitimately crosses a road does so
    // inside a Crossing polygon; if the Crossing count were zero across every arm-to-arm journey, the
    // union-containment test above would pass while the router was quietly avoiding roads altogether,
    // which would make it a much weaker result than it looks.
    [Fact]
    public void RoadTraversals_HappenOnlyOnCrossingPolygons()
    {
        var (nav, space, polys) = LoadPoc0();
        var paths = AllPaths(nav, polys);

        var byKind = new Dictionary<BakedPolygonKind, int>();
        var uncontained = 0;

        foreach (var (_, _, path) in paths)
        {
            foreach (var p in DenseSamples(path))
            {
                // The PRODUCTION predicate decides contained-vs-not, so this assertion measures the
                // real containment rule rather than the local ray-cast below.
                if (!space.Contains(p))
                {
                    uncontained++;
                    continue;
                }

                var kind = ClassifyKind(polys, p);
                if (kind is not null)
                {
                    byKind[kind.Value] = byKind.GetValueOrDefault(kind.Value) + 1;
                }
            }
        }

        foreach (var (kind, n) in byKind.OrderByDescending(kv => kv.Value))
        {
            _out.WriteLine($"Q5 kind {kind,-16} {n}");
        }

        _out.WriteLine($"Q5 uncontained      {uncontained}");

        Assert.Equal(0, uncontained);
        Assert.True(byKind.GetValueOrDefault(BakedPolygonKind.Crossing) > 0,
            "no sample fell on a Crossing polygon -- the O/D set never actually crosses a road, so the "
            + "containment result above says nothing about road traversal.");
    }

    private static BakedPolygonKind? ClassifyKind(IReadOnlyList<BakedPolygon> polys, Vec2 p)
    {
        // Prefer Crossing when a point is inside several polygons (crossings abut walkingareas, and the
        // overlap is exactly the kerb): the question is "is this traversal on a crosswalk", so the
        // crossing membership is the one that answers it.
        BakedPolygonKind? found = null;
        foreach (var poly in polys)
        {
            if (!PointInPolygon(poly.Vertices, p))
            {
                continue;
            }

            if (poly.Kind == BakedPolygonKind.Crossing)
            {
                return BakedPolygonKind.Crossing;
            }

            found ??= poly.Kind;
        }

        return found;
    }

    // NEGATIVE CONTROL -- without this the two tests above are worthless. They both report "0 samples
    // outside the walkable union", which is only meaningful if SumoWalkableSpace.Contains is capable of
    // returning false in the first place. A degenerate or over-large bake would make every point
    // "contained" and both tests would pass while measuring nothing. CLAUDE.md §Measurement discipline
    // item 1: the result I wanted is exactly the one to check against another surface.
    [Fact]
    public void WalkableSpace_ActuallyRejectsOffPavementPoints()
    {
        var (_, space, polys) = LoadPoc0();

        // Probe a coarse grid over the net's bounding box and count how much of it is NOT walkable.
        // In a road network the walkable union (sidewalk strips + crossings + walkingareas) is a small
        // fraction of the bounding box -- carriageway, buildings and open ground are all outside it.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var poly in polys)
        {
            foreach (var v in poly.Vertices)
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
            }
        }

        var inside = 0;
        var outside = 0;
        const int n = 120;
        for (var i = 0; i <= n; i++)
        {
            for (var j = 0; j <= n; j++)
            {
                var p = new Vec2(minX + (maxX - minX) * i / n, minY + (maxY - minY) * j / n);
                if (space.Contains(p))
                {
                    inside++;
                }
                else
                {
                    outside++;
                }
            }
        }

        var frac = (double)outside / (inside + outside);
        _out.WriteLine($"Q5 control: bbox [{minX:F1},{minY:F1}]..[{maxX:F1},{maxY:F1}]");
        _out.WriteLine($"Q5 control: grid inside={inside} outside={outside} ({frac:P1} non-walkable)");

        // A real road net is mostly NOT pavement. If this ever drops near zero the bake has gone
        // degenerate and the containment results above must be treated as vacuous.
        Assert.True(frac > 0.5,
            $"only {frac:P1} of the bounding box is non-walkable -- Contains() is barely discriminating, "
            + "so the zero-off-walkable results in this class prove nothing.");
    }

    // Local even-odd ray cast. Deliberately NOT the production predicate: it is used only to attribute a
    // sample to a polygon KIND (an informational histogram). Contained-vs-not is decided by
    // SumoWalkableSpace.Contains above, so a disagreement at a boundary cannot turn a real containment
    // failure into a pass.
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
}
