using System.Linq;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Lod;

// P1-1 (docs/PEDESTRIAN-TASKS.md; docs/PEDESTRIAN-DESIGN.md §5, use cases 1-4): the production
// movable, multi-source interest field that promotes POC-3's single caller-owned list. These tests
// are ADDITIONAL to (not a replacement for) PedLodManagerTests, which still cover the POC-3
// promotion/demotion success conditions unmodified in semantics -- only migrated to the new
// InterestField-based Step signature. These target InterestField itself, and PedLodManager driven by
// SEVERAL independently-moving sources at once (use case 4):
//   1. index correctness against a brute-force O(peds * sources) oracle, every step;
//   2. multi-source promotion ("any source") / demotion ("every source", with dwell) semantics;
//   3. no flapping when a hovering stimulus shares the field with other, unrelated sources;
//   4. determinism across independent runs of a multi-source scenario;
//   5. the per-query candidate count stays bounded as more spread-out sources are registered --
//      the "adding sources does not multiply the per-step promotion cost" success condition.
public class InterestFieldTests
{
    private readonly ITestOutputHelper _output;

    public InterestFieldTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const double Dt = 0.1;
    private const double DwellSeconds = 1.0;
    private const double MaxSpeed = 1.4;
    private const double Radius = 0.3;
    private const double ArriveRadius = 0.3;

    // Trivial straight-line navigation: these tests are about the interest field / LOD switching, not
    // routing, so no navmesh is needed (same convention as Sim.BenchPedLod's StraightLineNavigation).
    private sealed class StraightLineNavigation : IPedNavigation
    {
        public IReadOnlyList<Vec2>? FindPath(Vec2 start, Vec2 goal, out IReadOnlyList<int>? vertexSurfaces)
        {
        // Flat by explicit choice, not by inheriting a default: this provider has no surface model, so
        // it reports no provenance and zero elevation, and says so here where the choice is visible.
            vertexSurfaces = null;
            return new[] { start, goal };
        }

        public IReadOnlyList<double> ElevationsAlong(IReadOnlyList<Vec2> path, IReadOnlyList<int>? vertexSurfaces)
            => new double[path.Count];
    }

    // Brute-force O(sources) reference for a single query point: independently re-derives what
    // AnyWithinPromote / AllOutsideDemote SHOULD be, scanning every currently-registered source
    // directly via Snapshot() -- completely bypassing InterestField's grid. Run once per (ped, step)
    // this is the O(peds * sources) oracle the task calls for.
    private static InterestQueryResult BruteForceQuery(InterestField field, Vec2 pos)
    {
        var anyPromote = false;
        var allOutsideDemote = true;

        foreach (var (_, source, _) in field.Snapshot())
        {
            var dist = (pos - source.Position).Abs;
            if (dist <= source.DemoteRadius)
            {
                allOutsideDemote = false;
            }

            if (dist <= source.PromoteRadius)
            {
                anyPromote = true;
            }
        }

        return new InterestQueryResult(anyPromote, allOutsideDemote);
    }

    // ---- Success condition 1: multi-moving-source index correctness against the oracle -----------

