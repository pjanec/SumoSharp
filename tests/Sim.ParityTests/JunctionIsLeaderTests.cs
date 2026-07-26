using System.Xml.Linq;
using Sim.Core;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// F3/isLeader T2.3 -- docs/F3-ISLEADER-PORT-DESIGN.md §3, §3a, §4; docs/F3-ISLEADER-PORT-TASKS.md T2.3.
//
// Ports MSVehicle::isLeader (sumo/src/microsim/MSVehicle.cpp:7343-7483) as three directly-testable,
// SEPARATELY CALLABLE pieces on `Engine`:
//   - `Engine.IsLeaderByEntryOrder` (STATIC): the tie-break chain (design §4).
//   - `Engine.ResponseFor` (STATIC): the four right-of-way "response" attempts (design §3a).
//   - `Engine.IsLeader` (instance): the full case-selection + tie-break orchestration (design §3).
//
// T2.3 IS STILL PARITY-INERT BY CONSTRUCTION: nothing in Step()/JunctionYieldConstraint calls
// `IsLeader` yet (that is T2.4's arm-5 wiring) -- these tests call it directly, never through a
// running scenario, so a green suite here says nothing about the golden/bench surfaces (verified
// separately: they are unchanged by this task).
public class JunctionIsLeaderTests
{
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

    private static string SyntheticJunction2NetPath()
        => Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2", "grid.net.xml");

    // ============================================================================================
    // Success condition 1: the tie-break chain (design §4), all three rungs, BOTH argument orders,
    // exercised as a DIRECT unit test on `IsLeaderByEntryOrder` -- no scenario, no engine.
    // ============================================================================================

    [Fact]
    public void TieBreak_DifferentEntryTimes_LaterEntrantYields_BothOrders()
    {
        // Deliberately mismatched speed/id (foe "looks" slower AND lexicographically smaller) to
        // prove entry-time strictly dominates the speed/id rungs whenever ET differs (MSVehicle.cpp:
        // 7465-7473 is an unconditional `else`, never falls through to the speed/id branch).
        Assert.True(Engine.IsLeaderByEntryOrder(egoEntryTime: 10, foeEntryTime: 5, egoSpeed: 9.0, foeSpeed: 1.0, egoId: "z", foeId: "a"));
        Assert.False(Engine.IsLeaderByEntryOrder(egoEntryTime: 5, foeEntryTime: 10, egoSpeed: 1.0, foeSpeed: 9.0, egoId: "a", foeId: "z"));
    }

    [Fact]
    public void TieBreak_EqualEntryTimes_DifferentSpeeds_SlowerYields_BothOrders()
    {
        const long et = 42;

        // Ego is the SLOWER vehicle -> ego yields (true).
        Assert.True(Engine.IsLeaderByEntryOrder(et, et, egoSpeed: 1.0, foeSpeed: 2.0, egoId: "x", foeId: "y"));
        // Swapped: ego is now the FASTER vehicle -> ego does not yield (false).
        Assert.False(Engine.IsLeaderByEntryOrder(et, et, egoSpeed: 2.0, foeSpeed: 1.0, egoId: "y", foeId: "x"));
    }

    [Fact]
    public void TieBreak_EqualEntryTimesAndSpeeds_LexicographicallySmallerIdYields_BothOrders_Antisymmetric()
    {
        const long et = 7;
        const double speed = 3.5;

        // CompareOrdinal("102", "95") < 0 ('1' < '9' at the first differing byte) -- design doc §0a's
        // own worked example: the measured deadlock pair, tied on speed (both exactly 0.000 there),
        // resolves to veh 102 yielding.
        Assert.True(Engine.IsLeaderByEntryOrder(et, et, speed, speed, egoId: "102", foeId: "95"));
        var swapped = Engine.IsLeaderByEntryOrder(et, et, speed, speed, egoId: "95", foeId: "102");
        Assert.False(swapped);

        // Antisymmetry: swapping ego/foe negates the result exactly (never both true, never both false).
        Assert.NotEqual(
            Engine.IsLeaderByEntryOrder(et, et, speed, speed, "102", "95"),
            Engine.IsLeaderByEntryOrder(et, et, speed, speed, "95", "102"));
    }

