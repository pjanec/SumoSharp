using Sim.Pedestrians.Lod;
using Xunit;

namespace Sim.Pedestrians.Tests.Lod;

// PED-REALISM-1 Prototype 1 (docs/PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md): pins the deterministic low-power
// lateral weave -- pure, seeded, clamped, keep-right, endpoint-tapered. server==IG rests on this being a pure
// function of (s, seed, halfWidth), so determinism is the load-bearing property.
public class LateralWeaveTests
{
    private const double RouteLen = 60.0;
    private const double HalfWidth = 2.0;
    private static readonly WeaveParams P = WeaveParams.Default;

    [Fact]
    public void Deterministic_SameArgs_SameOffset()
    {
        for (var s = 0.0; s <= RouteLen; s += 1.0)
        {
            var a = LateralWeave.Offset(s, RouteLen, seed: 42, HalfWidth, P);
            var b = LateralWeave.Offset(s, RouteLen, seed: 42, HalfWidth, P);
            Assert.Equal(a, b, precision: 15);
        }
    }

    [Fact]
    public void EndpointsTaperToZero()
    {
        Assert.Equal(0.0, LateralWeave.Offset(0.0, RouteLen, seed: 7, HalfWidth, P), precision: 12);
        Assert.Equal(0.0, LateralWeave.Offset(RouteLen, RouteLen, seed: 7, HalfWidth, P), precision: 12);
    }

    [Fact]
    public void Offset_NeverCrossesCentreline_NorPastKerb()
    {
        // The load-bearing separation invariant: the offset is ALWAYS on the ped's own (right) half --
        // >= 0 so it never crosses the centreline into the opposing flow -- and never past the kerb
        // (<= halfWidth). With MinFrac = 0 peds may reach the centreline (populating it), but never cross it.
        for (var s = 0.0; s <= RouteLen; s += 0.1)
        {
            var off = LateralWeave.Offset(s, RouteLen, seed: 123, HalfWidth, P);
            Assert.True(off >= 0.0 && off <= HalfWidth + 1e-9, $"offset {off} not in [0, {HalfWidth}] at s={s}");
        }
    }

    [Fact]
    public void ZeroWidth_NoOffset()
    {
        Assert.Equal(0.0, LateralWeave.Offset(30.0, RouteLen, seed: 1, halfWidth: 0.0, P), precision: 15);
    }

    [Fact]
    public void ChangesLaneAlongRoute_NotConstant()
    {
        // The weave must VARY along arc-length (the "not a rigid car lane" property): sampling the interior
        // yields more than one distinct lateral value for a single ped.
        var seen = new System.Collections.Generic.HashSet<double>();
        for (var s = 5.0; s <= 55.0; s += 0.5)
        {
            seen.Add(System.Math.Round(LateralWeave.Offset(s, RouteLen, seed: 99, HalfWidth, P), 3));
        }

        Assert.True(seen.Count > 5, $"expected the offset to vary along the route (lane changes); distinct values = {seen.Count}");
    }

    [Fact]
    public void DifferentSeeds_DifferentLaneSequences()
    {
        // Two peds fan into a band: their lane sequences differ, so a same-direction flow is not a single line.
        var diff = false;
        for (var s = 5.0; s <= 55.0; s += 1.0)
        {
            if (System.Math.Abs(LateralWeave.Offset(s, RouteLen, 1, HalfWidth, P)
                              - LateralWeave.Offset(s, RouteLen, 2, HalfWidth, P)) > 0.05)
            {
                diff = true;
                break;
            }
        }

        Assert.True(diff, "different seeds should produce visibly different lateral tracks");
    }
}