    [Fact]
    public void Query_MatchesBruteForceOracle_AcrossManyIndependentlyMovingSourcesAndQueryPoints()
    {
        var field = new InterestField();

        // 5 sources, deliberately varied radii (a small static AoI next to a big camera-like reach),
        // each on its own independent orbit (own frequency/phase/amplitude) -- use case 4: "several
        // active interest sources at once, each moving independently".
        var specs = new (Vec2 Home, double Promote, double Demote, double Freq, double PhaseX, double PhaseY, double Amp)[]
        {
            (new Vec2(0, 0), 2.0, 4.0, 0.31, 0.0, 1.3, 15.0),
            (new Vec2(50, 10), 5.0, 9.0, 0.17, 0.7, 0.0, 30.0),
            (new Vec2(-40, 30), 1.0, 2.5, 0.53, 1.9, 0.4, 8.0),
            (new Vec2(20, -60), 10.0, 18.0, 0.11, 2.2, 3.1, 60.0),
            (new Vec2(-70, -20), 3.0, 6.0, 0.42, 0.5, 2.0, 20.0),
        };

        var ids = new List<InterestSourceId>();
        foreach (var s in specs)
        {
            ids.Add(field.Register(new InterestSource(s.Home, s.Promote, s.Demote)));
        }

        // 40 query points (a stand-in for "peds") spread over the same extent, each on its OWN
        // independent deterministic path too, so both sources and query points move every step.
        var queryPoints = new (Vec2 Home, double Freq, double PhaseX, double PhaseY, double Amp)[40];
        for (var i = 0; i < queryPoints.Length; i++)
        {
            var home = new Vec2((i % 10 * 20.0) - 100.0, (i / 10 * 20.0) - 60.0);
            queryPoints[i] = (home, 0.05 + (0.01 * i), i * 0.3, i * 0.7, 5.0 + (0.5 * i));
        }

        var mismatches = 0;
        const int steps = 150;
        for (var step = 0; step < steps; step++)
        {
            var t = step * Dt;

            for (var i = 0; i < specs.Length; i++)
            {
                var s = specs[i];
                var pos = s.Home + new Vec2(s.Amp * Math.Sin((s.Freq * t) + s.PhaseX), s.Amp * Math.Cos((s.Freq * t) + s.PhaseY));
                field.Move(ids[i], pos);
            }

            field.RebuildIndex();

            foreach (var q in queryPoints)
            {
                var pos = q.Home + new Vec2(q.Amp * Math.Sin((q.Freq * t) + q.PhaseX), q.Amp * Math.Cos((q.Freq * t) + q.PhaseY));

                var indexed = field.Query(pos);
                var oracle = BruteForceQuery(field, pos);

                if (indexed.AnyWithinPromote != oracle.AnyWithinPromote || indexed.AllOutsideDemote != oracle.AllOutsideDemote)
                {
                    mismatches++;
                    _output.WriteLine($"MISMATCH step={step} pos=({pos.X:F3},{pos.Y:F3}) indexed={indexed} oracle={oracle}");
                }
            }
        }

        _output.WriteLine($"[P1-1 measured] mismatches over {steps} steps x {queryPoints.Length} query points x {specs.Length} sources: {mismatches}");
        Assert.Equal(0, mismatches);
    }

    // ---- Success condition 1 (behavioral half): promote on ANY source, demote only outside EVERY ---

    [Fact]
    public void MultiSource_PromotesPedsInsideAnySource_DemotesOncePermanentlyOutsideAllSources()
    {
        var nav = new StraightLineNavigation();
        var publisher = new PedPublisher();
        var manager = new PedLodManager(nav, publisher, ArriveRadius, DwellSeconds);

        var homeA = new Vec2(0, 0);
        var homeB = new Vec2(100, 0);
        var homeC = new Vec2(200, 0); // never within reach of any source -- stays low-power throughout

        manager.AddPed(1, new[] { homeA, homeA + new Vec2(1, 0) }, MaxSpeed, Radius, now: 0.0);
        manager.AddPed(2, new[] { homeB, homeB + new Vec2(1, 0) }, MaxSpeed, Radius, now: 0.0);
        manager.AddPed(3, new[] { homeC, homeC + new Vec2(1, 0) }, MaxSpeed, Radius, now: 0.0);

        var field = new InterestField();
        var idA = field.Register(new InterestSource(homeA, promoteRadius: 3.0, demoteRadius: 6.0), InterestSourceKind.EntityAttached);
        var idB = field.Register(new InterestSource(homeB, promoteRadius: 3.0, demoteRadius: 6.0), InterestSourceKind.Camera);
        // A third, always-irrelevant intrinsic source, present at the same time (use case 4) but never
        // close enough to matter to any of the 3 peds above -- proves its mere presence doesn't perturb
        // anyone else's promotion/demotion.
        field.Register(new InterestSource(new Vec2(-500, -500), promoteRadius: 1.0, demoteRadius: 2.0), InterestSourceKind.Intrinsic);

        var noEntities = Array.Empty<WorldDisc>();
        var now = 0.0;

        // Phase 1: sources A/B independently orbit close to their own ped (different frequency/phase --
        // use case 4's "each moving independently") for long enough to clear the promote dwell.
        for (var i = 0; i < 60; i++)
        {
            field.Move(idA, homeA + new Vec2(0.5 * Math.Cos(0.9 * now), 0.5 * Math.Sin(0.9 * now)));
            field.Move(idB, homeB + new Vec2(0.5 * Math.Cos((1.7 * now) + 1.0), 0.5 * Math.Sin((1.7 * now) + 1.0)));
            manager.Step(now, Dt, field, noEntities);
            now += Dt;
        }

        Assert.Equal(PedDrModel.FreeKinematic, manager.ModelOf(1));
        Assert.Equal(PedDrModel.FreeKinematic, manager.ModelOf(2));
        Assert.Equal(PedDrModel.PathArc, manager.ModelOf(3));

        // Phase 2: move every relevant source far away permanently; step through the demote dwell.
        field.Move(idA, new Vec2(-10_000, -10_000));
        field.Move(idB, new Vec2(-10_000, -10_000));

        for (var i = 0; i < 60 && (manager.ModelOf(1) == PedDrModel.FreeKinematic || manager.ModelOf(2) == PedDrModel.FreeKinematic); i++)
        {
            manager.Step(now, Dt, field, noEntities);
            now += Dt;
        }

        Assert.Equal(PedDrModel.PathArc, manager.ModelOf(1));
        Assert.Equal(PedDrModel.PathArc, manager.ModelOf(2));
        Assert.Equal(PedDrModel.PathArc, manager.ModelOf(3));
    }

