using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// F3/cont-turn -- docs/NEED-contturn-stuck-in-junction.md.
//
// DIRECT regression guard for a mis-ported predicate. SUMO's "am I on the junction" test is a LANE
// PROPERTY -- MSLane::isInternal() (sumo/src/microsim/MSLane.cpp:2498) delegating to
// MSEdge::isInternal() (MSEdge.h:264, `myFunction == SumoXMLEdgeFunc::INTERNAL`) -- and it is true for
// EVERY internal lane of EVERY STAGE of a junction. It is used that way in the guard opening
// MSVehicle::isLeader (MSVehicle.cpp:7348):
//
//     if (!myLane->isInternal() || myLane->getEdge().getToJunction() != link->getJunction()) {
//         // if this vehicle is not yet on the junction, every vehicle is a leader
//         return true;
//     }
//
// The engine previously substituted `v.LaneId == egoInternalLaneId` -- equality against the single
// LINK-CONTROLLING internal lane. Those two predicates agree on an ordinary single-internal-lane turn
// and diverge exactly on a `cont` turn (one split by an internal junction), because netconvert writes
// only the SECOND-stage lane into a junction's `intLanes`:
//
//     NWWriter_SUMO.cpp:634-649:  haveVia ? intLanes.push_back(viaID + "_0")
//                                         : intLanes.push_back(getInternalLaneID())
//
// so the FIRST-stage lane is absent from `intLanes`, hence absent from `LinkByInternalLane`, hence
// invisible to any "inside the junction" test written against it. A vehicle sitting on the first-stage
// lane was therefore treated as "not yet on the junction", which wrongly enabled the `!egoOnInternal`
// cautious-approach arm mid-junction (measured consequence: a ~95-step freeze inside a junction with no
// leader and no blocked exit).
//
// These tests pin the FIX (NetworkModel.IsInternalLaneOfJunction / JunctionByInternalLane) directly, on
// committed net geometry, with no trajectory statistics and no SUMO dependency -- so they cannot pass
// vacuously and cannot be confounded by the other overlap causes in the live-city demo.
public class ContTurnInternalLaneOwnershipTests
{
    private static NetworkModel LoadScenario44()
    {
        var netPath = Path.Combine(RepoRoot(), "scenarios", "44-multilane-junction-turn", "net.net.xml");
        return NetworkParser.Parse(netPath);
    }

    // The two cont turns in scenario 44's junction C, as (first-stage lane, link-controlling lane).
    // NC->CE and SC->CW are split by an internal junction; EC->CS and WC->CN are single-stage.
    public static TheoryData<string, string> ContTurnStages() => new()
    {
        { ":C_3_0", ":C_16_0" },
        { ":C_11_0", ":C_17_0" },
    };

    // THE LOAD-BEARING ASSERTION. The first-stage lane must be recognised as an internal lane of
    // junction C even though it is NOT in C's intLanes. Both halves matter:
    //   * the `IsInternalLaneOfJunction` assertion is the fix;
    //   * the `IntLanes.DoesNotContain` assertion proves the test is NON-VACUOUS -- it pins the exact
    //     gap (a lane the old IntLanes-keyed predicate structurally could not see). If netconvert ever
    //     started listing first-stage lanes, that half fails loudly instead of the test silently
    //     becoming trivial.
    [Theory]
    [MemberData(nameof(ContTurnStages))]
    public void FirstStageContTurnLane_IsRecognisedAsInternalToItsJunction_ThoughAbsentFromIntLanes(
        string firstStageLaneId, string linkControllingLaneId)
    {
        var net = LoadScenario44();
        var junctionC = net.JunctionsById["C"];

        Assert.DoesNotContain(firstStageLaneId, junctionC.IntLanes);
        Assert.Contains(linkControllingLaneId, junctionC.IntLanes);

        Assert.True(
            net.IsInternalLaneOfJunction(firstStageLaneId, junctionC),
            $"[{firstStageLaneId}] is a FIRST-STAGE internal lane of junction C (it feeds the "
            + $"link-controlling lane [{linkControllingLaneId}] through an internal junction), so a vehicle "
            + "on it IS physically inside junction C. SUMO answers this with MSLane::isInternal(), a lane "
            + "property. If this fails, the engine has regressed to an IntLanes-keyed test and will again "
            + "treat a car mid-junction as 'not yet on the junction'. See "
            + "docs/NEED-contturn-stuck-in-junction.md.");

        // The link-controlling lane must of course also be recognised.
        Assert.True(net.IsInternalLaneOfJunction(linkControllingLaneId, junctionC));

        // And the OLD lookup must still be the link-controlling-only map -- this documents WHY a second
        // index was needed rather than widening the first (JunctionYieldConstraint depends on
        // LinkByInternalLane resolving to the lane that owns the <request> row).
        Assert.True(net.LinkByInternalLane.ContainsKey(linkControllingLaneId));
        Assert.False(net.LinkByInternalLane.ContainsKey(firstStageLaneId));
    }

