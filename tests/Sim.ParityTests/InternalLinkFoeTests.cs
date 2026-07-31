using System.Linq;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// JUNCTION-APPROACH-ARM T1 -- docs/JUNCTION-APPROACH-ARM-DESIGN.md §2, §3, §4.1, §10.2;
// docs/JUNCTION-APPROACH-ARM-TASKS.md T1.
//
// Ports `myInternalLinkFoes` (sumo/src/microsim/MSInternalJunction.cpp:96-110), the SECOND foe set
// `postloadInit` builds (`myInternalLaneFoes`, the FIRST, already has its own coverage in
// InternalJunctionFoeTests.cs). Parse-time only: `NetworkModel.InternalLinkFoes`. NOTHING in
// Sim.Core reads it yet (that is T4's admission arm) -- this task is parity-inert BY CONSTRUCTION,
// so these tests exercise `NetworkParser` output directly, never a running scenario.
public class InternalLinkFoeTests
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

    private static string JunctionRealismL1NetPath()
        => Path.Combine(RepoRoot(), "scenarios", "_diag", "junction-realism-L1", "net.net.xml");

    // ============================================================================================
    // Success condition 1 (T1): `:J01_13_0` resolves an `InternalLinkFoes` set containing the via
    // lane `:J01_10_0`, asserted as that SPECIFIC lane -- not "non-empty", which would pass under
    // the reversed-bit-mask error design §10.2 warns about (this workstream has already hit that
    // trap twice).
    //
    // Fixture facts (net.net.xml), pinned so a stale assumption cannot make this pass by accident:
    //   - `:J01_13_0 incLanes=":J01_5_0 in_W01_0"` -- incoming[1] is `in_W01_0` (a REAL, non-internal
    //     lane; incoming[0], `:J01_5_0`, is the checker/bay lane and must be EXCLUDED -- see the
    //     next test for a fixture where that exclusion actually changes the result).
    //   - `in_W01_0`'s through movement is `<connection from="in_W01" to="h1" via=":J01_10_0"
    //     tl="J01" linkIndex="10" .../>`.
    //   - `:J01_13_0`'s own link index at parent junction J01 is 5 (`:J01_5_0`'s single link).
    //   - J01's `<request index="5" response="111110000110" .../>` has bit 10 set (rightmost char
    //     is bit 0 -- `NetworkModel.Bit`/`JunctionRequest.RespondsTo`, never hand-decoded here).
    // ============================================================================================
    [Fact]
    public void InternalJunctionJ01_13_0_ResolvesLinkFoeContainingViaLaneJ01_10_0()
    {
        var net = NetworkParser.Parse(JunctionRealismL1NetPath());

        var parent = net.JunctionsById["J01"];
        JunctionRequest? request5 = null;
        foreach (var r in parent.Requests)
        {
            if (r.Index == 5)
            {
                request5 = r;
                break;
            }
        }

        Assert.NotNull(request5);
        Assert.True(request5!.RespondsTo(10),
            "fixture assumption stale: J01's response[5] must respond to link 10 for this test to be "
            + "meaningful (design doc §9's repro fact).");

        Assert.NotNull(net.InternalLinkFoes);
        Assert.True(net.InternalLinkFoes!.TryGetValue(":J01_13_0", out var foeHandles),
            "expected InternalLinkFoes[':J01_13_0'] to be present.");

        var actualFoeLaneIds = foeHandles!.Select(h => net.LanesByHandle[h].Id).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(":J01_10_0", actualFoeLaneIds);
    }

    // ============================================================================================
    // Success condition 2 (T1) -- THE NON-VACUITY GUARD for the index-0 exclusion. A hand-built
    // synthetic net where:
    //   - link 0 is a 2-stage (cont) chain: real lane "in1_0" -> stage-1 internal lane ":J_2_0" ->
    //     stage-2 internal lane ":J_5_0" -> real lane "out1_0". The internal junction under test is
    //     named ":J_5_0" (its own id equals the stage-2/link-controlling lane, exactly like every
    //     committed net's internal-junction naming), with `incLanes=":J_2_0 in2_0"` -- index 0 is
    //     the checker/bay lane ":J_2_0" (the SAME lane whose own link is `ownLinkIndex`=0), index 1
    //     is the plain real lane "in2_0" that feeds link 1.
    //   - Junction J's `<request index="0" response="01" .../>` sets bit 0 (self) and clears bit 1.
    //     Bit 0 set means: IF index 0 were (wrongly) included in the walk, its own link resolves
    //     to linkIndex 0, `response[0].RespondsTo(0)` is true, and ":J_5_0" (its via lane) would be
    //     added as its own foe. Bit 1 clear means index 1 alone (the CORRECT walk, per
    //     MSInternalJunction.cpp:97's `begin() + 1`) contributes nothing.
    //   - So the CORRECT result (index 0 skipped) is EMPTY; a walk that failed to skip index 0 would
    //     produce {":J_5_0"} instead. The fixture-assumption asserts on `RespondsTo` below pin BOTH
    //     halves of that claim directly against the request row, rather than trusting prose.
    // ============================================================================================
    [Fact]
    public void IndexZero_TheCheckerLane_IsExcluded_OnAFixtureWhereIncludingItWouldChangeTheResult()
    {
        const string net = """
            <net>
              <edge id="in1" from="X" to="J">
                <lane id="in1_0" index="0" speed="13.9" length="50.0" shape="0.00,0.00 50.00,0.00"/>
              </edge>
              <edge id="in2" from="W" to="J">
                <lane id="in2_0" index="0" speed="13.9" length="50.0" shape="0.00,-5.00 50.00,-5.00"/>
              </edge>
              <edge id=":J_2" from="J" to="J">
                <lane id=":J_2_0" index="0" speed="13.9" length="2.0" shape="50.00,0.00 52.00,0.00"/>
              </edge>
              <edge id=":J_5" from="J" to="J">
                <lane id=":J_5_0" index="0" speed="13.9" length="2.0" shape="52.00,0.00 54.00,0.00"/>
              </edge>
              <edge id=":J_6" from="J" to="J">
                <lane id=":J_6_0" index="0" speed="13.9" length="2.0" shape="50.00,-5.00 54.00,-5.00"/>
              </edge>
              <edge id="out1" from="J" to="Y">
                <lane id="out1_0" index="0" speed="13.9" length="50.0" shape="54.00,0.00 104.00,0.00"/>
              </edge>
              <edge id="out2" from="J" to="Z">
                <lane id="out2_0" index="0" speed="13.9" length="50.0" shape="54.00,-5.00 104.00,-5.00"/>
              </edge>
              <connection from="in1" to="out1" fromLane="0" toLane="0" via=":J_2_0" state="o"/>
              <connection from=":J_2" to="out1" fromLane="0" toLane="0" via=":J_5_0" state="m"/>
              <connection from="in2" to="out2" fromLane="0" toLane="0" via=":J_6_0" state="o"/>
              <junction id="J" type="priority" intLanes=":J_5_0 :J_6_0">
                <request index="0" response="01" foes="00" cont="1"/>
                <request index="1" response="00" foes="00" cont="0"/>
              </junction>
              <junction id=":J_5_0" type="internal" incLanes=":J_2_0 in2_0" intLanes=""/>
            </net>
            """;

        var model = NetworkParser.ParseXml(net);

        // Fixture assumptions, pinned directly against the parsed request row -- not trusted prose.
        var parent = model.JunctionsById["J"];
        var request0 = parent.Requests[0];
        Assert.True(request0.RespondsTo(0),
            "fixture assumption stale: request[0] must respond to link 0 (itself) -- this is what "
            + "makes including index 0 in the walk change the result.");
        Assert.False(request0.RespondsTo(1),
            "fixture assumption stale: request[0] must NOT respond to link 1 -- this is what makes "
            + "the CORRECT (index-0-skipped) walk produce an empty set.");

        // Sanity: `LinkIndexByInternalLane` resolves incLanes[0] (":J_2_0", the checker lane) to the
        // SAME link index (0) that request[0]'s self-test above exercises -- otherwise the fixture
        // assumption above would not actually correspond to "index 0's own link".
        Assert.NotNull(model.LinkIndexByInternalLane);
        Assert.Equal(0, model.LinkIndexByInternalLane![":J_2_0"].LinkIndex);
        Assert.Equal(1, model.LinkIndexByInternalLane[":J_6_0"].LinkIndex);

        Assert.NotNull(model.InternalLinkFoes);
        Assert.True(model.InternalLinkFoes!.TryGetValue(":J_5_0", out var foeHandles),
            "expected InternalLinkFoes[':J_5_0'] to be present.");

        // THE ASSERTION: empty, because index 0 (the checker lane) is excluded from the walk, and
        // index 1 alone (fixture assumption above) contributes nothing. Had the exclusion not been
        // implemented, this would instead be {":J_5_0"} (per the fixture assumptions above) --
        // making this a non-vacuous guard, not an incidental empty result.
        Assert.Empty(foeHandles!);
    }

    // ============================================================================================
    // Success condition 3 (T1 / design §4.1): sweep all committed *.net.xml -- every parsed
    // `InternalLinkFoes` entry resolves to a REAL lane handle (via `NetworkModel.LanesByHandle`).
    // `NetworkParser` throws `InvalidDataException` if a link foe fails to resolve a via-lane handle
    // (design §4.1's "STOP and report, don't silently drop" instruction), so a clean parse across
    // the whole corpus IS the proof that every committed net's link foes resolve a non-null via
    // lane -- this test additionally re-checks the handles directly rather than relying solely on
    // "it didn't throw".
    // ============================================================================================
    [Fact]
    public void AllCommittedNets_EveryInternalLinkFoeResolvesARealLaneHandle()
    {
        var netFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "scenarios"), "*.net.xml", SearchOption.AllDirectories).ToList();
        Assert.True(netFiles.Count >= 120, $"expected ~134 committed *.net.xml under scenarios/, found {netFiles.Count}.");

        var parsedNets = 0;
        var totalInternalJunctionsWithLinkFoes = 0;
        var totalLinkFoesChecked = 0;

        foreach (var netFile in netFiles)
        {
            System.Xml.Linq.XDocument probe;
            try
            {
                probe = System.Xml.Linq.XDocument.Load(netFile);
            }
            catch
            {
                continue;
            }

            if (probe.Root?.Name.LocalName != "net")
            {
                continue;
            }

            // No bare catch: a genuine InvalidDataException from the §4.1 guard (a link foe that
            // fails to resolve a via lane) must fail this test loudly, not be swallowed.
            var net = NetworkParser.Parse(netFile);
            parsedNets++;

            Assert.NotNull(net.InternalLinkFoes);
            foreach (var (internalJunctionId, foeHandles) in net.InternalLinkFoes!)
            {
                if (foeHandles.Count == 0)
                {
                    continue;
                }

                totalInternalJunctionsWithLinkFoes++;
                foreach (var handle in foeHandles)
                {
                    Assert.True(handle >= 0 && handle < net.LanesByHandle.Count,
                        $"net '{netFile}': internal junction '{internalJunctionId}' link foe lane handle "
                        + $"{handle} does not resolve to a real lane.");
                    totalLinkFoesChecked++;
                }
            }
        }

        Assert.True(parsedNets >= 120, $"only {parsedNets} of {netFiles.Count} committed nets parsed as a <net> -- the sweep is not covering the corpus.");
        Assert.True(totalInternalJunctionsWithLinkFoes > 0, "expected at least one internal junction with a non-empty link-foe set across the corpus.");
        Assert.True(totalLinkFoesChecked > 0, "expected at least one link-foe handle to have been checked across the corpus.");
    }
}