    // ============================================================================================
    // Success condition 2: the id comparison is ORDINAL (byte-wise), not culture-sensitive. "a" vs
    // "B" is the textbook case where they disagree -- confirmed live in this environment:
    // string.CompareOrdinal("a","B") = 31 (positive: 'a'=97 > 'B'=66, so "a" sorts AFTER "B"
    // byte-wise) while string.Compare("a","B") (culture-sensitive) = -1 (alphabetic order treats
    // "a" as coming BEFORE "B"). A test using ids that agree under both orderings would pass
    // whether or not the implementation used the wrong comparison -- this one would NOT.
    // ============================================================================================

    [Fact]
    public void TieBreak_IdCompareIsOrdinal_NotCultureSensitive()
    {
        // Self-check: pin the very disagreement this test exploits, so a future runtime/culture
        // change that erased the disagreement would fail LOUDLY here rather than silently passing
        // the assertions below for the wrong reason.
        Assert.True(string.CompareOrdinal("a", "B") > 0, "fixture assumption stale: CompareOrdinal(\"a\",\"B\") is no longer positive.");
        Assert.True(string.Compare("a", "B") < 0, "fixture assumption stale: culture-sensitive Compare(\"a\",\"B\") is no longer negative.");

        const long et = 1;
        const double speed = 0.0;

        // Byte-wise: CompareOrdinal("a","B") > 0 -> ego "a" is NOT smaller -> ego does NOT yield.
        // A culture-sensitive implementation would compute Compare("a","B") < 0 and wrongly return
        // true here.
        Assert.False(Engine.IsLeaderByEntryOrder(et, et, speed, speed, egoId: "a", foeId: "B"));

        // Swapped: CompareOrdinal("B","a") < 0 -> ego "B" IS smaller -> ego yields (true). A
        // culture-sensitive implementation would compute Compare("B","a") > 0 and wrongly return
        // false here.
        Assert.True(Engine.IsLeaderByEntryOrder(et, et, speed, speed, egoId: "B", foeId: "a"));
    }

    // ============================================================================================
    // Success condition 3: no committed net contains an INDIRECT connection, so design §7's
    // omission of the nested `isExitLinkAfterInternalJunction() && ...isIndirect()` sub-case
    // (MSVehicle.cpp:7366-7367) cannot silently start mattering. `indirect="1"` is not parsed into
    // `NetworkModel.Connection` at all (there is nothing to port), so this sweeps the raw XML
    // directly rather than through NetworkParser.
    // ============================================================================================

