using System;
using System.IO;

namespace Sim.LiveCity;

// docs/LIVE-CITY-VIEWERS-DESIGN.md §1: the constructor knobs for LiveCitySim, mirroring the constants
// SceneGen.BuildLiveCity hard-codes (the PINNED downtown-HERO crop, the car/ped seeds, the demo's tuned
// car cap) so a fresh LiveCitySim reproduces the reference recipe byte-for-byte unless a caller
// deliberately overrides a knob. Env-var overrides (LIVECITY_CARS/LCMIN/YIELD) keep the same semantics
// as the reference so an existing shell habit ("run it with LIVECITY_CARS=300") still works against the
// new host.
// docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.1: which pedestrian-navigation provider `LiveCitySim`
// builds. `Navmesh` is today's existing WalkablePolygonBaker + SumoNavMesh path (the demo, pinned,
// byte-identical). `RouteGraph` is the arbitrary-net-import provider (SumoRouteGraphNav, a later
// stage's work) -- selecting it here is DATA-ONLY in this stage; the ctor does not yet branch on it
// (that lands in Stage C, task C1). `ForDataset` sets `RouteGraph`; `ForRepoRoot` (the demo) sets
// `Navmesh`.
public enum PedNavMode
{
    Navmesh,
    RouteGraph,
}