    // ---- Success condition 2: no flap, with several sources sharing the field at once -------------

    [Fact]
    public void NoFlap_MultipleConcurrentSources_HoveringStimulusStaysBounded()
    {
        var nav = new StraightLineNavigation();
        var publisher = new PedPublisher();
        var manager = new PedLodManager(nav, publisher, ArriveRadius, DwellSeconds);

        var home = new Vec2(0, 0);
        manager.AddPed(1, new[] { home, home + new Vec2(50, 0) }, MaxSpeed, Radius, now: 0.0);

        const double promoteRadius = 3.0;
        const double demoteRadius = 6.0;

        var field = new InterestField();
        var hoverId = field.Register(new InterestSource(home, promoteRadius, demoteRadius));
        // Two more, permanently-distant sources present in the SAME field at the same time (use case
        // 4) but never close enough to matter -- proves concurrent unrelated sources don't perturb the
        // hovering source's own hysteresis.
        field.Register(new InterestSource(new Vec2(500, 500), 2.0, 4.0));
        field.Register(new InterestSource(new Vec2(-500, -500), 10.0, 20.0));

        var noEntities = Array.Empty<WorldDisc>();
        var now = 0.0;

        for (var i = 0; i < 400; i++)
        {
            var pedPos = manager.PositionOf(1, now);
            var nextPos = manager.ModelOf(1) == PedDrModel.PathArc
                ? pedPos + new Vec2(promoteRadius * 0.2, 0.0)
                : pedPos + new Vec2(i % 2 == 0 ? demoteRadius - 0.1 : demoteRadius + 0.1, 0.0);
            field.Move(hoverId, nextPos);

            manager.Step(now, Dt, field, noEntities);
            now += Dt;
        }

        var promotions = publisher.Events.Count(e => e is DrSwitchEvent { To: PedDrModel.FreeKinematic });
        _output.WriteLine($"[P1-1 measured] promotion count under boundary-hovering stimulus with 3 concurrent sources (400 steps): {promotions}");
        Assert.True(promotions is >= 1 and <= 2, $"expected the promotion count to stay bounded (<=2), got {promotions}");
    }

    // ---- Success condition 3: determinism, multi-source ---------------------------------------------

    [Fact]
    public void MultiSource_IsDeterministic_AcrossIndependentRuns()
    {
        var (trajectory1, events1) = RunMultiSourceScenario();
        var (trajectory2, events2) = RunMultiSourceScenario();

        Assert.Equal(trajectory1.Count, trajectory2.Count);
        for (var i = 0; i < trajectory1.Count; i++)
        {
            Assert.Equal(trajectory1[i].X, trajectory2[i].X, precision: 12);
            Assert.Equal(trajectory1[i].Y, trajectory2[i].Y, precision: 12);
        }

        Assert.Equal(events1.Count, events2.Count);
        for (var i = 0; i < events1.Count; i++)
        {
            Assert.Equal(events1[i].GetType(), events2[i].GetType());
            Assert.Equal(events1[i].Id, events2[i].Id);
            Assert.Equal(events1[i].Time, events2[i].Time, precision: 12);
        }
    }

    // 3 peds, 3 independently-orbiting sources (one per ped, own frequency/phase) promoting them
    // together, then all sources retreat permanently so every ped demotes too -- one full run,
    // returning every ped's per-step trajectory and the whole published event stream.
    private static (List<Vec2> Trajectory, List<PedEvent> Events) RunMultiSourceScenario()
    {
        var nav = new StraightLineNavigation();
        var publisher = new PedPublisher();
        var manager = new PedLodManager(nav, publisher, ArriveRadius, DwellSeconds);

        var homes = new[] { new Vec2(0, 0), new Vec2(40, 0), new Vec2(80, 0) };
        for (var i = 0; i < homes.Length; i++)
        {
            manager.AddPed(i + 1, new[] { homes[i], homes[i] + new Vec2(1, 0) }, MaxSpeed, Radius, now: 0.0);
        }

        var field = new InterestField();
        var ids = new List<InterestSourceId>();
        foreach (var h in homes)
        {
            ids.Add(field.Register(new InterestSource(h, promoteRadius: 3.0, demoteRadius: 6.0)));
        }

        var noEntities = Array.Empty<WorldDisc>();
        var trajectory = new List<Vec2>();
        var now = 0.0;

        const int steps = 200;
        const int retreatAt = 150;
        for (var step = 0; step < steps; step++)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var freq = 0.6 + (0.3 * i);
                var orbit = homes[i] + new Vec2(0.5 * Math.Cos((freq * now) + i), 0.5 * Math.Sin((freq * now) + i));
                field.Move(ids[i], step < retreatAt ? orbit : new Vec2(-10_000 - i, -10_000 - i));
            }

