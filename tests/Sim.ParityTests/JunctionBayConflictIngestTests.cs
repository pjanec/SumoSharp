using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// JUNCTION-FOE-LANE Entry 36 (docs/JUNCTION-REALISM-SESSION-JOURNAL.md): the bay-conflict ingest
// geometry that broke the traced dwell-634 gridlock. Junction 301 of the committed organic town is
// the witness site: links 24 and 25 are sibling left turns from the SAME approach lane (-336_1),
// their first-stage bays (:301_24_0, 5.05 m; :301_25_0, 4.29 m) share a start point and are both
// shorter than a car, and netconvert puts them in NEITHER foes row.
//
// Two geometry facts are load-bearing and pinned here on committed net data (offline, no SUMO):
//
//  1. The STAGE-LANE comparison alone (ego's link-controlling lane vs a foe bay) sees the sibling
//     pair's overlap only DEEP into the foe bay (the stage-2 lanes diverge; their corridors meet
//     the sibling bay near its far end) -- so an ego held by such a row has already fully entered
//     its own bay, which is how veh 235 came to rest interpenetrated with veh 198.
//  2. The Entry-36 BAY-PIECE rows (ego's own first-stage bay vs the foe bay, ego arcs emitted
//     RELATIVE TO THE STAGE-2 START, i.e. negative) cover the overlap from the SHARED START POINT,
//     so the engine's unchanged `egoDistToEntry + EgoArcStart` lands the hold at the stop line.
//
// The non-vacuity guard: assertion 1 pins that the positive-arc rows genuinely MISS the bay-start
// overlap (BayArcStart well above 0). If the stage-lane comparison ever started covering it, that
// half fails loudly instead of assertion 2 silently becoming redundant.
public class JunctionBayConflictIngestTests
{
    private const string WitnessJunction = "301";
    private const double Tol = 0.15;

    private static Junction LoadWitness()
    {
        var netPath = Path.Combine(RepoRoot(), "scenarios", "_bench", "city-organic-L2", "net.net.xml");
        var net = NetworkParser.Parse(netPath);
        var junction = net.Junctions.FirstOrDefault(j => j.Id == WitnessJunction);
        Assert.NotNull(junction);
        return junction!;
    }

    [Fact]
    public void SiblingBays_StageLaneRowsAlone_MissTheSharedStartOverlap()
    {
        var junction = LoadWitness();

        // Link 24's stage lane (:301_29_0) vs sibling bay :301_25_0: the positive-arc row starts
        // deep in the bay (3.10 of 4.29 m) -- a car parked at the bay start is OUTSIDE it.
        var stage24 = junction.BayConflicts.Single(
            bc => bc.EgoLink == 24 && bc.BayLaneId == ":301_25_0" && bc.EgoArcStart >= 0.0);
        Assert.True(stage24.BayArcStart > 2.0,
            $"stage-lane row (24, :301_25_0) unexpectedly covers the bay start (BayArcStart={stage24.BayArcStart:F2}); "
            + "the Entry-36 bay-piece row may have become redundant -- re-measure before removing it");

        var stage25 = junction.BayConflicts.Single(
            bc => bc.EgoLink == 25 && bc.BayLaneId == ":301_24_0" && bc.EgoArcStart >= 0.0);
        Assert.True(stage25.BayArcStart > 1.5,
            $"stage-lane row (25, :301_24_0) unexpectedly covers the bay start (BayArcStart={stage25.BayArcStart:F2})");
    }

    [Fact]
    public void SiblingBays_BayPieceRows_CoverTheSharedStartWithStage2RelativeArcs()
    {
        var junction = LoadWitness();

        // Ego link 24's own bay is :301_24_0 (5.05 m). Its bay-piece row against sibling bay
        // :301_25_0 must start at the bay's own start: EgoArcStart == -bayLength.
        var piece24 = junction.BayConflicts.Single(
            bc => bc.EgoLink == 24 && bc.BayLaneId == ":301_25_0" && bc.EgoArcStart < 0.0);
        Assert.InRange(piece24.EgoArcStart, -5.05 - Tol, -5.05 + Tol);
        Assert.InRange(piece24.BayArcStart, 0.0 - Tol, 0.0 + Tol); // covers the foe bay FROM ITS START
        Assert.True(piece24.EgoArcEnd <= 0.0 + Tol, "bay-piece ego arcs must stay stage-2-relative (<= 0)");

        // And symmetrically for ego link 25 (bay :301_25_0, 4.29 m) against :301_24_0.
        var piece25 = junction.BayConflicts.Single(
            bc => bc.EgoLink == 25 && bc.BayLaneId == ":301_24_0" && bc.EgoArcStart < 0.0);
        Assert.InRange(piece25.EgoArcStart, -4.29 - Tol, -4.29 + Tol);
        Assert.InRange(piece25.BayArcStart, 0.0 - Tol, 0.0 + Tol);
        Assert.True(piece25.EgoArcEnd <= 0.0 + Tol, "bay-piece ego arcs must stay stage-2-relative (<= 0)");
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
