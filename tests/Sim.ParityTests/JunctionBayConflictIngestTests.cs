using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// JUNCTION-FOE-LANE Entry 36 (docs/JUNCTION-REALISM-SESSION-JOURNAL.md): the bay-conflict ingest
// geometry, pinned on the two TRACED wedge sites.
//
// Witness 1 -- city-organic-L2 junction 301 (the dwell-634 mutual two-cycle): links 24 and 25 are
// sibling left turns from the SAME approach lane (-336_1); their first-stage bays (:301_24_0,
// 5.05 m; :301_25_0, 4.29 m) share a start point, are both shorter than a car, and appear in
// NEITHER foes row. The stage-lane comparison (ego's link-controlling lane vs the foe bay) meets
// the sibling bay only in a sub-metre sliver near its far end, so the Entry-36 BAY-PIECE rows
// (ego's own first-stage bay vs the foe bay, ego arcs RELATIVE TO THE STAGE-2 START, i.e.
// negative) are the ONLY coverage of the shared-start overlap -- the engine's unchanged
// `egoDistToEntry + EgoArcStart` then lands the hold at the stop line.
//
// Witness 2 -- city-organic junction 359 (the cross-arm wedge the gate-ON battery caught): the
// corridors of links 5 (:359_13_0) and the bay of link 8 (:359_8_0) merely BRUSH (ego-side
// sliver 0.27 m at ~1.9 m centerline distance -- bodies never touch; SUMO's non-foes verdict is
// geometrically correct there), and the row emitted for that brush parked a vehicle 0.1 m before
// the sliver forever, deadlocked against the bay occupant across two different arms. The
// minEgoOverlapLen=1.0 filter drops brush rows while keeping every genuine shared corridor
// (metres long) -- both directions are asserted so the filter can neither silently widen nor
// silently eat the load-bearing rows.
public class JunctionBayConflictIngestTests
{
    private const double Tol = 0.15;

    private static Junction LoadJunction(string scenario, string junctionId)
    {
        var netPath = Path.Combine(RepoRoot(), "scenarios", "_bench", scenario, "net.net.xml");
        var net = NetworkParser.Parse(netPath);
        var junction = net.Junctions.FirstOrDefault(j => j.Id == junctionId);
        Assert.NotNull(junction);
        return junction!;
    }

    [Fact]
    public void SiblingBays_BayPieceRows_CoverTheSharedStartWithStage2RelativeArcs()
    {
        var junction = LoadJunction("city-organic-L2", "301");

        // Ego link 24's own bay is :301_24_0 (5.05 m). Its bay-piece row against sibling bay
        // :301_25_0 must start at the bay's own start: EgoArcStart == -bayLength.
        var piece24 = junction.BayConflicts.Single(
            bc => bc.EgoLink == 24 && bc.BayLaneId == ":301_25_0");
        Assert.InRange(piece24.EgoArcStart, -5.05 - Tol, -5.05 + Tol);
        Assert.InRange(piece24.BayArcStart, 0.0 - Tol, 0.0 + Tol); // covers the foe bay FROM ITS START
        Assert.True(piece24.EgoArcEnd <= 0.0 + Tol, "bay-piece ego arcs must stay stage-2-relative (<= 0)");

        // And symmetrically for ego link 25 (bay :301_25_0, 4.29 m) against :301_24_0.
        var piece25 = junction.BayConflicts.Single(
            bc => bc.EgoLink == 25 && bc.BayLaneId == ":301_24_0" && bc.EgoArcStart < 0.0);
        Assert.InRange(piece25.EgoArcStart, -4.29 - Tol, -4.29 + Tol);
        Assert.InRange(piece25.BayArcStart, 0.0 - Tol, 0.0 + Tol);
        Assert.True(piece25.EgoArcEnd <= 0.0 + Tol, "bay-piece ego arcs must stay stage-2-relative (<= 0)");

        // The non-vacuity guard: the sibling STAGE-lane comparison yields only sub-metre slivers
        // here (0.00 m for link 24, 0.76 m for link 25), which the brush filter drops -- so if a
        // positive-arc row for the pair appears, either netconvert geometry changed or the filter
        // was weakened; re-measure before trusting the bay-piece rows alone.
        Assert.DoesNotContain(junction.BayConflicts,
            bc => bc.EgoLink == 24 && bc.BayLaneId == ":301_25_0" && bc.EgoArcStart >= 0.0);
    }

    [Fact]
    public void BrushSlivers_AreDropped_GenuineSharedCorridorsAreKept()
    {
        var junction = LoadJunction("city-organic", "359");

        // The traced wedge row: links 5 and 8's corridors brush for 0.27 m -- must NOT be a row.
        Assert.DoesNotContain(junction.BayConflicts,
            bc => bc.EgoLink == 5 && bc.BayLaneId == ":359_8_0");

        // The genuine along-bay corridors at the same junction (metres of overlap) must survive
        // the filter -- this is the half that keeps the filter from silently eating the mechanism.
        var kept = junction.BayConflicts.Single(
            bc => bc.EgoLink == 10 && bc.BayLaneId == ":359_11_0");
        Assert.True(kept.EgoArcEnd - kept.EgoArcStart > 3.0,
            $"expected a metres-long shared corridor, got {kept.EgoArcEnd - kept.EgoArcStart:F2} m");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Traffic.sln not found above test bin dir");
    }
}
