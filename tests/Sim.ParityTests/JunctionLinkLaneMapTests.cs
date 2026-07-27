using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// F3/isLeader T2.1 -- docs/F3-ISLEADER-PORT-DESIGN.md §2c, docs/F3-ISLEADER-PORT-TASKS.md T2.1.
//
// Guards the two new parse-time lookups `NetworkModel.LinkIndexByInternalLane` and
// `NetworkModel.EntryConnectionByLink`, both built by extending the existing
// `junctionByInternalLane` back-walk (NetworkParser.cs) rather than a second traversal.
//
// STAGE 1 IS PARITY-INERT BY CONSTRUCTION: nothing reads these two maps yet. These tests exist
// purely to pin the maps' own correctness directly against net geometry, independent of any
// trajectory or golden.
public class JunctionLinkLaneMapTests
{
    private static NetworkModel LoadSyntheticJunction2()
    {
        var netPath = Path.Combine(RepoRoot(), "scenarios", "_repro", "synthetic-junction2", "grid.net.xml");
        return NetworkParser.Parse(netPath);
    }

    // Success condition 1: BOTH cont stages of link 18 at junction 2336 resolve to link index 18,
    // and the first-stage lane is confirmed absent from IntLanes (so the assertion is non-vacuous --
    // it is exactly the gap `LinkIndexByInternalLane` exists to close, same shape as the already
    // committed ContTurnInternalLaneOwnershipTests finding).
    [Fact]
    public void ContStages_BothResolveToSameLinkIndex_AndFirstStageIsAbsentFromIntLanes()
    {
        var net = LoadSyntheticJunction2();
        var junction = net.JunctionsById["2336"];

        Assert.DoesNotContain(":2336_18_0", junction.IntLanes);
        Assert.Contains(":2336_42_0", junction.IntLanes);

        Assert.NotNull(net.LinkIndexByInternalLane);
        var stage2 = net.LinkIndexByInternalLane![":2336_42_0"];
        var stage1 = net.LinkIndexByInternalLane[":2336_18_0"];

        Assert.Equal("2336", stage2.Junction.Id);
        Assert.Equal(18, stage2.LinkIndex);
        Assert.Equal("2336", stage1.Junction.Id);
        Assert.Equal(18, stage1.LinkIndex);
    }

    // Success condition 2: EntryConnectionByLink[("2336", 18)] resolves to the ENTRY hop (state 'o',
    // tl/linkIndex set, via the stage-1 lane) -- NOT the second hop that `Junction.Links[18].Connection`
    // holds (state 'm', no tl/linkIndex). Asserting both halves in the same test demonstrates the two
    // differ -- a non-vacuous guard, per the design doc §2a fact 2/3.
    [Fact]
    public void EntryConnectionByLink_ResolvesTheEntryHop_NotTheSecondHop()
    {
        var net = LoadSyntheticJunction2();
        var junction = net.JunctionsById["2336"];

        // The second hop -- what Junction.Links[18].Connection already holds.
        var secondHop = junction.Links[18].Connection;
        Assert.Equal(":2336_42_0", secondHop.Via);
        Assert.Null(secondHop.LinkIndex);
        Assert.Null(secondHop.Tl);

        // The entry hop, via the new lookup.
        Assert.NotNull(net.EntryConnectionByLink);
        var entry = net.EntryConnectionByLink![("2336", 18)];
        Assert.Equal("2336", entry.Tl);
        Assert.Equal(18, entry.LinkIndex);
        Assert.Equal("o", entry.State);
        Assert.Equal(":2336_18_0", entry.Via);
    }

    // Success condition 3: every one of the ten cont links at junction 2336 has BOTH stages present
    // in LinkIndexByInternalLane mapping to the same link index, and a resolvable EntryConnectionByLink
    // entry carrying a non-null LinkIndex.
    public static TheoryData<int, string, string> AllContLinksAt2336() => new()
    {
        { 5, ":2336_5_0", ":2336_39_0" },
        { 12, ":2336_12_0", ":2336_40_0" },
        { 17, ":2336_17_0", ":2336_41_0" },
        { 18, ":2336_18_0", ":2336_42_0" },
        { 19, ":2336_19_0", ":2336_43_0" },
        { 25, ":2336_25_0", ":2336_44_0" },
        { 31, ":2336_31_0", ":2336_45_0" },
        { 36, ":2336_36_0", ":2336_46_0" },
        { 37, ":2336_37_0", ":2336_47_0" },
        { 38, ":2336_38_0", ":2336_48_0" },
    };

