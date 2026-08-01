using System.Collections.Generic;
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
/// <c>Sim.Viewer/Program.cs</c>'s list and extends it with the junction tags added later):
/// 0 none, 1 leaderFollow, 2 crossJxnLeader, 3 freeFlow, 4 successiveLane, 5 deadLaneMerge,
/// 6 stopLine, 7 redLight, 8 railSignal, 9 railCrossing, 10 junctionYield, 11 keepClear,
/// 12 obstacle, 13 crowd, 14 internalJunctionAdmission (lane-foe half -- a foe already STANDING on
/// a foe lane), 15 colocationSymmetryBreak, 16 crowdYield, 17 internalJunctionAdmission
/// (approach-arm half -- a foe APPROACHING, <c>InternalJunctionApproachArm</c>-gated; split out of
/// 14 so the two independent block reasons inside <c>InternalJunctionAdmissionConstraint</c> are
/// separately attributable, see that method's own header comment).
/// </para>
/// <para>
/// BLOCKER COLUMN: <see cref="VehicleExportSnapshot.BlockerEntityIndex"/>, the EntityIndex of the
/// foe/leader vehicle that the winning binder actually selected -- populated only for tags 1
/// (leaderFollow -- Geneva standoff-chain instrument, so a blocker chain can be followed THROUGH a
/// queue link), 2 (crossJxnLeader), 10 (junctionYield), 11 (keepClear -- the first stopped vehicle
/// its downstream space-walk found) and 14/17 (internalJunctionAdmission), the
/// constraints that identify a single blocking vehicle at their <c>Engine.ComputeMoveIntent</c> fold
/// call sites; -1 (VehicleRuntime.BlockerEntityIndex's default) for every other binder, and for any
/// of those three arms that itself has no single identifiable foe (e.g. JunctionYieldConstraint's
/// cycleHold/cautiousApproach/sameTargetMerge/externalAgent arms -- see that method's own comment).
/// Resolved to the blocker's SUMO vehicle-id STRING here, not left as a raw index, by buffering each
/// step's rows (via <c>OnFrameBegin</c>/<c>OnFrameEnd</c>) until every active vehicle's id for this
/// frame is known -- <c>OnVehicleExported</c> alone cannot resolve a blocker that exports LATER in
/// the same frame's iteration order. Falls back to <c>#&lt;index&gt;</c> when the index does not
/// resolve within the frame (blocker isn't active / a stale index), so a lookup miss is visible
/// in the CSV rather than silently dropped.
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
        "internalJunctionApproachArm", "urgentStrategicFollow", "urgentFollowerYield",
    };

    private readonly StreamWriter _writer;

    // Per-frame buffer (cleared at OnFrameBegin, flushed+resolved at OnFrameEnd): this diagnostic
    // observer is not on any hot sim path (CLAUDE.md rule 4's zero-alloc discipline binds
    // Engine.ComputeMoveIntent, not an opt-in CSV writer that already allocates a StreamWriter per
    // instance), so a small per-step Dictionary/List is the simplest correct shape rather than a
    // two-pass file re-read.
    private readonly Dictionary<int, string> _idByEntityIndex = new();
    private readonly List<Row> _rows = new();

    private readonly struct Row
    {
        public readonly double Time; public readonly string VehicleId; public readonly string Lane;
        public readonly double Pos; public readonly double Speed; public readonly byte Binder;
        public readonly byte JyArm;
        public readonly int BlockerEntityIndex;

        public Row(double time, string vehicleId, string lane, double pos, double speed, byte binder, byte jyArm, int blockerEntityIndex)
        {
            Time = time; VehicleId = vehicleId; Lane = lane; Pos = pos; Speed = speed; Binder = binder; JyArm = jyArm;
            BlockerEntityIndex = blockerEntityIndex;
        }
    }

    public BinderLogObserver(string path)
    {
        _writer = new StreamWriter(path);
        _writer.WriteLine("t,veh,lane,pos,speed,binder,binderName,jyArm,jyGreen,blocker");
    }

    public void OnFrameBegin(double time)
    {
        _idByEntityIndex.Clear();
        _rows.Clear();
    }

    public void OnVehicleExported(in VehicleExportSnapshot s)
    {
        // Straight off the snapshot. The FIRST version of this class read
        // Engine.BindingConstraints[s.EntityIndex] instead, and reported 100% OUT_OF_RANGE: that span is
        // indexed by READ-BUFFER COLUMN and is empty on a host that never pumps the read buffer, while
        // EntityIndex is the ECS entity index. The guard below caught it instead of silently logging
        // garbage -- keep it.
        _idByEntityIndex[s.EntityIndex] = s.VehicleId;
        _rows.Add(new Row(s.Time, s.VehicleId, s.Lane, s.Pos, s.Speed, s.BindingConstraint, s.JunctionYieldArm, s.BlockerEntityIndex));
    }

    public void OnFrameEnd(double time)
    {
        foreach (var row in _rows)
        {
            var name = row.Binder < BinderNames.Length ? BinderNames[row.Binder] : "OUT_OF_RANGE";
            string blocker;
            if (row.BlockerEntityIndex < 0)
            {
                blocker = "";
            }
            else if (_idByEntityIndex.TryGetValue(row.BlockerEntityIndex, out var blockerId))
            {
                blocker = blockerId;
            }
            else
            {
                // Blocker not exported this frame (not active / stale index) -- report the raw index
                // rather than silently dropping it, per this class's own header comment.
                blocker = "#" + row.BlockerEntityIndex.ToString(CultureInfo.InvariantCulture);
            }

            _writer.Write(row.Time.ToString("R", CultureInfo.InvariantCulture));
            _writer.Write(',');
            _writer.Write(row.VehicleId);
            _writer.Write(',');
            _writer.Write(row.Lane);
            _writer.Write(',');
            _writer.Write(row.Pos.ToString("R", CultureInfo.InvariantCulture));
            _writer.Write(',');
            _writer.Write(row.Speed.ToString("R", CultureInfo.InvariantCulture));
            _writer.Write(',');
            _writer.Write(row.Binder.ToString(CultureInfo.InvariantCulture));
            _writer.Write(',');
            _writer.Write(name);
            // jyArm is meaningful ONLY when the binder is 10 (junctionYield); emitted as -1 otherwise so
            // a reader cannot mistake a stale byte for an arm. Bit 0x80 is protected-green priority and
            // is split into its own column rather than left packed into the number.
            _writer.Write(',');
            _writer.Write(row.Binder == 10
                ? (row.JyArm & 0x0F).ToString(CultureInfo.InvariantCulture)
                : "-1");
            _writer.Write(',');
            _writer.Write(row.Binder == 10 && (row.JyArm & 0x80) != 0 ? "1" : "0");
            _writer.Write(',');
            _writer.WriteLine(blocker);
        }
    }

    public void Dispose() => _writer.Dispose();
}
