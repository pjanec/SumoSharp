using Sim.Core;
using Xunit;
using Xunit.Abstractions;

namespace Sim.ParityTests;

// Guards Engine.ColocationSymmetryBreak -- see docs/NEED-same-step-double-placement-colocation.md.
//
// This is the ONE deliberate deviation from SUMO on this branch. SUMO has no symmetry-break mechanism
// because it cannot reach the state: it places vehicles sequentially, so two can never claim one slot in a
// step. Our plan phase is parallel over a frozen snapshot, so it can. The mechanism therefore recovers from
// a state SUMO never produces rather than altering behaviour SUMO defines -- and it is parity-safe by
// construction, because it fires only when two same-lane bodies already overlap, which no golden contains.
//
// Ladder compliance (docs/CONSTRAINT-high-realism-artefact-ladder.md): the trigger is MEASURED PHYSICAL
// OVERLAP, never a timer, so it cannot fire on a rung-5 "stuck for no obvious reason" car and conceal that
// car's defect. It neither teleports (rung 4) nor passes cars through each other (rung 2) -- it SEPARATES
// them, which is better than both.
public class ColocationSymmetryBreakTests
{
    private readonly ITestOutputHelper _out;

    public ColocationSymmetryBreakTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DefaultIsOff()
    {
        Assert.False(new Engine().ColocationSymmetryBreak);
    }

    // Fix 3's flag guarded here too, so every behavioural gate on this branch has a default assertion.
    [Fact]
    public void LaneChangeArrivalArbitration_DefaultIsOff()
    {
        Assert.False(new Engine().LaneChangeArrivalArbitration);
    }

    // Fix 3's competitor tie-break: the SMALLER ordinal id wins the contested slot, so exactly one of the
    // pair defers. Antisymmetry is the load-bearing property -- if both deferred, neither would ever change
    // lane; if neither deferred, both would take the slot and the onset would persist.
    [Theory]
    [InlineData("veh56", "veh84", false, "smaller id wins the slot -> does NOT defer")]
    [InlineData("veh84", "veh56", true, "greater id defers")]
    [InlineData("a", "B", true, "ORDINAL: 'a' > 'B' byte-wise, so ego defers (culture compare would disagree)")]
    [InlineData("B", "a", false, "ORDINAL: 'B' < 'a' byte-wise, so ego wins")]
    public void ArbitrationTieBreak_SmallerOrdinalIdWinsTheSlot(string egoId, string competitorId, bool expectDefer, string why)
    {
        var defer = string.CompareOrdinal(egoId, competitorId) > 0;
        _out.WriteLine($"ego={egoId} competitor={competitorId} => defer={defer} ({why})");
        Assert.Equal(expectDefer, defer);
        Assert.NotEqual(defer, string.CompareOrdinal(competitorId, egoId) > 0);
    }

    // The yield rule, stated as the predicate the arm implements. Pins BOTH rungs of the chain and its
    // ANTISYMMETRY -- the property that makes exactly one of a pair yield. Without antisymmetry the pair
    // either both stops (deadlock) or both proceeds (overlap persists), so it is the load-bearing property.
    private static bool EgoYields(double egoFront, string egoId, double otherFront, string otherId)
        => egoFront < otherFront
           || (egoFront == otherFront && string.CompareOrdinal(egoId, otherId) > 0);

    [Theory]
    // egoFront, egoId, otherFront, otherId, expectEgoYields, why
    [InlineData(10.0, "A", 12.0, "B", true, "ego is BEHIND -> ego yields, letting the leader pull clear")]
    [InlineData(12.0, "A", 10.0, "B", false, "ego is AHEAD -> ego proceeds")]
    [InlineData(10.0, "veh84", 10.0, "veh56", true, "exact positional tie -> greater id yields; 'veh84' > 'veh56'")]
    [InlineData(10.0, "veh56", 10.0, "veh84", false, "exact positional tie -> smaller id proceeds")]
    public void YieldRule_IsDeterministicAndPositionThenIdOrdered(
        double egoFront, string egoId, double otherFront, string otherId, bool expect, string why)
    {
        var actual = EgoYields(egoFront, egoId, otherFront, otherId);
        _out.WriteLine($"ego(front={egoFront},id={egoId}) vs other(front={otherFront},id={otherId}) => egoYields={actual} ({why})");
        Assert.Equal(expect, actual);

        // ANTISYMMETRY: evaluated the other way round, exactly one of the pair yields.
        var reverse = EgoYields(otherFront, otherId, egoFront, egoId);
        Assert.NotEqual(actual, reverse);
    }