    // Single-stage turns must be unaffected: their one internal lane is both in intLanes and owned.
    [Theory]
    [InlineData(":C_7_0")]
    [InlineData(":C_15_0")]
    public void SingleStageTurnLane_IsInIntLanesAndOwned(string laneId)
    {
        var net = LoadScenario44();
        var junctionC = net.JunctionsById["C"];

        Assert.Contains(laneId, junctionC.IntLanes);
        Assert.True(net.IsInternalLaneOfJunction(laneId, junctionC));
    }

    // Negative cases: the predicate must not over-report. A normal (non-internal) lane is never
    // "inside" a junction -- otherwise the fix would disable the cautious-approach arm on approach
    // lanes too, which is the arm's legitimate domain.
    [Theory]
    [InlineData("NC_1")]
    [InlineData("CE_1")]
    [InlineData("EC_1")]
    public void NormalLane_IsNotInternalToAnyJunction(string laneId)
    {
        var net = LoadScenario44();
        var junctionC = net.JunctionsById["C"];

        Assert.False(net.IsInternalLaneOfJunction(laneId, junctionC));
    }

    // The same defect on a SECOND, independent net (the live-city demo), so the guard is not tied to
    // one fixture's geometry. Junction d_3_4 lists :d_3_4_20_0 but not the first-stage :d_3_4_5_0 --
    // this is the exact lane __veh127 froze on for 95 steps.
    [Fact]
    public void DemoCityNet_FirstStageContTurnLane_IsRecognised()
    {
        var netPath = Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml");
        var net = NetworkParser.Parse(netPath);
        var junction = net.JunctionsById["d_3_4"];

        Assert.DoesNotContain(":d_3_4_5_0", junction.IntLanes);
        Assert.Contains(":d_3_4_20_0", junction.IntLanes);

        Assert.True(
            net.IsInternalLaneOfJunction(":d_3_4_5_0", junction),
            "[:d_3_4_5_0] is the first-stage lane __veh127 froze on for 95 consecutive steps while being "
            + "treated as 'not yet on the junction'. See docs/NEED-contturn-stuck-in-junction.md.");
    }

    // A lane must be owned by ITS OWN junction only -- the second clause of SUMO's guard
    // (`myLane->getEdge().getToJunction() != link->getJunction()`), which exists to exclude an
    // ADJACENT junction's internal lane.
    [Fact]
    public void InternalLane_IsNotReportedAsBelongingToADifferentJunction()
    {
        var netPath = Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml");
        var net = NetworkParser.Parse(netPath);

        var owning = net.JunctionsById["d_3_4"];
        var other = net.JunctionsById["d_5_4"];

        Assert.True(net.IsInternalLaneOfJunction(":d_3_4_5_0", owning));
        Assert.False(net.IsInternalLaneOfJunction(":d_3_4_5_0", other));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios"))
                && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }
}