    [Theory]
    [MemberData(nameof(AllContLinksAt2336))]
    public void EveryContLinkAt2336_HasBothStagesMapped_AndAResolvableEntryConnection(
        int linkIndex, string firstStageLaneId, string intLanesLaneId)
    {
        var net = LoadSyntheticJunction2();
        var junction = net.JunctionsById["2336"];

        // Confirm this really is a cont link (Requests[i].Cont), and that IntLanes carries only the
        // second stage -- pins the fixture assumption this theory is built on.
        Assert.True(junction.Requests[linkIndex].Cont, $"link {linkIndex} was expected to be `cont`.");
        Assert.Equal(intLanesLaneId, junction.IntLanes[linkIndex]);
        Assert.DoesNotContain(firstStageLaneId, junction.IntLanes);

        Assert.NotNull(net.LinkIndexByInternalLane);
        var map = net.LinkIndexByInternalLane!;
        Assert.True(map.ContainsKey(firstStageLaneId), $"first-stage lane {firstStageLaneId} missing from LinkIndexByInternalLane.");
        Assert.True(map.ContainsKey(intLanesLaneId), $"second-stage lane {intLanesLaneId} missing from LinkIndexByInternalLane.");
        Assert.Equal(linkIndex, map[firstStageLaneId].LinkIndex);
        Assert.Equal(linkIndex, map[intLanesLaneId].LinkIndex);
        Assert.Equal("2336", map[firstStageLaneId].Junction.Id);
        Assert.Equal("2336", map[intLanesLaneId].Junction.Id);

        Assert.NotNull(net.EntryConnectionByLink);
        Assert.True(net.EntryConnectionByLink!.TryGetValue(("2336", linkIndex), out var entry),
            $"no EntryConnectionByLink entry for link {linkIndex}.");
        Assert.NotNull(entry.LinkIndex);
        Assert.Equal(linkIndex, entry.LinkIndex);
    }

    // Success condition 4: sweep every committed *.net.xml under scenarios/ -- for each junction,
    // every lane id in IntLanes must be present in LinkIndexByInternalLane at the matching index, and
    // must resolve back to the SAME junction (by id) that owns it. Cheap, and it is what catches a net
    // shape the two-junction sample does not cover.
    //
    // Scoped to `junction.Links` (rather than raw `IntLanes` indices) because netconvert's separate
    // `type="internal"` junction object (design doc §1) also carries a (vestigial, single-entry,
    // request-less) `intLanes` attribute that NAMES a lane really owned by a DIFFERENT (the real)
    // junction -- e.g. scenario 41-forced-turn-lane's junction ':J_2_0' (type internal) lists
    // ':J_0_0', which is actually link 0 of junction 'J'. `Junction.Links` is only ever populated for
    // a junction that has BOTH a nonempty IntLanes AND at least one child <request> (Junction's own
    // doc comment), so a request-less internal-junction object -- exactly like ':J_2_0' -- naturally
    // contributes no Links and is excluded here, matching MSLink::getJunction() always resolving to
    // the REAL junction (design doc §1's verified fact) rather than the internal-junction object.
    [Fact]
    public void EveryCommittedNet_IntLanesAreAllPresentInLinkIndexByInternalLane_WithMatchingIndexAndJunction()
    {
        var netFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "scenarios"), "*.net.xml", SearchOption.AllDirectories).ToList();
        Assert.True(netFiles.Count > 0, "expected at least one committed *.net.xml under scenarios/.");

        var checkedLinks = 0;
        var parsedNets = 0;
        foreach (var netFile in netFiles)
        {
            NetworkModel net;
            try
            {
                net = NetworkParser.Parse(netFile);
            }
            catch
            {
                // Not every *.net.xml under scenarios/ need be a full NetworkParser-compatible net
                // (e.g. fixtures for a different parser subset); skip files that don't parse rather
                // than fail the sweep on an unrelated fixture.
                continue;
            }

            parsedNets++;

            Assert.NotNull(net.LinkIndexByInternalLane);
            var map = net.LinkIndexByInternalLane!;

            foreach (var junction in net.Junctions)
            {
                foreach (var link in junction.Links)
                {
                    var laneId = link.InternalLaneId;
                    Assert.True(
                        map.TryGetValue(laneId, out var entry),
                        $"[{netFile}] junction '{junction.Id}' link {link.Index} lane '{laneId}' missing from LinkIndexByInternalLane.");
                    Assert.True(
                        entry.LinkIndex == link.Index,
                        $"[{netFile}] junction '{junction.Id}' link {link.Index} lane '{laneId}' mapped to link index {entry.LinkIndex}, expected {link.Index}.");
                    Assert.True(
                        entry.Junction.Id == junction.Id,
                        $"[{netFile}] junction '{junction.Id}' link {link.Index} lane '{laneId}' mapped to junction '{entry.Junction.Id}' instead.");

                    checkedLinks++;
                }
            }
        }

        // VACUITY FLOORS, not cosmetic. The `catch` above is the failure mode that matters: if a
        // parser change starts throwing on most nets, every assertion in the loop is silently
        // skipped and a `> 0` floor still passes. Measured at the time of writing: all 134 committed
        // *.net.xml parse, 2927 junctions carry a right-of-way matrix, and their `intLanes` hold
        // 37426 entries (the upper bound on junction links -- the actual figure is slightly lower
        // where a link index has no matching top-level <connection>). The floors below are set well
        // under those so ordinary scenario churn does not trip them, but far enough above zero that
        // wholesale swallowing does.
        Assert.True(
            parsedNets >= 120,
            $"only {parsedNets} of {netFiles.Count} committed nets parsed; the sweep's assertions are "
            + "being skipped wholesale, so this test is no longer checking anything. Fix the parser "
            + "regression rather than lowering this floor.");
        Assert.True(
            checkedLinks >= 30_000,
            $"swept only {checkedLinks} junction links; expected ~37k across the committed nets. "
            + "A collapse here means the sweep has stopped covering the corpus.");
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