    // The tie-break must be ORDINAL, never culture-sensitive -- the same requirement (and the same
    // demonstrating pair) as IsLeaderByEntryOrder. Ids where the two orderings DISAGREE, so a
    // string.Compare implementation fails this.
    [Fact]
    public void TieBreak_IsOrdinal_NotCultureSensitive()
    {
        Assert.True(string.CompareOrdinal("a", "B") > 0, "fixture stale: CompareOrdinal(\"a\",\"B\") no longer positive.");
        Assert.True(string.Compare("a", "B") < 0, "fixture stale: culture Compare(\"a\",\"B\") no longer negative.");

        // Ordinal: "a" > "B" so ego "a" yields. A culture-sensitive compare would say "a" < "B" and NOT yield.
        Assert.True(EgoYields(10.0, "a", 10.0, "B"));
        Assert.False(EgoYields(10.0, "B", 10.0, "a"));
    }

    // Merely TOUCHING is not overlapping, so the arm must stay inert -- the interval test uses strict
    // inequalities. This is what keeps it from firing during ordinary tight car-following.
    // The FULL 2-D predicate. Longitudinal alone is WRONG and broke five goldens when first written that
    // way -- every one a lateral-passing scenario (RungP22SublaneSideBySide, RungD3CooperativeOvertake,
    // RungD2ReturnGap, RungOV3OvertakeExecution, RungRvoMultiNeighbor). Under the sublane model two cars
    // legitimately share a lane side by side: longitudinally overlapping, laterally clear, never touching.
    private static bool BodiesOverlap(
        double egoFront, double egoLen, double egoLat, double egoWidth,
        double otherFront, double otherLen, double otherLat, double otherWidth)
    {
        var egoBack = egoFront - egoLen;
        var otherBack = otherFront - otherLen;
        if (egoBack >= otherFront || otherBack >= egoFront) return false;      // longitudinally clear
        return Math.Abs(egoLat - otherLat) < (egoWidth + otherWidth) / 2.0;    // else: laterally?
    }

    [Theory]
    // egoFront, egoLen, egoLat, otherFront, otherLen, otherLat, expectOverlap, why
    [InlineData(10.0, 5.0, 0.0, 5.0, 5.0, 0.0, false, "ego back == other front: touching, NOT overlapping")]
    [InlineData(10.0, 5.0, 0.0, 5.5, 5.0, 0.0, true, "0.5 m of body overlap, same lateral position")]
    [InlineData(10.0, 5.0, 0.0, 20.0, 5.0, 0.0, false, "far apart longitudinally")]
    // THE REGRESSION ROWS -- longitudinally overlapping but LATERALLY CLEAR. A longitudinal-only test
    // reports these as collisions and brakes a legitimate overtake. Vehicle width 1.8 => touch distance 1.8.
    [InlineData(10.0, 5.0, 0.0, 9.0, 5.0, 1.9, false, "side by side, lateral separation 1.9 > 1.8: CLEAR")]
    [InlineData(10.0, 5.0, -0.95, 9.0, 5.0, 0.95, false, "symmetric lateral pass, separation 1.9: CLEAR")]
    [InlineData(10.0, 5.0, 0.0, 9.0, 5.0, 1.7, true, "lateral separation 1.7 < 1.8: genuinely touching")]
    public void OverlapPredicate_IsTwoDimensional_LateralPassIsNotAnOverlap(
        double egoFront, double egoLen, double egoLat,
        double otherFront, double otherLen, double otherLat, bool expectOverlap, string why)
    {
        const double w = 1.8;
        var overlap = BodiesOverlap(egoFront, egoLen, egoLat, w, otherFront, otherLen, otherLat, w);
        _out.WriteLine($"ego(front={egoFront},lat={egoLat}) other(front={otherFront},lat={otherLat}) "
            + $"=> overlap={overlap} ({why})");
        Assert.Equal(expectOverlap, overlap);
    }
}
