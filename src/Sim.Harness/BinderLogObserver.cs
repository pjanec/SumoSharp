using System.Globalization;
using Sim.Core;

namespace Sim.Harness;

/// <summary>
/// Writes the per-vehicle, per-step BINDING CONSTRAINT ("binder") as CSV, alongside the pose the FCD
/// writer emits for the same frame.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. A trajectory shows a car stopped; it does not show WHY. Every junction
/// investigation in this repo eventually needs the same fact — which constraint won the
/// <c>Math.Min</c> fold at the step a vehicle did something surprising — and the difference between
/// "the guard evaluated and permitted" and "the guard was never consulted" has completely different
/// fixes. Guessing between them from the source is the reasoning that has been refuted seven times
/// here (docs/F3-SESSION-LOG.md §7 lesson 2, CLAUDE.md measurement discipline #2).
/// </para>
/// <para>
/// COMMITTED, not scratch, deliberately. A probe that is run once and deleted makes its own numbers
/// unfalsifiable and silently poisons every later comparison, because cross-instrument numbers are
/// never comparable (CLAUDE.md #8/#13). The last junction session lost a whole result that way.
/// </para>
/// <para>
/// It reads <see cref="VehicleExportSnapshot.BindingConstraint"/> — the diagnostic argmin of the
/// constraint fold, written on BOTH the pre-pass and the real pass so a <c>ReuseIntent</c> vehicle
/// reports its CURRENT binder rather than a stale one (Engine.cs:5412 and the T1.8 note there; stale
/// binder diagnostics have themselves produced a confident wrong attribution here).
/// NOT <see cref="Engine.BindingConstraints"/>: that span is indexed by read-buffer column and is
/// empty on a host that never pumps the read buffer.
/// </para>
/// <para>
/// TAG LEGEND (the authority is the fold in <c>Engine.ComputeMoveIntent</c>; this mirrors
/// <c>Sim.Viewer/Program.cs</c>'s list and extends it with the three junction tags added later):
/// 0 none, 1 leaderFollow, 2 crossJxnLeader, 3 freeFlow, 4 successiveLane, 5 deadLaneMerge,
/// 6 stopLine, 7 redLight, 8 railSignal, 9 railCrossing, 10 junctionYield, 11 keepClear,
/// 12 obstacle, 13 crowd, 14 internalJunctionAdmission, 15 colocationSymmetryBreak, 16 crowdYield.
/// </para>
/// <para>
/// NOT part of <c>dotnet test</c> and not on any parity path: it is attached only when a caller
/// explicitly registers it, and it never writes to the simulation.
/// </para>
/// </remarks>
public sealed class BinderLogObserver : ISimExportObserver, IDisposable
{
    /// <summary>Human-readable name per binder tag; index is the tag.</summary>
    public static readonly string[] BinderNames =
    {
        "none", "leaderFollow", "crossJxnLeader", "freeFlow", "successiveLane", "deadLaneMerge",
        "stopLine", "redLight", "railSignal", "railCrossing", "junctionYield", "keepClear",
        "obstacle", "crowd", "internalJunctionAdmission", "colocationSymmetryBreak", "crowdYield",
    };

    private readonly StreamWriter _writer;

    public BinderLogObserver(string path)
    {
        _writer = new StreamWriter(path);
        _writer.WriteLine("t,veh,lane,pos,speed,binder,binderName");
    }

    public void OnVehicleExported(in VehicleExportSnapshot s)
    {
        // Straight off the snapshot. The FIRST version of this class read
        // Engine.BindingConstraints[s.EntityIndex] instead, and reported 100% OUT_OF_RANGE: that span is
        // indexed by READ-BUFFER COLUMN and is empty on a host that never pumps the read buffer, while
        // EntityIndex is the ECS entity index. The guard below caught it instead of silently logging
        // garbage -- keep it.
        var tag = s.BindingConstraint;
        var name = tag < BinderNames.Length ? BinderNames[tag] : "OUT_OF_RANGE";

        _writer.Write(s.Time.ToString("R", CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.Write(s.VehicleId);
        _writer.Write(',');
        _writer.Write(s.Lane);
        _writer.Write(',');
        _writer.Write(s.Pos.ToString("R", CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.Write(s.Speed.ToString("R", CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.Write(tag.ToString(CultureInfo.InvariantCulture));
        _writer.Write(',');
        _writer.WriteLine(name);
    }

    public void Dispose() => _writer.Dispose();
}