    [Fact]
    public void NoCommittedNet_ContainsAnIndirectConnection()
    {
        var netFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "scenarios"), "*.net.xml", SearchOption.AllDirectories).ToList();
        Assert.True(netFiles.Count >= 120, $"expected ~134 committed *.net.xml under scenarios/, found {netFiles.Count}.");

        var checkedFiles = 0;
        var indirectConnections = 0;
        foreach (var netFile in netFiles)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(netFile);
            }
            catch
            {
                // Not every *.net.xml is a full netconvert net (see JunctionLinkLaneMapTests' same
                // tolerance for non-net fixtures under scenarios/) -- skip rather than fail the sweep.
                continue;
            }

            checkedFiles++;
            foreach (var connection in doc.Descendants("connection"))
            {
                if (connection.Attribute("indirect")?.Value == "1")
                {
                    indirectConnections++;
                }
            }
        }

        Assert.True(checkedFiles >= 120, $"only {checkedFiles} of {netFiles.Count} committed nets parsed as XML -- the sweep is not covering the corpus.");
        Assert.Equal(0, indirectConnections);
    }

    // ============================================================================================
    // Success condition 4: case-selection on the MEASURED DEADLOCK PAIR (junction 2336, link 3 =
    // veh 102, link 18 = veh 95 -- design §0a). Per §0a's measurement (not the response matrix):
    // junction 2336's TL never shows both links non-red at once, so ATTEMPT 1 (haveRed) is the
    // ONLY response arm that ever fires for this pair. Representative simulation seconds, read
    // straight off the tlLogic's own <phase> durations (offset 0, cycle 90s):
    //   t=5  -> phase 0  (duration 10) -- BOTH RED     (state[3]='r', state[18]='r')
    //   t=20 -> phase 2  (cum. 13-27)  -- LINK 3 RED ONLY  (state[3]='r', state[18]='g')
    //   t=45 -> phase 6  (cum. 39-53)  -- LINK 18 RED ONLY (state[3]='G', state[18]='r')
    // ============================================================================================

    private const string Junction2336 = "2336";
    private const int Link3 = 3;   // veh 102's link (non-cont, EntryConnectionByLink == Links[3].Connection).
    private const int Link18 = 18; // veh 95's link (cont: EntryConnectionByLink is the STAGE-1 hop).

    private const double BothRedTime = 5.0;
    private const double Link3RedOnlyTime = 20.0;
    private const double Link18RedOnlyTime = 45.0;

    [Fact]
    public void Junction2336_NeverShowsLinks3And18NonRedSimultaneously_ZeroOfTwelvePhases()
    {
        var net = NetworkParser.Parse(SyntheticJunction2NetPath());
        var tlLogic = net.TlLogicsById[Junction2336];
        Assert.Equal(12, tlLogic.Phases.Count);

        var bothNonRedPhases = 0;
        foreach (var phase in tlLogic.Phases)
        {
            var s3 = phase.State[Link3];
            var s18 = phase.State[Link18];
            var red3 = s3 is 'r' or 'u';
            var red18 = s18 is 'r' or 'u';
            if (!red3 && !red18)
            {
                bothNonRedPhases++;
            }
        }

        Assert.Equal(0, bothNonRedPhases);
    }

    [Fact]
    public void ResponseFor_BothEntryLinksRed_SelectsMutualBranch_ResponseAndResponse2BothTrue()
    {
        var net = NetworkParser.Parse(SyntheticJunction2NetPath());
        var tlLogic = net.TlLogicsById[Junction2336];

        var state3 = TrafficLightState.GetLinkState(tlLogic, Link3, BothRedTime);
        var state18 = TrafficLightState.GetLinkState(tlLogic, Link18, BothRedTime);
        Assert.Equal('r', state3);
        Assert.Equal('r', state18);

        var junction = net.JunctionsById[Junction2336];
        var request3 = FindRequest(junction, Link3);
        var request18 = FindRequest(junction, Link18);

        // Both cars stopped (design §0a's measured facts: speed exactly 0.000) -> attempt 1's
        // moving-foe brakeGap sub-branch is never entered; `gap`/foe car-follow params are
        // therefore irrelevant here and left at 0.
        var (response, response2) = Engine.ResponseFor(
            entryState: state3, foeEntryState: state18,
            egoSpeed: 0.0, foeSpeed: 0.0, gap: 2.99,
            foeMaxAccel: 0.0, foeMaxDecel: 0.0, foeHeadwayTime: 0.0, foeLength: 0.0, egoMinGap: 0.0,
            egoRequest: request3, egoLinkIndex: Link3, foeRequest: request18, foeLinkIndex: Link18,
            dt: 1.0);

        Assert.True(response);
        Assert.True(response2);
    }

    [Fact]
    public void ResponseFor_OneRedCases_PickTheOtherTwoResponsePairs()
    {
        var net = NetworkParser.Parse(SyntheticJunction2NetPath());
        var tlLogic = net.TlLogicsById[Junction2336];
        var junction = net.JunctionsById[Junction2336];
        var request3 = FindRequest(junction, Link3);
        var request18 = FindRequest(junction, Link18);

        // t=20: link 3 red, link 18 NOT red.
        var s3At20 = TrafficLightState.GetLinkState(tlLogic, Link3, Link3RedOnlyTime);
        var s18At20 = TrafficLightState.GetLinkState(tlLogic, Link18, Link3RedOnlyTime);
        Assert.Equal('r', s3At20);
        Assert.NotEqual('r', s18At20);
        Assert.NotEqual('u', s18At20);

        // ego = link 3 (the RED link): response=false (ego has ROW), response2=true.
        var (respEgo3, respEgo3Of18) = Engine.ResponseFor(
            s3At20, s18At20, egoSpeed: 0.0, foeSpeed: 0.0, gap: 2.99,
            foeMaxAccel: 0.0, foeMaxDecel: 0.0, foeHeadwayTime: 0.0, foeLength: 0.0, egoMinGap: 0.0,
            request3, Link3, request18, Link18, dt: 1.0);
        Assert.False(respEgo3);
        Assert.True(respEgo3Of18);

        // ego = link 18 (the non-red link): mirrored -- response=true, response2=false.
        var (respEgo18, respEgo18Of3) = Engine.ResponseFor(
            s18At20, s3At20, egoSpeed: 0.0, foeSpeed: 0.0, gap: 2.99,
            foeMaxAccel: 0.0, foeMaxDecel: 0.0, foeHeadwayTime: 0.0, foeLength: 0.0, egoMinGap: 0.0,
            request18, Link18, request3, Link3, dt: 1.0);
        Assert.True(respEgo18);
        Assert.False(respEgo18Of3);

        // t=45: link 18 red, link 3 NOT red -- the mirror image of the above.
        var s3At45 = TrafficLightState.GetLinkState(tlLogic, Link3, Link18RedOnlyTime);
        var s18At45 = TrafficLightState.GetLinkState(tlLogic, Link18, Link18RedOnlyTime);
        Assert.NotEqual('r', s3At45);
        Assert.NotEqual('u', s3At45);
        Assert.Equal('r', s18At45);

        var (respEgo18b, respEgo18Of3b) = Engine.ResponseFor(
            s18At45, s3At45, egoSpeed: 0.0, foeSpeed: 0.0, gap: 2.99,
            foeMaxAccel: 0.0, foeMaxDecel: 0.0, foeHeadwayTime: 0.0, foeLength: 0.0, egoMinGap: 0.0,
            request18, Link18, request3, Link3, dt: 1.0);
        Assert.False(respEgo18b);
        Assert.True(respEgo18Of3b);

        var (respEgo3b, respEgo3Of18b) = Engine.ResponseFor(
            s3At45, s18At45, egoSpeed: 0.0, foeSpeed: 0.0, gap: 2.99,
            foeMaxAccel: 0.0, foeMaxDecel: 0.0, foeHeadwayTime: 0.0, foeLength: 0.0, egoMinGap: 0.0,
            request3, Link3, request18, Link18, dt: 1.0);
        Assert.True(respEgo3b);
        Assert.False(respEgo3Of18b);
    }

    // Success condition 4(d) -- THE MOST IMPORTANT ASSERTION IN THIS TASK: for each of the three
    // phase classes (both red / link-3-red-only / link-18-red-only), evaluating the FULL `IsLeader`
    // decision BOTH WAYS ROUND yields EXACTLY ONE `true`. This is the property that makes the
    // measured deadlock structurally unreachable (design §0a): the two vehicles always compare the
    // SAME two numbers in opposite senses, so they can never both decide to yield (nor both decide
    // to proceed).
    [Theory]
    [InlineData(BothRedTime)]
    [InlineData(Link3RedOnlyTime)]
    [InlineData(Link18RedOnlyTime)]
    public void IsLeader_AntisymmetricAcrossAllThreePhaseClasses_ExactlyOneTrue(double evalTime)
    {
        var net = NetworkParser.Parse(SyntheticJunction2NetPath());
        var junction = net.JunctionsById[Junction2336];
        var link3 = junction.Links[Link3];
        var link18 = junction.Links[Link18];
        Assert.Equal(Link3, link3.Index);
        Assert.Equal(Link18, link18.Index);

        var engine = new Engine();
        engine.LoadNetwork(SyntheticJunction2NetPath());

        // veh 95 (design §0a): cont link 18, sitting on the STAGE-2 (bay-exit/conflict) lane
        // `:2336_42_0` -- the lane the vehicle physically occupies once past the internal junction.
        // Synthetic but SHAPED like §2b's worked cont example: ET==ETN (never yielded) < CET (the
        // later stage-2/conflict-area entry).
        var veh95 = new Engine.JunctionLeaderCandidate(
            LaneId: ":2336_42_0", Id: "95", Speed: 0.0,
            EntryTime: 300, EntryTimeNeverYield: 300, ConflictEntryTime: 305);

        // veh 102 (design §0a): non-cont link 3 -- all three timestamps equal (single entry hop).
        var veh102 = new Engine.JunctionLeaderCandidate(
            LaneId: ":2336_3_0", Id: "102", Speed: 0.0,
            EntryTime: 302, EntryTimeNeverYield: 302, ConflictEntryTime: 302);

        const double gap = 2.99; // design §5b's measured clear-box gap; irrelevant here (both stopped).
        const double dt = 1.0;

        var ninetyFiveYieldsToOneOhTwo = engine.IsLeader(net, junction, link18, veh95, veh102, gap, dt, evalTime);
        var oneOhTwoYieldsToNinetyFive = engine.IsLeader(net, junction, link3, veh102, veh95, gap, dt, evalTime);

        Assert.NotEqual(ninetyFiveYieldsToOneOhTwo, oneOhTwoYieldsToNinetyFive);
    }

    // ============================================================================================
    // Success condition 5 (bonus, not separately enumerated but load-bearing): attempt 1's
    // moving-foe brakeGap sub-branch (MSVehicle.cpp:7381-7401), exercised directly with the
    // `-2 * minGap` arithmetic (:7386-7388) reproduced verbatim -- both outcomes (foe can brake
    // safely / cannot).
    // ============================================================================================

    [Fact]
    public void ResponseFor_Attempt1_MovingForeignForeGap_BrakesSafely_FoeHasPriority()
    {
        // entryState red (ego), foeEntryState green/priority (foe not red, foe moving) -> the
        // brakeGap sub-branch's precondition (`!foeRed && foeSpeed > halting && gap < 0`) holds.
        const double foeSpeed = 5.0, foeMaxAccel = 2.6, foeMaxDecel = 4.5, foeHeadwayTime = 1.0, foeLength = 5.0, egoMinGap = 2.5, dt = 1.0;

        // foeNextSpeed = 5.0 + 2.6*1.0 = 7.6; foeBrakeGap = KraussModel.BrakeGap(7.6, 4.5, 1.0, 1.0).
        var foeBrakeGap = KraussModel.BrakeGap(foeSpeed + KraussModel.Accel2Speed(foeMaxAccel, dt), foeMaxDecel, foeHeadwayTime, dt);
        Assert.True(foeBrakeGap > 0);

        // gap=-30 -> foeGap = -(-30) - 5.0 - 2*2.5 = 30-5-5 = 20 >= foeBrakeGap (~10.7): foe CAN
        // brake safely before the conflict -> response=false (ego need not yield), response2=true.
        var (responseSafe, response2Safe) = Engine.ResponseFor(
            entryState: 'r', foeEntryState: 'G', egoSpeed: 0.0, foeSpeed: foeSpeed, gap: -30.0,
            foeMaxAccel, foeMaxDecel, foeHeadwayTime, foeLength, egoMinGap,
            egoRequest: DummyRequest(0), egoLinkIndex: 0, foeRequest: DummyRequest(1), foeLinkIndex: 1, dt);
        Assert.False(responseSafe);
        Assert.True(response2Safe);

        // gap=-5 -> foeGap = 5 - 5 - 5 = -5 < foeBrakeGap (~10.7): foe CANNOT brake safely ->
        // response=true (ego must yield), response2=false.
        var (responseUnsafe, response2Unsafe) = Engine.ResponseFor(
            entryState: 'r', foeEntryState: 'G', egoSpeed: 0.0, foeSpeed: foeSpeed, gap: -5.0,
            foeMaxAccel, foeMaxDecel, foeHeadwayTime, foeLength, egoMinGap,
            egoRequest: DummyRequest(0), egoLinkIndex: 0, foeRequest: DummyRequest(1), foeLinkIndex: 1, dt);
        Assert.True(responseUnsafe);
        Assert.False(response2Unsafe);
    }

    private static JunctionRequest DummyRequest(int index) => new(index, "0", "0", Cont: false);

    private static JunctionRequest FindRequest(Junction junction, int linkIndex)
    {
        foreach (var r in junction.Requests)
        {
            if (r.Index == linkIndex)
            {
                return r;
            }
        }

        throw new InvalidOperationException($"junction '{junction.Id}' has no <request> row for link {linkIndex}.");
    }
}
