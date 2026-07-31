using System.Diagnostics;

namespace Sim.Evac;

// PANIC-EVAC-PHASE5-DESIGN.md §4 (T4.2): opt-in per-phase wall-time accounting for EvacDirector.Tick,
// mirroring Engine.ProfilePhases (Sim.Core/Engine.cs -- same Stopwatch.GetTimestamp() pattern, same
// null-object shape). EvacDirector holds a nullable `EvacProfiler?` field that is null unless
// EnableProfiling() is called; every call site is then a `_profiler?.Begin()` / `_profiler?.End(...)`
// no-op when off, so the instrumentation is zero-cost and zero-behaviour when profiling is not
// requested (the default for every existing demo/test). Never on the parity path (Sim.Evac is already
// parity-exempt), so this cannot move the determinism hash regardless of on/off state.
public sealed class EvacProfiler
{
    public enum Phase
    {
        FearUpdate,      // PreStep: the observation-gather loop + FearField.Update(...)
        DiscFeeds,       // PreStep: FeedVehicleDiscsToPeds()
        PedestrianStep,  // PostStep: DrivePedestrians()
        PusherStep,      // PostStep: DriveOrcaPushers()
        EngineStep,      // Tick: the parity core, engine.Step() (context, not an evac cost)

        // PANIC-EVAC-PHASE5-TIER2-DESIGN.md §3d / TASKS T2.7: PreStep's AutoTrackWorkingRegion() scan
        // -- O(city) per tick (a full read-buffer scan looking for new entrants). Tier-1 folded this
        // into "other" (< 4%); Tier-2 measures it explicitly at 10k so the auto-track verdict (does it
        // need its own optimization?) is backed by a number instead of a guess.
        AutoTrackScan,
    }

    // Enum.GetValues(Type) (not the generic Enum.GetValues<T>(), which is net5+ only) so this compiles
    // on netstandard2.1 as well as net8.0 (§V2 E1: Evac folds into the portable SumoSharp package).
    private static readonly int PhaseCount = Enum.GetValues(typeof(Phase)).Length;

    private readonly long[] _phaseTicks = new long[PhaseCount];
    private long _tickTicks;
    private int _tickCount;

    public long Begin() => Stopwatch.GetTimestamp();

    public void End(long start, Phase phase) => _phaseTicks[(int)phase] += Stopwatch.GetTimestamp() - start;

    public void EndTick(long start)
    {
        _tickTicks += Stopwatch.GetTimestamp() - start;
        _tickCount++;
    }

    public ProfileSnapshot Snapshot()
    {
        static TimeSpan ToSpan(long ticks) => TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

        return new ProfileSnapshot(
            ToSpan(_phaseTicks[(int)Phase.FearUpdate]),
            ToSpan(_phaseTicks[(int)Phase.DiscFeeds]),
            ToSpan(_phaseTicks[(int)Phase.PedestrianStep]),
            ToSpan(_phaseTicks[(int)Phase.PusherStep]),
            ToSpan(_phaseTicks[(int)Phase.EngineStep]),
            ToSpan(_phaseTicks[(int)Phase.AutoTrackScan]),
            ToSpan(_tickTicks),
            _tickCount);
    }
}

// Cumulative per-phase wall time since profiling was enabled, plus the tick count they were
// accumulated over. `TotalTick` is the whole EvacDirector.Tick() wall time (PreStep + engine.Step +
// PostStep); the five phases below it are components of that total -- FearUpdate/DiscFeeds/
// PedestrianStep/PusherStep are the evac layer's own cost, EngineStep is the parity core (context,
// not an evac hotspot candidate). `TotalTick` minus the sum of the five is "other" (auto-track scan,
// blocked-detector update, conversion bookkeeping, etc.).
public readonly record struct ProfileSnapshot(
    TimeSpan FearUpdate,
    TimeSpan DiscFeeds,
    TimeSpan PedestrianStep,
    TimeSpan PusherStep,
    TimeSpan EngineStep,
    TimeSpan AutoTrackScan,
    TimeSpan TotalTick,
    int TickCount);
