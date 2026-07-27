using System.Xml.Linq;
using Sim.Ingest;
using Xunit;

namespace Sim.ParityTests;

// F3/internal-junction-foes T3.1 -- docs/F3-INTERNAL-JUNCTION-DESIGN.md §1, §4, §7 T3.1.
//
// Ports MSInternalJunction::postloadInit (sumo/src/microsim/MSInternalJunction.cpp:60-95) as
// parse-time data ONLY: `NetworkModel.InternalJunctions`, `InternalJunctionByBayLane`, and
// `InternalLaneFoes`. NOTHING in Sim.Core reads any of these three yet (that is T3.2's admission
// arm) -- this task is parity-inert BY CONSTRUCTION, so these tests exercise `NetworkParser`
// output directly, never a running scenario.
public class InternalJunctionFoeTests
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

    private const string BayLane = ":2336_18_0";
    private const string InternalJunctionId = ":2336_42_0";

    // ============================================================================================
    // Success condition 1 (design §7 T3.1.1 / §1): `:2336_42_0` resolves EXACTLY the 14-lane foe
    // set, asserted AS A SET (not a count). Must include `:2336_3_0` (veh 102's lane -- the
    // UNCONDITIONAL foe that alone prevents the measured deadlock, design §1's closing paragraph).
    // ============================================================================================
    [Fact]
    public void InternalJunction2336_42_0_ResolvesExactly14FoeLanes_AsASet()
    {
        var net = NetworkParser.Parse(SyntheticJunction2NetPath());

        var expectedFoeLaneIds = new[]
        {
            ":2336_2_0", ":2336_3_0", ":2336_10_0", ":2336_11_0",
            ":2336_21_0", ":2336_22_0", ":2336_23_0", ":2336_24_0",
            ":2336_26_0", ":2336_27_0", ":2336_33_0", ":2336_34_0",
            ":2336_34_1", ":2336_44_0",
        };

        Assert.NotNull(net.InternalLaneFoes);
        Assert.True(net.InternalLaneFoes!.TryGetValue(InternalJunctionId, out var foeHandles),
            $"expected InternalLaneFoes['{InternalJunctionId}'] to be present.");

        var actualFoeLaneIds = foeHandles!
            .Select(h => net.LanesByHandle[h].Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expectedFoeLaneIds.ToHashSet(StringComparer.Ordinal), actualFoeLaneIds);
        Assert.Contains(":2336_3_0", actualFoeLaneIds);
        Assert.Equal(14, actualFoeLaneIds.Count);
    }

    // ============================================================================================
    // Success condition 2 (design §7 T3.1.2) -- THE NON-VACUITY GUARD. `:2336_25_0` is a cont
    // STAGE-1 bay whose own entry link index (25) is NOT set in the parent's response[18] row, so
    // it must be ABSENT from the foe set, while its STAGE-2 lane `:2336_44_0` (always added,
    // regardless of the response test) must be PRESENT. A test that only checked the 13
    // unconditional lanes would pass under the WRONG single-branch rule the NEED doc originally
    // sketched (every intLanes entry whose link index is set in the response row) -- this one does
    // not, because :2336_25_0's absence and :2336_44_0's presence together only hold under the
    // correct TWO-branch rule.
    // ============================================================================================
    [Fact]
    public void TwoBranchRule_ContBayWithFalseResponse_IsAbsent_ItsStage2LaneIsPresent()
    {
        var net = NetworkParser.Parse(SyntheticJunction2NetPath());

        // Pin the non-vacuity fixture assumption itself: parent junction 2336's response[18] row
        // must NOT have bit 25 set, else this test would pass whether or not the two-branch rule is
        // implemented correctly.
        var parent = net.JunctionsById["2336"];
        JunctionRequest? request18 = null;
        foreach (var r in parent.Requests)
        {
            if (r.Index == 18)
            {
                request18 = r;
                break;
            }
        }

        Assert.NotNull(request18);
        Assert.False(request18!.RespondsTo(25),
            "fixture assumption stale: parent junction 2336's response[18] must NOT respond to link 25 "
            + "for this non-vacuity guard to be meaningful.");

        Assert.NotNull(net.InternalLaneFoes);
        var foeHandles = net.InternalLaneFoes![InternalJunctionId];
        var actualFoeLaneIds = foeHandles.Select(h => net.LanesByHandle[h].Id).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(":2336_25_0", actualFoeLaneIds);
        Assert.Contains(":2336_44_0", actualFoeLaneIds);
    }

    // ============================================================================================
    // Success condition 3 (design §7 T3.1.3): `InternalJunctionByBayLane` is keyed on the FIRST
    // `IncLanes` entry ONLY -- `:2336_18_0` (position 0 of `incLanes=":2336_18_0 -2439_0"`) resolves
    // to `:2336_42_0`; a NON-first entry (`-2439_0`, position 1 of the SAME incLanes attribute) does
    // NOT key it (either absent, or -- if some other internal junction happens to also list it as
    // ITS OWN first entry -- resolves to a DIFFERENT internal junction, never `:2336_42_0`).
    // ============================================================================================
    [Fact]
    public void InternalJunctionByBayLane_KeyedOnFirstIncLanesEntryOnly()
    {
        var net = NetworkParser.Parse(SyntheticJunction2NetPath());

        Assert.NotNull(net.InternalJunctionByBayLane);
        Assert.True(net.InternalJunctionByBayLane!.TryGetValue(BayLane, out var resolved));
        Assert.Equal(InternalJunctionId, resolved!.Id);

        // Fixture assumption: `-2439_0` really is a NON-first incLanes entry of :2336_42_0 (position
        // 1 of "incLanes=\":2336_18_0 -2439_0\""), so this is testing exactly what it claims to.
        Assert.Equal(new[] { ":2336_18_0", "-2439_0" }, resolved.IncLanes);

        if (net.InternalJunctionByBayLane.TryGetValue("-2439_0", out var keyedByNonFirst))
        {
            Assert.NotEqual(InternalJunctionId, keyedByNonFirst!.Id);
        }
    }

    // ============================================================================================
    // Success condition 4 (design §7 T3.1.4): sweep all 134 committed *.net.xml -- every
    // `type="internal"` junction parses, every foe lane resolves to a REAL lane handle (via
    // `NetworkModel.LanesByHandle`), and the sweep asserts CORPUS FLOORS (>= 120 nets parsed, and
    // the 251 internal junctions of synthetic-junction2 all present) so a parser regression that
    // silently skips the loop body cannot pass this test vacuously (the exact weakness T2.1's
    // review caught). No bare catch around the per-net parse: a genuine parse failure fails loudly.
    // ============================================================================================
    [Fact]
    public void AllCommittedNets_EveryInternalJunctionParses_EveryFoeLaneIsARealHandle()
    {
        var netFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "scenarios"), "*.net.xml", SearchOption.AllDirectories).ToList();
        Assert.True(netFiles.Count >= 120, $"expected ~134 committed *.net.xml under scenarios/, found {netFiles.Count}.");

        var parsedNets = 0;
        var totalInternalJunctions = 0;
        var totalFoeLanesChecked = 0;
        var syntheticJunction2InternalJunctionCount = -1;

        foreach (var netFile in netFiles)
        {
            // Not every committed *.net.xml under scenarios/ is a full netconvert net (mirrors
            // JunctionIsLeaderTests.NoCommittedNet_ContainsAnIndirectConnection's own tolerance for
            // non-net XML fixtures) -- but a file that IS a real net.xml (has a <net> root) must
            // parse cleanly; no bare catch swallows a genuine parser failure.
            XDocument probe;
            try
            {
                probe = XDocument.Load(netFile);
            }
            catch
            {
                continue;
            }

            if (probe.Root?.Name.LocalName != "net")
            {
                continue;
            }

            var net = NetworkParser.Parse(netFile);
            parsedNets++;

            Assert.NotNull(net.InternalJunctions);

            // Every <junction type="internal"> in the raw XML must have a matching InternalJunction
            // record -- catches a parser regression that silently skips the loop entirely (an empty
            // InternalJunctions list would otherwise let a `count >= 0` style assertion pass
            // vacuously).
            var rawInternalJunctionIds = probe.Root!.Elements("junction")
                .Where(j => j.Attribute("type")?.Value == "internal")
                .Select(j => j.Attribute("id")!.Value)
                .ToList();

            var parsedInternalJunctionIds = net.InternalJunctions!.Select(ij => ij.Id).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(rawInternalJunctionIds.Count, net.InternalJunctions!.Count);
            foreach (var rawId in rawInternalJunctionIds)
            {
                Assert.Contains(rawId, parsedInternalJunctionIds);
            }

            totalInternalJunctions += net.InternalJunctions!.Count;

            if (netFile.Replace('\\', '/').EndsWith("_repro/synthetic-junction2/grid.net.xml", StringComparison.Ordinal))
            {
                syntheticJunction2InternalJunctionCount = net.InternalJunctions!.Count;
            }

            Assert.NotNull(net.InternalLaneFoes);
            foreach (var (_, foeHandles) in net.InternalLaneFoes!)
            {
                foreach (var handle in foeHandles)
                {
                    Assert.True(handle >= 0 && handle < net.LanesByHandle.Count,
                        $"net '{netFile}': foe lane handle {handle} does not resolve to a real lane.");
                    totalFoeLanesChecked++;
                }
            }
        }

        Assert.True(parsedNets >= 120, $"only {parsedNets} of {netFiles.Count} committed nets parsed as a <net> -- the sweep is not covering the corpus.");
        Assert.True(totalInternalJunctions > 0, "expected at least one internal junction across the corpus.");
        Assert.Equal(251, syntheticJunction2InternalJunctionCount);
        Assert.True(totalFoeLanesChecked > 0, "expected at least one foe-lane handle to have been checked across the corpus.");
    }
}
