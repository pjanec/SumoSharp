using System;
using System.Collections.Generic;

namespace Sim.Replication.Recording;

// docs/LIVE-CITY-VIEWERS-DESIGN.md §2.1, docs/LIVE-CITY-VIEWERS-TASKS.md Stage C (C2) — the ped side of a
// `.simrec` replay. Peds render as plain regime-coloured discs (LiveCityOverlay), never through the
// KinematicReconstructor, so a per-frame position+regime snapshot is all replay needs -- no dead-reckoning,
// no history buffering, just "which frame is nearest <= t". A NEUTRAL tuple (no Sim.LiveCity dependency,
// per the design's explicit "no dependency on Sim.LiveCity" instruction) -- the viewer maps the `Regime`
// byte to its own `Sim.LiveCity.PedRegime` enum (the numeric values already agree: LowPowerWalking=0,
// HighPower=1, Paused=2).
//
// Eagerly loads every PEDFRAME record into memory up front (one linear pass over the file, ignoring every
// other record type) and answers PedsAt via binary search -- simple and robust at the minute-scale
// recordings this feature targets; a multi-hour recording would want a streaming/windowed version instead.
public sealed class PedFrameTrack
{
    private readonly List<(double Time, (int Id, float X, float Y, float Z, byte Regime, string AnimTag)[] Peds)> _frames = new();

    public PedFrameTrack(string path)
    {
        using var reader = new SimRecReader(path);
        while (reader.TryReadNext(out var entry))
        {
            if (entry.Kind == SimRecFormat.RecordType.PedFrame)
            {
                _frames.Add((entry.Time, entry.Peds!));
            }
        }
    }

    public int FrameCount => _frames.Count;

    // The PEDFRAME nearest <= t, or the earliest frame if t is before every recorded frame, or an empty
    // list if the recording has no ped frames at all.
    public IReadOnlyList<(int Id, float X, float Y, float Z, byte Regime, string AnimTag)> PedsAt(double t)
    {
        if (_frames.Count == 0)
        {
            return Array.Empty<(int, float, float, float, byte, string)>();
        }

        var lo = 0;
        var hi = _frames.Count - 1;
        var best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_frames[mid].Time <= t)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return _frames[best].Peds;
    }

    // docs/LIVE-CITY-VISUALS-NOTES.md-adjacent fix (ped smoothing task) -- PedsAt's nearest-frame-<=-t is a
    // step function (a ped holds its position for a whole recorded tick, then snaps), the exact same
    // visible jump the live path had before Sim.LiveCity.PedInterpolator. This is that interpolator's logic
    // reproduced here rather than shared: PedFrameTrack is deliberately Sim.LiveCity-free (this file's own
    // top-of-file doc comment), so it cannot reference Sim.LiveCity.PedInterpolator/PedInterpFrame; the
    // algorithm is the same (linear-by-id lerp between the two bracketing frames, hold at the ends, new ids
    // appear at the later position, missing ids are dropped) just re-expressed over the neutral tuple this
    // class already stores. `Z`/`AnimTag`/`Regime` are NOT interpolated (Z is always 0 on a flat ped net;
    // Regime/AnimTag are discrete state, taken from the LATER bracketing frame, same as PedInterpolator).
    public IReadOnlyList<(int Id, float X, float Y, float Z, byte Regime, string AnimTag)> PedsAtInterpolated(double t)
    {
        if (_frames.Count == 0)
        {
            return Array.Empty<(int, float, float, float, byte, string)>();
        }

        var oldest = _frames[0];
        if (_frames.Count == 1 || t <= oldest.Time)
        {
            return oldest.Peds;
        }

        var newest = _frames[^1];
        if (t >= newest.Time)
        {
            return newest.Peds;
        }

        var ai = 0;
        var lo = 0;
        var hi = _frames.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_frames[mid].Time <= t)
            {
                ai = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var a = _frames[ai];
        var b = _frames[ai + 1];
        var span = b.Time - a.Time;
        var f = span > 1e-9 ? (float)((t - a.Time) / span) : 1f;

        var bById = new Dictionary<int, (int Id, float X, float Y, float Z, byte Regime, string AnimTag)>(b.Peds.Length);
        foreach (var pb in b.Peds)
        {
            bById[pb.Id] = pb;
        }

        var result = new List<(int Id, float X, float Y, float Z, byte Regime, string AnimTag)>(b.Peds.Length);
        var seenInA = new HashSet<int>();

        foreach (var pa in a.Peds)
        {
            seenInA.Add(pa.Id);
            if (!bById.TryGetValue(pa.Id, out var pb))
            {
                continue; // gone in the later frame -> dropped
            }

            result.Add((pa.Id, pa.X + (pb.X - pa.X) * f, pa.Y + (pb.Y - pa.Y) * f, pb.Z, pb.Regime, pb.AnimTag));
        }

        foreach (var pb in b.Peds)
        {
            if (!seenInA.Contains(pb.Id))
            {
                result.Add(pb); // new since `a` -> appears at the later position
            }
        }

        return result;
    }
}