            manager.Step(now, Dt, field, noEntities);
            now += Dt;

            for (var i = 0; i < homes.Length; i++)
            {
                trajectory.Add(manager.PositionOf(i + 1, now));
            }
        }

        return (trajectory, new List<PedEvent>(publisher.Events));
    }

    // ---- Success condition 4: cost does not blow up as (spread-out) sources are added --------------

    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(500)]
    public void Query_CandidateCountStaysBounded_AsSpreadOutSourceCountGrows(int sourceCount)
    {
        var field = new InterestField();

        // Spread sources 500 m apart, far larger than their own radii (promote=2, demote=4, so
        // RebuildIndex derives a ~8 m cell -- see its remarks) so no two sources can ever land in the
        // same or an overlapping cell, no matter how many are registered.
        for (var i = 0; i < sourceCount; i++)
        {
            field.Register(new InterestSource(new Vec2(i * 500.0, 0), promoteRadius: 2.0, demoteRadius: 4.0));
        }

        field.RebuildIndex();

        // Total insertions scale with sourceCount (RebuildIndex is O(sources), never O(peds)) -- that's
        // expected and fine; what must NOT scale with sourceCount is the per-QUERY candidate count.
        Assert.True(field.LastRebuildInsertionCount > 0);

        // Querying exactly where a source sits is the worst case for that source's neighbourhood --
        // it must never see any of the OTHER (spread-out, far-away) sources as candidates, regardless
        // of how many are registered elsewhere in the field.
        var atSource = field.Query(new Vec2(0, 0));
        Assert.True(atSource.AnyWithinPromote);
        Assert.True(
            field.LastQueryCandidateCount <= 1,
            $"expected exactly the 1 local source as a candidate with {sourceCount} total sources registered, got {field.LastQueryCandidateCount}");

        // Querying far from every source costs nothing (empty bucket).
        var farAway = field.Query(new Vec2(-10_000, -10_000));
        Assert.False(farAway.AnyWithinPromote);
        Assert.Equal(0, field.LastQueryCandidateCount);

        _output.WriteLine(
            $"[P1-1 measured] sourceCount={sourceCount}: rebuildInsertions={field.LastRebuildInsertionCount}, "
            + $"candidatesAtSource={atSource}, lastQueryCandidateCount(at-source query)<=1 holds");
    }

    // ---- Stable identity: Register/Move/Remove don't reindex or disturb unrelated sources ----------

    [Fact]
    public void RegisterMoveRemove_DoNotDisturbOtherSources()
    {
        var field = new InterestField();

        var idA = field.Register(new InterestSource(new Vec2(0, 0), 2.0, 4.0));
        var idB = field.Register(new InterestSource(new Vec2(100, 0), 2.0, 4.0));
        var idC = field.Register(new InterestSource(new Vec2(200, 0), 2.0, 4.0));

        Assert.Equal(3, field.Count);
        Assert.True(field.Contains(idA));
        Assert.True(field.Contains(idB));
        Assert.True(field.Contains(idC));
        Assert.NotEqual(idA, idB);
        Assert.NotEqual(idB, idC);

        // Moving B doesn't affect A or C's registration or position.
        field.Move(idB, new Vec2(999, 999));
        Assert.Equal(new Vec2(0, 0), field.SourceOf(idA).Position);
        Assert.Equal(new Vec2(999, 999), field.SourceOf(idB).Position);
        Assert.Equal(new Vec2(200, 0), field.SourceOf(idC).Position);

        // Removing B leaves A and C fully intact and independently queryable, and B's old id is gone
        // (never silently reused).
        field.Remove(idB);
        Assert.Equal(2, field.Count);
        Assert.False(field.Contains(idB));
        Assert.True(field.Contains(idA));
        Assert.True(field.Contains(idC));

        field.RebuildIndex();
        Assert.True(field.Query(new Vec2(0, 0)).AnyWithinPromote);
        Assert.True(field.Query(new Vec2(200, 0)).AnyWithinPromote);
        Assert.False(field.Query(new Vec2(999, 999)).AnyWithinPromote); // B is gone

        var newId = field.Register(new InterestSource(new Vec2(50, 50), 1.0, 2.0));
        Assert.NotEqual(idB, newId); // ids are never reused
    }
}