public sealed class LiveCityConfig
{
    // The demo_city/box dataset directory (contains net.xml + scenario.rou.xml). Set by ForRepoRoot or
    // by the caller directly.
    public string DatasetDir { get; set; } = string.Empty;

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.1: selects the pedestrian-navigation provider Stage C
    // wires up. Defaults to `Navmesh` (today's only wired behaviour); `ForDataset` sets `RouteGraph`.
    public PedNavMode NavMode { get; set; } = PedNavMode.Navmesh;

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.9, -TASKS.md C6: opt-in Engine.RegionPlan (a
    // bit-identical spatial-region decomposition of the parallel car plan -- see Engine.RegionPlan's
    // own header) for large road-net-import datasets. Default OFF so `ForRepoRoot` (the demo) keeps
    // today's Engine config byte-identical; `ForDataset` turns it on. Never enabled by parity/bench
    // (they construct their own Engine directly, never LiveCityConfig).
    public bool RegionPlan { get; set; } = false;

    // PINNED crop = SumoData's co-located downtown HERO block (SUMOSHARP-LIVE-CITY-DECISIONS.md Q7).
    public double X0 { get; set; } = 2055;
    public double Y0 { get; set; } = 2055;
    public double X1 { get; set; } = 2895;
    public double Y1 { get; set; } = 2895;

    // Max density: with the multi-lane overlap fix on main, the downtown crop holds ~157 concurrent
    // cars + 160 peds cleanly (SceneGen.BuildLiveCity's remarks). Overridable via LIVECITY_CARS.
    public int CarTargetConcurrent { get; set; } = 160;

    // A queued/standing car must not snap sideways a full lane -- it sorts into its lane only while moving.
    // 1.5 clamps more of the standing/crawling snaps than the 2D path's 1.0 (which still left ~15% residual)
    // for the 3D impression; keep <= ~2.0 so legitimate turn-lane sorting still happens and saturated queues
    // don't deadlock (any forward creep clears the gate). Overridable via LIVECITY_LCMIN.
    public double LaneChangeMinSpeed { get; set; } = 1.5;

    // #15 into-occupied cut-in floor (Engine.MergeStoppedMinGap). A moving car must not slot into the target
    // lane within this many metres AHEAD of a STANDING (near-stopped) follower there -- IsTargetLaneSafe is a
    // braking-gap check that a stopped follower satisfies at ~any closeness, so without this floor cars cut in
    // 2-5 m ahead of standing cars (the residual after the cooperative-LC float fix). Only active when
    // CooperativeLaneChange is on (high realism); low realism keeps the cheap tight merge. 0 = off (parity).
    // Overridable via LIVECITY_MERGEGAP. Default 5 m covers the measured 2-5 m follow-side cut-in band.
    public double MergeStoppedMinGap { get; set; } = 5.0;

    // #15 into-occupied, STRATEGIC (required) path only (Engine.MergeStoppedStrategicDeferDist). Urgency-gated
    // deferral: defer a tight cut-in into a stopped turn-lane queue only while ego still has more than this
    // much usable distance to complete the change; allow it once urgent so ego never strands. 0 = off (the
    // required merge is never deferred). Overridable via LIVECITY_MERGEDEFER (only active when
    // MergeStoppedMinGap>0 and coop on). Default 15 m: an A/B sweep found a sharp cliff -- <=20 m reduces the
    // strategic tight cut-ins (44->16, -64%) with NO flow change (arrivals 1068, stoppedFrac 0.34, identical
    // progression to no-defer), while >=25 m tips into congestion (arrivals 959, stoppedFrac 0.91) that
    // paradoxically breeds MORE stopped-follower cut-ins. 15 m sits comfortably below that cliff.
    public double MergeStoppedStrategicDeferDist { get; set; } = 15.0;

    // A/B switch: full crossing-yield gate + ped signal compliance vs the baseline (no coupling).
    // Overridable via LIVECITY_YIELD (0 = off).
    public bool YieldEnabled { get; set; } = true;

    // docs/LIVE-CITY-15-YIELD-TIMEOUT-DESIGN.md: after this many seconds waiting at a junction, a car
    // forces its gap through APPROACHING cross-traffic (impatience) instead of yielding forever -- the
    // "driver who didn't notice the gap, then recovers" behaviour. 0 = off (SUMO-parity). Only affects
    // the demo; never a parity/bench scenario. Overridable via LIVECITY_YIELDTIMEOUT.
    public double JunctionYieldTimeoutSeconds { get; set; } = 5.0;

    // SUMO's own jam escape valve (time-to-teleport): a vehicle stuck/jammed for this many seconds is
    // lifted past the blockage (CheckJamTeleports, already ported; gated off at <=0). SUMO default is
    // 300 s; the demo wants a SHORT recovery ("driver didn't notice the gap, recovers quickly"). At 5 s
    // the downtown crop goes from ~0.39 stopped to ~0.10 (free flow) and arrivals 81 -> 188 over 200 s.
    // 0 = OFF (default). Owner rejected teleport as an unrealistic cure ("the car needs to travel THROUGH
    // the junction, not jump across"), so it is off by default; the knob stays for experimentation only.
    // Overridable via LIVECITY_TELEPORT. Only the demo could enable it; scenarios/bench always leave it off.
    public double TimeToTeleportSeconds { get; set; } = 0.0;

    // docs/LIVE-CITY-15-DEADLANE-DRIVETHROUGH-DESIGN.md: never let a dead-ended car freeze forever --
    // free-flow-reroute or drive through on any forward connection instead. RE-MEASURED after the
    // lane-change-straddles-junction CURE (docs/LIVE-CITY-15-LANECHANGE-JUNCTION-FIX-DESIGN.md): with the
    // desync cascade gone, this + WrongLaneRerouteAtApproach make every wrong-lane car RECOVER (strand
    // reasons collapse to reResolveOK+rerouteOK only; strandedDeadEnd=0, stuckInternal=0-3, no capSpent
    // clamp), so a single wrong-lane car can no longer clamp Speed=0 and wall its queue. Default ON for
    // the demo (owner priority: floaters must not cause blockage). off = SUMO-parity clamp; every
    // parity/bench scenario leaves the underlying Engine property false (byte-identical).
    // Overridable via LIVECITY_DRIVETHROUGH (0 = off).
    public bool DeadLaneDriveThrough { get; set; } = true;

    // Issue #15: generalises TryReResolveFromActualLane/TryRerouteFromDeadLane to fire while a
    // wrong-lane car is still APPROACHING the junction (within its own brake distance of the dead
    // lane's end) and to retry every step rather than permanently one-shot-capping after
    // MaxDeadLaneReroutes -- see Engine.WrongLaneRerouteAtApproach's own header comment for the full
    // mechanism. ORIGINALLY measured as a regression (box-blocking) -- but that was BEFORE the
    // lane-change-straddles-junction CURE (docs/LIVE-CITY-15-LANECHANGE-JUNCTION-FIX-DESIGN.md). RE-
    // MEASURED after the cure: with the desync cascade gone, this + DeadLaneDriveThrough make every
    // wrong-lane car RECOVER instead of clamping -- strand reasons collapse to reResolveOK+rerouteOK
    // only (0 capSpent/poolEdgeMismatch), strandedDeadEnd=0, stuckInternal=0-3, stoppedFrac 0.99->~0.2-0.4,
    // arrivals 258->800+. A single wrong-lane car can no longer clamp Speed=0 and wall its queue (owner
    // priority: floaters must not cause blockage). Default ON for the demo; every parity/bench scenario
    // leaves the underlying Engine property false (byte-identical). Overridable via LIVECITY_WRONGLANE.
    public bool WrongLaneRerouteAtApproach { get; set; } = true;

    // docs/LIVE-CITY-15-COOPERATIVE-LC-DESIGN.md: cooperative lane change -- when a car needs a lane a
    // neighbour occupies, the neighbour (follower) eases off (one gentle helpDecel step) to open a gap
    // instead of the car stalling/floating. Sets BOTH Engine.CoordinatedLaneChange and
    // Engine.CooperativeInformFollower. Default ON for the demo (a saturated grid, the good case for this
    // mechanism -- see CooperativeInformFollower's own header comment for why it is organic-net poison
    // but saturated-grid medicine); every parity/bench scenario leaves both underlying Engine properties
    // false (byte-identical). Overridable via LIVECITY_COOP (0 = off).
    public bool CooperativeLaneChange { get; set; } = true;

    public int CarSpawnPerStep { get; set; } = 5;

    // A3 (docs/DENSITY-DIFF-HARNESS-DESIGN.md §1b): OPEN-LOOP inflow, in vehicles per SIMULATED SECOND.
    // `null` (the default) keeps the demo's normal CLOSED-LOOP behaviour byte-identical.
    //
    // WHY THIS EXISTS -- it is not a convenience knob, it is the difference between being able and unable to
    // measure junction discharge. The normal spawn loop is
    //     for (s = 0; s < CarSpawnPerStep && live < CarTargetConcurrent; s++)
    // which inserts ONLY while occupancy is below the cap. That makes inflow a function of our own drain: if
    // junctions discharge slowly we simply insert fewer cars, and resident count can never run away. A
    // discharge deficit manifests as UNBOUNDED QUEUE GROWTH AT FIXED INFLOW, so a closed-loop model cannot
    // exhibit the symptom at all -- and a comparison built on one reports "close to SUMO" no matter how
    // narrow the drain actually is. That is exactly what happened: a closed-loop run reported 96% of SUMO's
    // throughput while an open-loop experiment had SumoSharp climbing 258 -> 2623 resident cars over an hour
    // and never reaching steady state, against vanilla SUMO plateauing at ~430.
    //
    // When set, `CarTargetConcurrent` is IGNORED (there is no cap -- that is the point) and insertions are
    // paced by a fractional-credit accumulator so any real-valued rate is expressible, not just integer
    // multiples of `CarSpawnPerStep / Dt`.
    public double? CarInflowVehPerSec { get; set; }

    // step-length 0.5 == the ped/frame Dt, so cars and peds advance the same sim-time per Step().
    public double Dt { get; set; } = 0.5;

    // docs/LIVE-CITY-VISUALS-NOTES.md (tick-rate task): a convenience Hz view of Dt -- Dt = 1.0/Hz, so
    // SimHz = 20 <=> Dt = 0.05. This is the SAME knob as Dt, just expressed the way a CLI flag
    // (`--sim-hz`) or a viewer control naturally wants it; setting either one is visible through the
    // other immediately (no separate backing field). LiveCityConfig itself does NOT validate Hz against
    // the allowed set {1,2,5,10,20} -- any positive Dt/Hz is accepted here -- the CLI layer
    // (Sim.Viewer/Program.cs, City3D/Viewer/Main.cs) is where that enum is enforced, per the design's
    // "LiveCityConfig itself just takes a Dt" instruction.
    public double SimHz
    {
        get => Dt > 0.0 ? 1.0 / Dt : 0.0;
        set { if (value > 0.0) Dt = 1.0 / value; }
    }

    // Ped demand seed (SceneGen.BuildLiveCity's PedDemandConfig.Seed).
    public ulong PedSeed { get; set; } = 20260721UL;

    // Ped crowd size knobs (SceneGen.BuildLiveCity's PedDemandConfig). Overridable via LIVECITY_PEDS,
    // which sets the concurrent cap and scales the spawn rate proportionally so the crowd fills to the
    // new cap at about the same wall-time as the default 160 does.
    public int PedPopulationCap { get; set; } = 160;
    public double PedSpawnRatePerSecond { get; set; } = 8.0;

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §7, -TASKS.md D1: ped-demand knobs promoted from
    // LiveCitySim's ctor-hardcoded literals. Each default is the EXACT literal the ctor used to inline
    // (PedDemandConfig.MaxSpeed/Radius/ArrivalRadius/EnableWeave), so `ForRepoRoot` (the demo) builds a
    // byte-identical `PedDemandConfig` to before this knob existed. Not overridable via any LIVECITY_*
    // env var (unlike PedPopulationCap/PedSpawnRatePerSecond above) -- this stage only surfaces the
    // field on the config object; no new env knob was requested.
    public double PedMaxSpeed { get; set; } = 1.3;
    public double PedRadius { get; set; } = 0.3;
    public double PedArrivalRadius { get; set; } = 0.6;
    public bool PedEnableWeave { get; set; } = true;

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §7, -TASKS.md D1: the PedLivelinessConfig block, likewise
    // promoted as a group from LiveCitySim's ctor-hardcoded literals (same byte-identical-demo argument
    // as PedMaxSpeed et al. above).
    public double PedPauseProbability { get; set; } = 0.15;
    public double PedMinPauseSeconds { get; set; } = 2.0;
    public double PedMaxPauseSeconds { get; set; } = 5.0;
    public int PedMaxPausesPerTrip { get; set; } = 1;
    public string PedPauseAnimTag { get; set; } = "idle";

    // Car spawn PRNG seed (SceneGen.BuildLiveCity's `rng` initializer for the deterministic SplitMix64).
    public ulong CarRngSeed { get; set; } = 0x243F6A8885A308D3UL;

    // docs/LIVE-CITY-VIEWERS-TASKS.md A2: env knobs with the same semantics as the reference
    // (SceneGen.BuildLiveCity), resolved once here so callers get the exact same defaults/overrides.
    // Delegates to the shared `WithEnvOverrides` builder (docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.1)
    // so `ForDataset` below applies the IDENTICAL `LIVECITY_*` overrides -- only `DatasetDir` and
    // `NavMode` differ between the two factories. CRITICAL: this must remain field-for-field
    // identical to the pre-refactor `ForRepoRoot` (crop X0..Y1, all seeds, all knobs); only the demo's
    // `DatasetDir`/`NavMode` are set here, on top of the shared defaults.
    public static LiveCityConfig ForRepoRoot(string repoRoot)
    {
        var cfg = WithEnvOverrides(new LiveCityConfig());
        cfg.DatasetDir = Path.Combine(repoRoot, "scenarios", "_ped", "demo_city", "box");
        cfg.NavMode = PedNavMode.Navmesh;
        cfg.RegionPlan = false;
        return cfg;
    }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.1: the road-net-import factory -- an arbitrary
    // SUMO dataset directory (net.xml, with or without a companion scenario.rou.xml), routed on
    // `SumoRouteGraphNav` (Stage C wires the actual provider swap; this stage only sets the data
    // flag). No crop: `X0..Y1` are left at the shared builder's pinned-crop defaults, but road-net
    // mode ignores them (Stage C bypasses the crop predicates when `NavMode==RouteGraph`). Applies
    // the SAME `LIVECITY_*` env overrides as `ForRepoRoot` via the shared builder.
    public static LiveCityConfig ForDataset(string datasetDir)
    {
        var cfg = WithEnvOverrides(new LiveCityConfig());
        cfg.DatasetDir = datasetDir;
        cfg.NavMode = PedNavMode.RouteGraph;
        cfg.RegionPlan = true;
        return cfg;
    }

    // Shared builder: applies every `LIVECITY_*` env-var override to a fresh (or caller-supplied)
    // config and returns it. Both `ForRepoRoot` and `ForDataset` call this so a shell habit
    // ("run it with LIVECITY_CARS=300") behaves identically regardless of which factory launched the
    // sim. Factored out of the former `ForRepoRoot` body verbatim -- no override's semantics changed.
    private static LiveCityConfig WithEnvOverrides(LiveCityConfig cfg)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("LIVECITY_CARS"), out var cars))
        {
            cfg.CarTargetConcurrent = cars;
        }

        // LIVECITY_PEDS: concurrent ped cap; spawn rate scales with it so it fills at ~the default's pace.
        if (int.TryParse(Environment.GetEnvironmentVariable("LIVECITY_PEDS"), out var peds) && peds > 0)
        {
            cfg.PedPopulationCap = peds;
            cfg.PedSpawnRatePerSecond = 8.0 * System.Math.Max(1.0, peds / 160.0);
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("LIVECITY_LCMIN"), out var lcMin))
        {
            cfg.LaneChangeMinSpeed = lcMin;
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("LIVECITY_MERGEGAP"), out var mergeGap) && mergeGap >= 0.0)
        {
            cfg.MergeStoppedMinGap = mergeGap;
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("LIVECITY_MERGEDEFER"), out var mergeDefer) && mergeDefer >= 0.0)
        {
            cfg.MergeStoppedStrategicDeferDist = mergeDefer;
        }

        cfg.YieldEnabled = Environment.GetEnvironmentVariable("LIVECITY_YIELD") != "0";

        if (double.TryParse(Environment.GetEnvironmentVariable("LIVECITY_YIELDTIMEOUT"), out var yto) && yto >= 0.0)
        {
            cfg.JunctionYieldTimeoutSeconds = yto;
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("LIVECITY_TELEPORT"), out var tel) && tel >= 0.0)
        {
            cfg.TimeToTeleportSeconds = tel;
        }

        // Default OFF (measured regression, see the property's header); only an explicit
        // LIVECITY_WRONGLANE toggles it: "0" forces off, anything else forces on for experimentation.
        var wrongLaneEnv = Environment.GetEnvironmentVariable("LIVECITY_WRONGLANE");
        if (wrongLaneEnv != null)
        {
            cfg.WrongLaneRerouteAtApproach = wrongLaneEnv != "0";
        }

        // LIVECITY_DRIVETHROUGH: experimental "never freeze -- take any forward connection" fallback
        // (Engine.DeadLaneDriveThrough). Only overrides when explicitly set.
        var driveThroughEnv = Environment.GetEnvironmentVariable("LIVECITY_DRIVETHROUGH");
        if (driveThroughEnv != null)
        {
            cfg.DeadLaneDriveThrough = driveThroughEnv != "0";
        }

        // LIVECITY_COOP: cooperative lane change (Engine.CoordinatedLaneChange + CooperativeInformFollower).
        // Only overrides when explicitly set.
        var coopEnv = Environment.GetEnvironmentVariable("LIVECITY_COOP");
        if (coopEnv != null)
        {
            cfg.CooperativeLaneChange = coopEnv != "0";
        }

        // LIVECITY_HZ: same env-knob convention as LIVECITY_CARS/LCMIN above, expressed in Hz (via
        // SimHz) rather than raw Dt seconds since that's how a shell habit is more likely to want it.
        // No {1,2,5,10,20} validation here -- ForRepoRoot mirrors LIVECITY_CARS/LCMIN's own "any parsed
        // value is accepted" behavior; the CLI-facing --sim-hz flags do the enum validation.
        if (double.TryParse(Environment.GetEnvironmentVariable("LIVECITY_HZ"), out var hz) && hz > 0.0)
        {
            cfg.SimHz = hz;
        }

        return cfg;
    }
}
