using System;
using Xunit;

namespace CityLib.Tests;

/// A `[Fact]` that is SKIPPED unless `CITY3D_REALTIME_TESTS=1`.
///
/// WHY THIS EXISTS. A handful of tests in this suite drive the viewer's production wall-clock frame loop, and
/// every scenario they use has `step-length = 1` in its sumocfg — so one `sim.Tick()` is a whole second of sim
/// time, and the loop has to sleep `dt / FramesPerTick` to be the real-time render loop it claims to be.
/// `DrClock` advances its render clock at WALL rate scaled by a fitted wall↔sim rate and caps catch-up at
/// `frameDt · simRate · 3` (`DrClock.cs:255`, a deliberate anti-jump guard), so feeding it faster than real
/// time makes it fall behind and never recover — which is not a bug to work around but the contract to honour.
/// The consequence is unavoidable: these tests cost roughly one second of wall clock per simulated second,
/// ~30–40 s each, ~2 minutes for the group.
///
/// That is too slow to pay on every local run, and too valuable to delete: they are the ONLY end-to-end proof
/// that the reconstructor tracks the arc, pivots on the vehicle centre, and holds at a stop through the real
/// render loop. So they are opt-in.
///
/// SKIPPED, NOT EXCLUDED — deliberately. The test still appears in the run as `Skipped` with the reason
/// attached, so "I did not run these" is visible in the output rather than being the silent absence a
/// `--filter` exclusion or a `#if` would produce. A suppressed test that looks like a passing suite is worse
/// than a slow one.
///
/// To run them — the env var is the ONLY switch, because `Skip` is decided at DISCOVERY time:
///     CITY3D_REALTIME_TESTS=1 dotnet test demos/City3D/CityLib.Tests -c Release        (bash)
///     $env:CITY3D_REALTIME_TESTS=1; dotnet test demos\City3D\CityLib.Tests -c Release  (PowerShell)
///
/// `--filter Category=RealTime` does NOT enable them on its own (verified: it selects the four and they
/// still report Skipped) — a filter chooses which tests to consider, it cannot un-skip one. The trait is for
/// narrowing a run that has ALREADY opted in, i.e. run only the slow group and nothing else:
///     CITY3D_REALTIME_TESTS=1 dotnet test demos/City3D/CityLib.Tests -c Release --filter Category=RealTime
///
/// Run them after ANY change to `Sim.Viewer.Motion` (`DrClock`, `KinematicReconstructor`,
/// `KinematicHeading`), to `CityLib.Reconstructor`, or to the playout-delay/render-clock plumbing. Nothing
/// else covers those end to end.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealTimeFactAttribute : FactAttribute
{
    public const string EnvVar = "CITY3D_REALTIME_TESTS";

    public RealTimeFactAttribute()
    {
        if (!Enabled)
        {
            Skip = $"real-time paced (~30-40 s): set {EnvVar}=1, or --filter Category=RealTime, to run. "
                + "See RealTimeFactAttribute for why these cannot be made fast.";
        }
    }

    /// Whether the opt-in is active. Also useful to a test that wants to assert its own pacing assumption.
    public static bool Enabled =>
        Environment.GetEnvironmentVariable(EnvVar) is "1" or "true" or "TRUE";
}
