using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Sim.Core;
using Sim.Core.Bridge;
using Sim.Core.Orca;
using Sim.Host;
using Sim.Ingest;
using Sim.Pedestrians;
using Sim.Pedestrians.Crossing;
using Sim.Pedestrians.Demand;
using Sim.Pedestrians.Lod;
using Sim.Pedestrians.Navigation;
using Sim.Pedestrians.Navigation.Bake;
using Sim.Pedestrians.Navigation.RouteGraph;
using Sim.Replication;

namespace Sim.LiveCity;

// docs/LIVE-CITY-VIEWERS-DESIGN.md §1: `BuildLiveCity`'s wiring (src/Sim.Viz/SceneGen.cs) turned into a
// real-time, steppable, publish-ready host. Constructs the SAME coupled sim (net parsed twice, navmesh
// baked, CrosswalkSignals, CrossingOccupancySource, PedDemand/PedLodManager, InterestField pocket, Engine
// tuned for the demo's step-length/lanechange/speeddev, CrowdSource = Composite(HighPowerFootprints,
// crossingOccupancy)) and reproduces the reference's exact per-tick order in Step(). `LiveCitySim` does
// not render; it only steps, samples (Sample()), and -- as of this task -- publishes onto the same
// in-memory replication wire the local viewers already consume (CityLib/SimSource.cs, PedSimSource.cs).
public sealed class LiveCitySim : IDisposable
{
    private readonly LiveCityConfig _cfg;
    private readonly double _x0, _y0, _x1, _y1;
    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.4 (no crop): true only in `Navmesh` (demo) mode. In
    // `RouteGraph` mode every crop predicate below (the ctor's local `In`/`InV`, `Sample()`,
    // `SampleCars()`, and the `_cropEdges` filter) is bypassed -- road-net mode routes/samples/spawns
    // over the WHOLE net, never just the pinned demo crop.
    private readonly bool _cropEnabled;

    private readonly Engine _engine;
    private readonly VTypeHandle _vtype;
    private readonly List<(string Id, int Lane)> _cropEdges;
    private ulong _rng;

    private readonly PedPublisher _pedPublisher;
    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6: null when `PedestriansEnabled==false` (a bare
    // vehicle-only net -- no sidewalks). `PedSource`/`_pedPublisher` stay non-null regardless (the
    // wire is always constructed; it simply carries no peds when these are null).
    private readonly PedLodManager? _manager;
    private readonly PedDemand? _demand;
    private readonly InterestField _field;
    // The single ped-ORCA promotion source, re-centred AND re-sized on the live high-realism zone by
    // SetLcRealismZone so peds promote to full ORCA across the WHOLE highlighted zone wherever the viewer
    // looks (Follow/Locked), not only at the static crop-centre crossing. Position is mutated in place
    // (InterestSource doc: "an IG camera frustum carries its bubble with it"); radius is readonly on
    // InterestSource, so a radius change rebuilds the source (Remove + Register) -- the promoted-ped count
    // therefore scales with the zone radius by design (owner: "honor the zone radius, no matter perf").
    private InterestSource _orcaSource;
    private InterestSourceId _orcaSourceId;
    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6: null when `CrossingsEnabled==false` (no sidewalks,
    // or sidewalks but no crossings -- "walk-only" degrade). `OccupiedCrossings` reads 0 when null.
    private readonly Sim.Pedestrians.Crossing.CrossingOccupancySource? _crossingOccupancy;
    private readonly List<Vec2> _movingLowPowerPositions = new();
    private static readonly WorldDisc[] NoEntities = Array.Empty<WorldDisc>();

    // The car publish wire (mirrors CityLib/SimSource.cs).
    private readonly ReplicationPublisher _vehPublisher = new();
    private readonly InMemoryReplicationBus _vehBus = new();
    private bool _vehGeometryPublished;

    // docs/LIVE-CITY-VIEWERS-DESIGN.md §2.2, -TASKS.md Stage C (C1): an OPTIONAL tee onto a caller-supplied
    // sink (e.g. a RecordingReplicationSink writing a .simrec) -- purely additive, a null sink is the
    // default and costs nothing beyond the two null checks in Step(). A SEPARATE ReplicationPublisher
    // instance (never the live `_vehPublisher` above) drives it: ReplicationPublisher's own per-vehicle
    // lifecycle/adaptive-publish bookkeeping is STATEFUL, so sharing one instance across the live bus and
    // the record sink would make the second PublishStep call each tick see "already known" vehicles the
    // first call just announced, silently dropping spawn/despawn events from the recording. LiveCitySim
    // owns no file handle here and never disposes `_recordVehSink` -- the caller (RunLiveCity) constructs
    // and disposes it, exactly as the design's "LiveCitySim does not know about files" tenet requires.
    private readonly IReplicationSink? _recordVehSink;
    private readonly ReplicationPublisher? _recordPublisher;
    private bool _recordGeometryPublished;

    // docs/DENSITY-DIFF-HARNESS-DESIGN.md §2, -TASKS.md B1: OPTIONAL demand-recorder tee -- mirrors
    // `_recordVehSink`'s shape exactly (nullable, caller-owned sink; LiveCitySim knows nothing about
    // files). `_demandRouter` is a SEPARATE Sim.Ingest.NetworkRouter instance, built once over the
    // SAME NetworkModel Engine's own internal router uses, only when a sink is supplied -- Engine's
    // `SpawnVehicle(type, fromEdge, toEdge, ...)` overload already resolves the identical Dijkstra
    // shortest path internally but does not expose it, so this purely-additive duplicate call is the
    // only way to recover the vehicle's actual edge route for recording without touching Engine.cs.
    // It cannot diverge from what Engine inserted: same algorithm, same graph, same (fromId, toId).
    private readonly IDemandRecordSink? _recordDemandSink;
    private readonly Sim.Ingest.NetworkRouter? _demandRouter;
    private long _recordVehCounter;
    // The recorded file's own vType id -- independent of Engine's internal `__vtype0` id (never
    // exposed back to the caller), since the emitted .rou.xml only needs its `type=` attribute to
    // match the `<vType id=...>` it also emits, not Engine's internal bookkeeping.
    private const string RecordVTypeId = "car";

    // The ped publish wire (mirrors CityLib/PedSimSource.cs).
    private readonly PedReplicationPublisher _pedWirePublisher;
    private readonly InMemoryPedReplicationBus _pedBus = new();

    // docs/LIVE-CITY-VIEWERS-DESIGN.md §7, -TASKS.md Stage E (E3): the ped-side twin of `_recordVehSink`/
    // `_recordPublisher` above -- an OPTIONAL tee onto a caller-supplied ped sink (e.g. a
    // DdsPedReplicationSink for the combined cars+peds DDS producer), purely additive (null = unchanged
    // Stage A/C behaviour, the two extra null checks in Step() cost nothing). Exactly like the car tee, a
    // DEDICATED `PedReplicationPublisher` instance (never the live `_pedWirePublisher` above) drives it,
    // with its OWN scheduler/governor/meter -- mirrors the in-mem ped publish's own setup in the
    // constructor below so the record/DDS tee gates/measures its own stream independently of the live
    // in-mem wire (sharing gating state across the two would let one stream's suppression decisions leak
    // into the other's). LiveCitySim owns no DDS participant/file handle here and never disposes
    // `_recordPedSink` -- the caller constructs and disposes it, exactly as the vehicle tee's own remark
    // states.
    private readonly IPedReplicationSink? _recordPedSink;
    private readonly PedReplicationPublisher? _recordPedPublisher;

    // A3 (design §1b): fractional insertion credit for OPEN-LOOP inflow. Carries the sub-vehicle remainder
    // across steps so a rate like 1.7 veh/s is honoured exactly over time instead of truncating to 1/step.
    // Untouched (and therefore inert) unless `CarInflowVehPerSec` is set.
    private double _openLoopSpawnCredit;

    private double _now;
    private SimulationSnapshot _lastSnapshot = SimulationSnapshot.Empty;

    public LiveCitySim(
        LiveCityConfig cfg,
        IReplicationSink? recordVehSink = null,
        IPedReplicationSink? recordPedSink = null,
        IDemandRecordSink? recordDemandSink = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _recordVehSink = recordVehSink;
        _recordPublisher = recordVehSink is not null ? new ReplicationPublisher() : null;
        _recordPedSink = recordPedSink;
        _recordDemandSink = recordDemandSink;
        _x0 = cfg.X0; _y0 = cfg.Y0; _x1 = cfg.X1; _y1 = cfg.Y1;

        // docs/LIVE-CITY-VISUALS-NOTES.md "Shared foundation": load the static world-overlay scene
        // (zones/buildings/pois, all optional) once here so both viewers get it for free off `Scene`
        // instead of each re-parsing the dataset dir's JSON companions themselves.
        Scene = LiveCityScene.Load(cfg.DatasetDir);

        // docs/EXTERNAL-NET-VIEWER-DESIGN.md §1: the net path is `cfg.NetPath` when the caller set one
        // (an explicit path, a `scenario.net.xml` cut, or a `.sumocfg`-resolved path via ForSumocfg),
        // else the historical `<DatasetDir>/net.xml` convention -- see LiveCityConfig.ResolveNetPath.
        // ForRepoRoot/ForDataset leave NetPath null, so the demo resolves to the identical string.
        var netPath = cfg.ResolveNetPath();

        // net parsed twice (once for the vehicle-side NetworkModel, once for the ped-side PedNetwork) --
        // exactly as SceneGen.BuildLiveCity does; the two readers own disjoint models.
        var model = NetworkParser.Parse(netPath);
        Network = model;
        LocalLanes = new NetworkLaneSource(model);

        // B1: built only when a demand sink was supplied -- zero cost (no allocation, no adjacency
        // build) on the "recorder off" path this task's SC1 requires to stay byte-identical.
        _demandRouter = recordDemandSink is not null ? new Sim.Ingest.NetworkRouter(model) : null;

        // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6 (capability probe + graceful degrade): a malformed
        // ped-net (e.g. a crossing/walkingarea edge with no pedestrian lane, or an internal edge id
        // that doesn't match SUMO's ":<junction>_[cw]<N>" convention) degrades to "no pedestrians"
        // instead of throwing out of the ctor.
        PedNetwork pedNetwork;
        try
        {
            pedNetwork = PedNetworkParser.Load(netPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            pedNetwork = PedNetwork.Empty;
        }

        PedestriansEnabled = pedNetwork.Sidewalks.Count > 0;
        CrossingsEnabled = PedestriansEnabled && pedNetwork.Crossings.Count > 0;

        // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.2 (C1 mode branch): RouteGraph is the road-net
        // (arbitrary net) import path -- SumoRouteGraphNav, no sidewalk bake, no crop (§5.4). Navmesh
        // is today's ONLY-EVER-WIRED demo path (`ForRepoRoot`), untouched below.
        var routeGraphMode = cfg.NavMode == PedNavMode.RouteGraph;
        _cropEnabled = !routeGraphMode;

        bool In(double x, double y) => InCrop(x, y);
        bool InV(Vec2 p) => InCrop(p.X, p.Y);

        var cx = (_x0 + _x1) / 2.0;
        var cy = (_y0 + _y1) / 2.0;
        if (routeGraphMode)
        {
            // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.4: road-net mode has no crop, so the
            // realism-pocket/LC-zone default centre is the NET's own geometric centre (AABB over
            // every parsed lane shape) rather than a crop midpoint road-net mode ignores.
            var netCentre = ComputeNetAabbCentre(model);
            cx = netCentre.X;
            cy = netCentre.Y;
        }

        _pedPublisher = new PedPublisher();

        // Defaults for the "no pedestrians" degrade: the InterestField pocket / LC-realism zone still
        // need SOME centre, so they fall back to the crop centre (or, in RouteGraph mode, the net AABB
        // centre computed above) when there is no crossing to anchor on (either because peds are
        // disabled entirely, or CrossingsEnabled is false).
        var pocketCentre = new Vec2(cx, cy);
        var cropCrossingPolys = new List<BakedPolygon>();

        if (PedestriansEnabled)
        {
            IPedNavigation nav;
            List<Vec2> odPoints;

            if (routeGraphMode)
            {
                // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.2/§5.4 (C1): road-net mode skips
                // WalkablePolygonBaker.Bake/SumoNavMesh entirely -- SumoRouteGraphNav routes directly
                // on the PedNetwork's own lane/connection graph. No RerouteDriver is ever constructed
                // here either (§5.7/C4) -- `RouteGraphNavigationActive` below is the read-only witness
                // a test can assert this against.
                nav = new SumoRouteGraphNav(pedNetwork);
                RouteGraphNavigationActive = true;

                // C3 (§5.5): O/D sampled from whole-net sidewalk centrelines, no crop, deterministic
                // seeded stride (no System.Random).
                odPoints = SampleSidewalkCentrelineEndpoints(pedNetwork.Sidewalks);

                // C2 (§5.3): crossings-only bake (whole net, no crop) -- cheap vs. the full
                // sidewalk/walkingarea bake; feeds the SAME CrossingOccupancySource/CrosswalkSignals
                // wiring the Navmesh branch below uses.
                if (CrossingsEnabled)
                {
                    cropCrossingPolys.AddRange(WalkablePolygonBaker.BakeCrossingsOnly(pedNetwork));
                }
            }
            else
            {
                // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6: this stage keeps using the EXISTING
                // navmesh path (WalkablePolygonBaker + SumoNavMesh) whenever peds are enabled and
                // NavMode==Navmesh -- unchanged from before Stage C.
                var polygons = WalkablePolygonBaker.Bake(pedNetwork);
                nav = new SumoNavMesh(polygons, new SumoWalkableSpace(polygons), pedNetwork.PedConnections);

                // Pedestrian O-D endpoints = sidewalk spine midpoints inside the crop.
                var allEndpoints = new List<Vec2>();
                foreach (var poly in polygons)
                {
                    if (poly.Kind != BakedPolygonKind.SidewalkSegment) continue;
                    if (!InV(poly.Centroid)) continue;
                    var spine = poly.Spine;
                    var pt = spine is { Count: > 0 } ? spine[spine.Count / 2] : poly.Centroid;
                    if (InV(pt)) allEndpoints.Add(pt);
                }

                const int MaxEndpoints = 90;
                odPoints = new List<Vec2>();
                if (allEndpoints.Count <= MaxEndpoints)
                {
                    odPoints.AddRange(allEndpoints);
                }
                else
                {
                    var stride = (double)allEndpoints.Count / MaxEndpoints;
                    for (var k = 0; k < MaxEndpoints; k++) odPoints.Add(allEndpoints[(int)(k * stride)]);
                }

                // Crop crossings, split by signalization -- SceneGen.BuildLiveCity's Phase 2b split.
                foreach (var poly in polygons)
                {
                    if (poly.Kind == BakedPolygonKind.Crossing && InV(poly.Centroid)) cropCrossingPolys.Add(poly);
                }
            }

            // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6: sidewalks-but-no-crossings ("walk-only")
            // skips the crosswalk-signal wiring entirely, regardless of cfg.YieldEnabled -- there is no
            // crossing gate to compose it with. `CrosswalkSignals.FromNet` is only ever called when
            // there is at least one crossing to look up TL logic for.
            var crosswalkSignals = CrossingsEnabled ? CrosswalkSignals.FromNet(netPath, cropCrossingPolys) : null;

            var config = new PedDemandConfig
            {
                Origins = odPoints,
                Destinations = odPoints,
                SpawnRatePerSecond = cfg.PedSpawnRatePerSecond, // LIVECITY_PEDS scales this (default 8.0)
                PopulationCap = cfg.PedPopulationCap,           // LIVECITY_PEDS overrides this (default 160)
                Seed = cfg.PedSeed,
                // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §7, -TASKS.md D1: these were ctor-hardcoded
                // literals; now read from cfg, each defaulted to the exact former literal so
                // `ForRepoRoot` (the demo) builds a byte-identical PedDemandConfig.
                MaxSpeed = cfg.PedMaxSpeed,
                Radius = cfg.PedRadius,
                ArrivalRadius = cfg.PedArrivalRadius,
                Liveliness = new PedLivelinessConfig
                {
                    PauseProbability = cfg.PedPauseProbability,
                    MinPauseSeconds = cfg.PedMinPauseSeconds,
                    MaxPauseSeconds = cfg.PedMaxPauseSeconds,
                    MaxPausesPerTrip = cfg.PedMaxPausesPerTrip,
                    PauseAnimTag = cfg.PedPauseAnimTag,
                },
                EnableWeave = cfg.PedEnableWeave,
                CrosswalkSignals = cfg.YieldEnabled ? crosswalkSignals : null,
                CrosswalkWaitSpreadRadius = cfg.PedCrosswalkWaitSpreadRadius,
                SpeedVariationFrac = cfg.PedSpeedVariationFrac,
            };

            _manager = new PedLodManager(nav, _pedPublisher, arriveRadius: 0.3, dwellSeconds: 1.0);

            // Perf (A3, see LiveCityConfig.PedParallelOrca): plan the high-power ORCA crowd in parallel.
            // Bit-identical to serial (OrcaParallelStepTests) and self-gated at >=256 high-power agents, so
            // every small scenario -- including the whole test suite -- keeps the untouched serial path.
            _manager.UseParallelHighCrowd = cfg.PedParallelOrca;

            // A22: cap the crowd's parallel degree (see LiveCityConfig.MaxParallelism). Resolves to -1 for
            // every existing caller, so this is inert unless a host asks for headroom.
            _manager.HighCrowdMaxParallelism = cfg.ResolveMaxParallelism();
            _demand = new PedDemand(config, nav, _manager, startTime: 0.0);

            // The high-realism pocket, anchored on the crossing nearest the pocket centre (crop centre
            // for Navmesh, net AABB centre for RouteGraph -- see cx/cy above) -- the same "peds
            // actually walk here" anchoring SceneGen.BuildLiveCity uses. Stays at that default centre
            // when there is no crossing to anchor on.
            var bestD2 = double.PositiveInfinity;
            foreach (var poly in cropCrossingPolys)
            {
                var d2 = (poly.Centroid.X - cx) * (poly.Centroid.X - cx) + (poly.Centroid.Y - cy) * (poly.Centroid.Y - cy);
                if (d2 < bestD2) { bestD2 = d2; pocketCentre = poly.Centroid; }
            }

            // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6: no crossings -> no crossing-occupancy gate
            // (walk-only). Peds still walk via `_manager`/`_demand` above.
            if (CrossingsEnabled)
            {
                _crossingOccupancy = new Sim.Pedestrians.Crossing.CrossingOccupancySource(cropCrossingPolys, pedRadius: 0.3);
            }
        }

        const double promoteRadius = 70.0, demoteRadius = 100.0;
        _field = new InterestField();
        _orcaSource = new InterestSource(pocketCentre, promoteRadius, demoteRadius);
        _orcaSourceId = _field.Register(_orcaSource);

        // Expose the high-realism (ORCA-promotion) pocket so a viewer can render it: peds within
        // PromoteRadius of this centre are promoted to full ORCA; beyond DemoteRadius they fall back to
        // low-power dead-reckoning (hysteresis band in between). Centre is in SUMO x/y (world) coords.
        HighRealismPocketX = pocketCentre.X;
        HighRealismPocketY = pocketCentre.Y;
        HighRealismPromoteRadius = promoteRadius;
        HighRealismDemoteRadius = demoteRadius;

        // #15 camera-driven LC-realism zone (docs/LIVE-CITY-CAMERA-REALISM-ZONE-DESIGN.md): the per-area
        // lane-change realism gate starts ON the static pocket, so Central mode == the prior behaviour;
        // a viewer can later move/lock it to the camera via SetLcRealismZone.
        _lcZoneX = pocketCentre.X;
        _lcZoneY = pocketCentre.Y;
        _lcZoneR = promoteRadius;

        // ---- cars: real Engine on the full net; a dense LOCAL flow on the crop's drivable edges ----
        _engine = new Engine();
        // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.9, -TASKS.md C6: opt-in bit-identical spatial
        // region decomposition of the parallel car plan (Engine.RegionPlan's own header) for large
        // road-net-import datasets. Set BEFORE LoadNetwork per RegionPlan's own comment ("set before
        // LoadScenario"); false for the demo (`ForRepoRoot`) so its Engine config stays byte-identical.
        _engine.RegionPlan = cfg.RegionPlan;
        // A22 (see LiveCityConfig.MaxParallelism): cap the car plan/willPass/emit parallel region. -1 for
        // every existing caller, so the engine's configuration is byte-identical unless a host opts in.
        _engine.MaxParallelism = cfg.ResolveMaxParallelism();
        // docs/LIVE-CITY-VISUALS-NOTES.md (tick-rate task): step-length now tracks cfg.Dt instead of the
        // old hardcoded "0.5" literal -- the live-city coupling invariant (car Dt == ped Dt) requires the
        // engine's own resolution to move with LiveCityConfig.Dt, not just the ped publisher (which
        // already read cfg.Dt, see `stepDt: cfg.Dt` below). InvariantCulture is mandatory here: this
        // string is spliced into XML the engine re-parses with double.Parse -- a locale that renders
        // '.' as ',' (ToString() under a non-invariant thread culture) would corrupt the XML attribute
        // (e.g. "0,1" splitting into two malformed tokens), never a locale-dependent path.
        var stepLengthText = cfg.Dt.ToString(CultureInfo.InvariantCulture);
        // SUMO jam escape valve: splice <time-to-teleport> only when the demo enables it (>0); at 0 the
        // parser stores -1 (off), byte-identical to the pre-knob config.
        var teleportXml = cfg.TimeToTeleportSeconds > 0.0
            ? "<time-to-teleport value=\"" + cfg.TimeToTeleportSeconds.ToString(CultureInfo.InvariantCulture) + "\"/>"
            : string.Empty;
        var engineConfig = ScenarioConfigParser.ParseXml(
            "<configuration><time><begin value=\"0\"/><end value=\"1000000000\"/><step-length value=\""
            + stepLengthText + "\"/></time>"
            + "<processing><lanechange.duration value=\"2.0\"/><default.speeddev value=\"0.0\"/>" + teleportXml + "</processing></configuration>");
        _engine.LoadNetwork(netPath, engineConfig);
        _engine.LaneChangeMinSpeed = cfg.LaneChangeMinSpeed;
        // Task A (redo): suppress ONLY the held-car crowd-swerve -- a car held (nearly) stopped by a
        // laterally-static pedestrian (BindingConstraint == 13) recentres and waits in-lane instead of
        // steering a full lane-width sideways at ~0 forward speed (the demo's "floating"/wobble). This
        // replaces a reverted blanket lateral freeze, which also pinned cars MID-LANE-CHANGE -> straddle
        // -> trailing cars saw gap=Infinity -> queue overlaps (F2: veh17/26, 18/49, 117/26). The targeted
        // gate cannot straddle (it only recentres) and leaves moving-ped dodging / lane changes / passes
        // untouched. On by default (delivers the fix); LIVECITY_HELDSWERVE=0 disables for A/B. Guarded by
        // DemoCarOverlapInvariantTests' straddle test (F4a). See docs/LIVE-CITY-REALISM-AB-DESIGN.md §Task A
        // and docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md §F2.
        _engine.SuppressHeldCrowdSwerve = Environment.GetEnvironmentVariable("LIVECITY_HELDSWERVE") != "0";
        // F3 junction physical-occupancy gate (docs/F3-JUNCTION-OVERLAP-DESIGN.md). OFF by default --
        // the port is INCOMPLETE (missing SUMO's isLeader() entry-time symmetry break, so saturated
        // grids deadlock; see the Engine property comment). LIVECITY_F3OCCUPANCY=1 enables it for A/B
        // measurement of the crossing-internal-lane overlap it is meant to remove.
        _engine.JunctionPhysicalOccupancyGate = Environment.GetEnvironmentVariable("LIVECITY_F3OCCUPANCY") == "1";
        // F3/cont-turn predicate fix (docs/NEED-contturn-stuck-in-junction.md). OFF by default -- correct
        // in isolation but it regresses a saturated-grid diagnostic until checkRewindLinkLanes is ported
        // (see the Engine property comment). LIVECITY_CONTTURNFIX=1 enables it for A/B measurement of the
        // mid-junction freeze it removes.
        _engine.ContTurnInsideJunctionGate = EnvGate("LIVECITY_CONTTURNFIX", _engine.ContTurnInsideJunctionGate);
        // F3/isLeader entry-time ordering (docs/F3-ISLEADER-PORT-DESIGN.md). OFF by default. Faithful and
        // measurably safe, but on its own it does NOT resolve the arm-5 deadlock: the trace showed
        // IsLeader correctly releasing the yielding vehicle 121/121 steps while `FoeIsInTheWay` -- the
        // other half of SUMO's `isLeader(...) || inTheWay()` disjunction (MSVehicle.cpp:3429) -- stayed
        // true symmetrically. LIVECITY_ISLEADERFIX=1 for A/B.
        _engine.JunctionIsLeaderGate = EnvGate("LIVECITY_ISLEADERFIX", _engine.JunctionIsLeaderGate);
        // F3/internal-junction SECOND-STAGE admission (docs/F3-INTERNAL-JUNCTION-DESIGN.md) -- the port
        // that actually fixes the deadlock (veh 95/102 both arrive at SUMO's own --ignore-junction-blocker
        // default). OFF by default pending the owner's defaults decision. Wired here so the live-city F3
        // overlap buckets can be A/B'd at all: without this line the demo never exercises the gate, so a
        // bucket re-measurement would report "unchanged" for the trivial reason that nothing was enabled
        // -- an UNMEASURED condition masquerading as a neutral result. LIVECITY_INTERNALJUNCTIONFIX=1.
        _engine.InternalJunctionAdmissionGate = EnvGate("LIVECITY_INTERNALJUNCTIONFIX", _engine.InternalJunctionAdmissionGate);
        // Sub-gate of the line above (inert without it): applies `isLeader`'s entry-time ORDERING to a
        // bay-vs-bay foe instead of blocking on bare occupancy, which is symmetric and therefore wedges a
        // cycle of bays permanently (measured: 4 cars, junction d_5_4, 857+ steps, 48.1% of stall heads at
        // 3x). Separate flag so the A/B has one variable. LIVECITY_INTERNALJUNCTIONENTRYORDER=1.
        _engine.InternalJunctionAdmissionEntryOrder =
            EnvGate("LIVECITY_INTERNALJUNCTIONENTRYORDER", _engine.InternalJunctionAdmissionEntryOrder);
        // H-INS insertion follower-gap (pure-overlap) check -- docs/NEED-same-step-double-placement-colocation.md.
        // Refuses a departure that would bury the new car's REAR inside a car already queued just behind the
        // depart position. SUMO refuses these BY DEFAULT (insertionChecks = InsertionCheck::ALL), so this is a
        // faithfulness increase. OFF by default here only until measured. LIVECITY_INSERTIONFOLLOWERGAP=1.
        _engine.InsertionFollowerGapCheck = EnvGate("LIVECITY_INSERTIONFOLLOWERGAP", _engine.InsertionFollowerGapCheck);
        // Fix 2: co-location symmetry break -- lets an already-overlapping same-lane pair SEPARATE instead of
        // persisting (measured: longest episode 79 steps). Triggered by measured overlap only, never a timer.
        // LIVECITY_COLOCATIONSYMMETRYBREAK=1.
        _engine.ColocationSymmetryBreak = EnvGate("LIVECITY_COLOCATIONSYMMETRYBREAK", _engine.ColocationSymmetryBreak);
        // G1 of the checkRewindLinkLanes port (docs/NEED-checkrewindlinklanes-partial-port.md): propagate
        // junction blockage backward from a car that merely CANNOT PROCEED, not only one already halted.
        // Default OFF; the measurement that decides it is the OPEN-LOOP discharge test, not the goldens.
        // LIVECITY_KEEPCLEARHELD=1.
        _engine.KeepClearHeldPropagation = EnvGate("LIVECITY_KEEPCLEARHELD", _engine.KeepClearHeldPropagation);
        // Minor-link approach: SUMO's nonzero arrival-speed target instead of a stop-at-the-line plan.
        // LIVECITY_MINORARRIVALSPEED=1.
        _engine.MinorApproachArrivalSpeed = EnvGate("LIVECITY_MINORARRIVALSPEED", _engine.MinorApproachArrivalSpeed);
        // Fix 3: same-step lane-change arrival arbitration -- prevents the ONSET fixes 1/2 could only
        // mitigate (two cars changing into one slot in one step). LIVECITY_LANECHANGEARBITRATION=1.
        _engine.LaneChangeArrivalArbitration = EnvGate("LIVECITY_LANECHANGEARBITRATION", _engine.LaneChangeArrivalArbitration);

        // Task B-guard (docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md): inside the high-realism zone a car
        // YIELDS to a pedestrian in its path instead of weaving past it at speed, and can never pass one
        // at close distance AND high speed. Task A stopped the held car from FLOATING sideways; this stops
        // the car from dodging a CROSSING ped at 5 m/s (measured on the committed crosswalk repro: 0.70 m
        // of body-to-ped clearance at 3.90 m/s -> 2.05 m at 2.60 m/s). Pointed at the SAME camera-driven
        // LC-realism zone the viewer highlights, so the yield region is exactly the region the user sees;
        // SetLcRealismZone keeps the two in step. On by default; LIVECITY_PEDYIELD=0 disables for A/B.
        // Demo-only: parity/bench drive Engine directly and never set a zone, so it stays fully inert.
        // Read from CONFIG (LiveCityConfig.PedYieldEnabled, itself defaulted from LIVECITY_PEDYIELD in
        // ForRepoRoot) rather than from the environment here, so an A/B test can flip it per-instance
        // instead of mutating process-global state that a concurrently-running test would see.
        _pedYieldEnabled = cfg.PedYieldEnabled;
        if (_pedYieldEnabled)
        {
            _engine.SetCrowdYieldZone(_lcZoneX, _lcZoneY, _lcZoneR);
        }
        // #15 into-occupied: active only under cooperative (high-realism) LC; low realism keeps the cheap
        // tight merge. The engine helper is also caller-gated on CooperativeInformFollower, so this is
        // belt-and-suspenders (0 => the veto is fully inert).
        _engine.MergeStoppedMinGap = cfg.CooperativeLaneChange ? cfg.MergeStoppedMinGap : 0.0;
        _engine.MergeStoppedStrategicDeferDist = cfg.CooperativeLaneChange ? cfg.MergeStoppedStrategicDeferDist : 0.0;
        _engine.JunctionYieldTimeoutSeconds = cfg.JunctionYieldTimeoutSeconds;
        _engine.DeadLaneDriveThrough = cfg.DeadLaneDriveThrough;
        _engine.WrongLaneRerouteAtApproach = cfg.WrongLaneRerouteAtApproach;
        // docs/LIVE-CITY-15-COOPERATIVE-LC-DESIGN.md: cooperative lane change -- both flags gate together,
        // the informFollower is inert unless CoordinatedLaneChange is also on.
        _engine.CoordinatedLaneChange = cfg.CooperativeLaneChange;
        _engine.CooperativeInformFollower = cfg.CooperativeLaneChange;
        _engine.DiagSeqDesync = Environment.GetEnvironmentVariable("LIVECITY_SEQDESYNC") == "1"; // #15 prong-1
        _engine.DiagLaneChangeLog = Environment.GetEnvironmentVariable("LIVECITY_LCLOG") == "1"; // #15 float/swap analysis
        _vtype = _engine.DefineVType(new VTypeParams { VClass = "passenger", Sigma = 0.0 });
        // B1: report the vType once, before any RecordVehicle call, so the emitted file is
        // self-contained. No-op when `_recordDemandSink` is null.
        _recordDemandSink?.RecordVType(RecordVTypeId, vClass: "passenger", sigma: 0.0);

        // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6: vehicle-only (CrowdSource left null, "leave
        // CrowdSource vehicle-only") when peds are disabled; footprints-only (walk-only, no crossing
        // composite) when crossings are disabled; the full composite otherwise -- byte-identical to
        // today's behaviour whenever both are enabled (the demo).
        _engine.CrowdSource = _manager is null
            ? null
            : (cfg.YieldEnabled && _crossingOccupancy is not null)
                ? new CompositeFootprintSource(_manager.HighPowerFootprints, _crossingOccupancy)
                : _manager.HighPowerFootprints;

        // docs/EXTERNAL-NET-VIEWER-DESIGN.md §1: scrape the resolved route-file LIST (a `.sumocfg`'s
        // `<route-files>` is comma-separated and typically leads with vType files before the real
        // demand -- see LiveCityConfig.RoutePaths). Unset => the single `<DatasetDir>/scenario.rou.xml`
        // the demo has always used, so its scrape is byte-identical.
        var routeEdges = ReadDrivableEdges(cfg.ResolveRoutePaths());
        if (routeEdges.Count == 0)
        {
            // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.6: a dataset with no (or an empty)
            // scenario.rou.xml has no route-file edge scrape to seed spawn edges from -- derive them
            // straight from the parsed net instead: any edge with >=1 lane a road vehicle may use,
            // excluding internal (":"-prefixed) junction-interior edges. The demo always has a
            // populated scenario.rou.xml, so the scrape above wins there and this fallback never runs
            // (byte-identical demo edge set).
            routeEdges = DeriveDrivableEdgesFromNetwork(model);
        }

        _cropEdges = new List<(string Id, int Lane)>();
        foreach (var eid in routeEdges)
        {
            if (!model.EdgesById.TryGetValue(eid, out var edge) || edge.Lanes.Count == 0) continue;
            var carLane = edge.Lanes[^1];
            if (carLane.Shape.Count == 0) continue;
            var mid = carLane.Shape[carLane.Shape.Count / 2];
            if (In(mid.X, mid.Y)) _cropEdges.Add((eid, carLane.Index));
        }

        _rng = cfg.CarRngSeed;

        // ---- publish wires (mirrors CityLib/SimSource.cs + CityLib/PedSimSource.cs) ----
        VehicleSource = _vehBus.Source;

        var scheduler = new PedPublishScheduler(new PedDrErrorPublishPolicy());
        var meter = new PedBandwidthMeter();
        var governor = new PedBandwidthGovernor(scheduler, meter, maxMbitPerSecond: 500.0);
        _pedWirePublisher = new PedReplicationPublisher(_pedBus.Sink, scheduler, governor, meter, stepDt: cfg.Dt);
        PedSource = _pedBus.Source;

        // Stage E (E3) tee: an entirely SEPARATE scheduler/meter/governor triple, wired to the caller's
        // sink -- see `_recordPedSink`'s field remark for why this must not share state with the live
        // in-mem publisher above.
        if (_recordPedSink is not null)
        {
            var recordScheduler = new PedPublishScheduler(new PedDrErrorPublishPolicy());
            var recordMeter = new PedBandwidthMeter();
            var recordGovernor = new PedBandwidthGovernor(recordScheduler, recordMeter, maxMbitPerSecond: 500.0);
            _recordPedPublisher = new PedReplicationPublisher(
                _recordPedSink, recordScheduler, recordGovernor, recordMeter, stepDt: cfg.Dt);
        }
    }

    public NetworkModel Network { get; }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6 (capability probe): true iff the loaded (or
    // try/catch-degraded) PedNetwork has at least one sidewalk lane. False for a bare vehicle-only net
    // or one whose ped geometry failed to parse -- the ctor then skips the ped nav/demand/LOD-manager/
    // crossing-occupancy/crosswalk-signals wiring entirely and cars run alone (`PedSource` stays
    // non-null but carries no peds).
    public bool PedestriansEnabled { get; }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6: true iff `PedestriansEnabled` AND the network has at
    // least one crossing. False means either no peds at all, or peds walk with no crossing-occupancy
    // gate / crosswalk-signal coupling ("walk-only" degrade) -- composes with `cfg.YieldEnabled`
    // (both must effectively hold for the crossing gate to be wired).
    public bool CrossingsEnabled { get; }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.7, -TASKS.md C4: read-only diagnostic witness --
    // true iff the ctor built `SumoRouteGraphNav` (NavMode==RouteGraph and PedestriansEnabled). False
    // for the Navmesh demo path (SumoNavMesh) and for a bare/degraded net with no peds at all. Proves
    // road-net mode never constructs `RerouteDriver`/the concrete `SumoNavMesh` -- `RerouteDriver`
    // holds a `SumoNavMesh`, and this flag is only ever true when the nav object is the OTHER provider.
    public bool RouteGraphNavigationActive { get; }

    // The static world-overlay scene (zones/buildings/pois) loaded once from cfg.DatasetDir in the ctor.
    public LiveCityScene Scene { get; }

    public NetworkLaneSource LocalLanes { get; }

    public IReplicationSource VehicleSource { get; }

    public IPedReplicationSource PedSource { get; }

    public double Time => _now;

    public int PeakCars { get; private set; }

    public int PeakPeds { get; private set; }

    // Cumulative count of vehicles that finished their route and left the sim (Engine.Events, kind
    // Arrived, tallied each Step). The #15 gridlock signal: in free flow this climbs steadily; under the
    // junction-discharge deadlock it flatlines near zero even while cars are still on the road (cars have
    // destinations but never reach them). Host-side read-only metric only -- never feeds the engine.
    public long ArrivedTotal { get; private set; }

    // B1 (docs/DENSITY-DIFF-HARNESS-TASKS.md, SC4): read-only passthroughs of Engine's own already-
    // computed counts, for a caller (Sim.DensityDiff) to derive "vehicles spawned but neither active
    // nor arrived" (a still-Pending/refused-insertion proxy) without Engine exposing a dedicated
    // refusal counter. Zero cost beyond the property read; never used by any golden/parity path.
    public int CurrentCars => _engine.VehicleHandles.Length;

    public int CurrentPeds => _demand?.LiveCount ?? 0;

    // LIVE-CITY-PERF-DESIGN.md ADD1: read-only passthrough to the live ped LOD manager's high-power
    // (full ORCA, inside the high-realism pocket) count -- PedLodManager.HighPowerCount. 0 when peds
    // are disabled (`_manager` null). Ped cost is dominated by how many peds are high-power, not by
    // the raw ped count, so a ped total alone is an uninterpretable workload number; this is the split
    // Sim.BenchLiveCity reports alongside it. Additive, read-only, never consulted by Step() -> zero
    // behavioral effect.
    public int PedHighPowerCount => _manager?.HighPowerCount ?? 0;

    // The live pedestrian demand, or null on a net with no pedestrians (`PedestriansEnabled == false`).
    //
    // READ-MOSTLY. `Step()` mirrors `_cfg`'s density knobs into this object every tick (see
    // MirrorPedDensity), so calling `SetPopulationCap`/`SetSpawnRatePerSecond` on it directly is
    // overwritten on the next step. It is exposed for READING -- `SpawnEvents` for a determinism check,
    // the live `PopulationCap`/`SpawnRatePerSecond` for a slider label. Drive density through
    // `SetPedDensity` or through the config object.
    public PedDemand? PedDemand => _demand;

    // docs/EXTERNAL-NET-VIEWER-DESIGN.md §3 (C3): change the pedestrian density LIVE -- takes effect on
    // the next Step(), with no sim rebuild. This is the knob a free-style density slider drives, in
    // BIG's Spectacle scene and the Godot City3D viewer alike; before it existed both had to rebuild
    // the whole sim (their ped sliders were documented as "applies on Restart").
    //
    // Raising converges upward at the given rate; LOWERING stops new spawns but does not despawn
    // anybody -- live peds drain as they arrive, exactly as lowering `CarTargetConcurrent` drains cars.
    // See PedDemand.SetPopulationCap for why that asymmetry is deliberate.
    //
    // A silent no-op when this net has no pedestrians, so a slider handler needs no capability guard.
    public void SetPedDensity(int populationCap, double spawnRatePerSecond)
    {
        // `cfg` FIRST and always -- it is the single source of truth (see MirrorPedDensity). Writing
        // only the demand would leave `cfg` stale, so a UI that reads `cfg` to position its slider shows
        // the old number, and the two silently disagree. Clamping happens HERE, into cfg, rather than
        // only inside the demand setter: otherwise cfg would keep a negative that gets re-clamped on
        // every single step.
        _cfg.PedPopulationCap = populationCap < 0 ? 0 : populationCap;
        _cfg.PedSpawnRatePerSecond = spawnRatePerSecond;

        // Apply immediately as well as on the next Step(), so a caller that reads CurrentPeds or
        // PedDemand.PopulationCap right after setting sees the value it just set.
        MirrorPedDensity();
    }

    // docs/EXTERNAL-NET-LOADING-DESIGN.md §4 / -TASKS.md D1: push the config's ped-density knobs into the
    // live `PedDemand`. Called at ONE fixed point at the top of `Step()`, before any spawn logic, and
    // from `SetPedDensity` above.
    //
    // WHY THIS EXISTS AT ALL. `PedDemandConfig` is built once in the ctor, so before this the ped knobs
    // were write-once: mutating `cfg.PedPopulationCap` did nothing, forever. That is exactly what the
    // BIG/Spectacle handoff requires to work ("please keep Step() reading these off the by-reference cfg
    // each tick ... so a slider takes effect without a sim rebuild"), and exactly what the CAR knobs have
    // always done -- `Step()` reads `_cfg.CarTargetConcurrent` every tick. This makes the two halves obey
    // one rule instead of two.
    //
    // COSTS NOTHING WHEN NOTHING CHANGED: `PedDemand.SetSpawnRatePerSecond` early-returns on an unchanged
    // rate (so no RNG stream is disturbed and no reschedule is queued), and setting an unchanged cap is a
    // plain field assignment. A run that never touches a knob is therefore bit-identical to one from
    // before this method existed.
    //
    // CONSEQUENCE, deliberate: because this runs every step, poking `PedDemand.SetPopulationCap(...)`
    // directly is overwritten on the next `Step()`. `PedDemand` is exposed READ-MOSTLY (for SpawnEvents
    // and the live values); drive density through `SetPedDensity` or `cfg`, not through the demand.
    private void MirrorPedDensity()
    {
        if (_demand is null)
        {
            return;
        }

        _demand.SetPopulationCap(_cfg.PedPopulationCap);
        _demand.SetSpawnRatePerSecond(_cfg.PedSpawnRatePerSecond);
    }

    // docs/LIVE-CITY-THREADED-TICK-DESIGN.md §6 Stage 1b: a settable timestep, so a viewer slider can
    // retune the tick rate at RUNTIME with no sim rebuild. `Step()` already reads `_cfg.Dt` fresh at the
    // top of every call (see `var dt = _cfg.Dt;` there), so this property is a thin forwarding wrapper
    // over the SAME by-reference `_cfg` the density knobs above use -- writing it here is felt on the very
    // next `Step()`. Additive only: the default stays `LiveCityConfig.Dt`'s own default (0.5s = 2 Hz), so
    // nothing changes unless a caller actually sets this.
    public double Dt
    {
        get => _cfg.Dt;
        set => _cfg.Dt = value;
    }

    // The car-side twin of SetPedDensity, for symmetry at the call site. Cars needed no engine change:
    // `Step()` already reads `CarTargetConcurrent`/`CarSpawnPerStep` off the by-reference `_cfg` every
    // tick, so writing them here is felt on the next tick. This method exists so a viewer has ONE
    // obvious API for "set the density" instead of having to know that trick -- and so the two
    // densities are driven the same way from the same place.
    //
    // `spawnPerStep` null (the default) leaves the per-step insertion budget alone. Note the cap is
    // IGNORED entirely while `CarInflowVehPerSec` is set (open-loop mode has no cap by design -- see
    // that property's own header), so this is a no-op for a caller running an open-loop measurement.
    public void SetCarDensity(int targetConcurrent, int? spawnPerStep = null)
    {
        _cfg.CarTargetConcurrent = targetConcurrent < 0 ? 0 : targetConcurrent;
        if (spawnPerStep is { } perStep)
        {
            _cfg.CarSpawnPerStep = perStep < 0 ? 0 : perStep;
        }
    }

    // DIAGNOSTIC (#15 residual): how many vehicles hit the wrong-lane dead-end clamp in the engine's
    // last step (a turner that could not merge into its turn lane and stranded at the stop line with no
    // onward connection). This is the strand that upstream lane-change cooperation would PREVENT --
    // measuring it against the total stuck-on-green count decides whether that fix is the right lever.
    public int StrandedOffRouteLastStep => _engine.StrandedOffRouteThisStep;

    // #15 diagnostic passthrough: cumulative histogram of WHY wrong-lane cars resolved as they did at a
    // lane end (indices per Engine.StrandReasonHistogram). Read as deltas across samples to see the live
    // mix of recovered-vs-stranded and, among strands, the dominant cause.
    public System.ReadOnlySpan<long> StrandReasonHistogram => _engine.StrandReasonHistogram;

    // #15 float/swap analysis passthrough: committed lane changes by [path][changer-speed] (flattened
    // path*3+spd; path 0 overtake 1 speedGain 2 strategic 3 keepRight; spd 0 stopped<0.5 1 slow<2 2 moving)
    // and, per path, commits where a target-lane car <20m is stopped (swap into an occupied stretch).
    public System.ReadOnlySpan<long> LaneChangeByPathChangerSpeed => _engine.LaneChangeByPathChangerSpeed;
    public System.ReadOnlySpan<long> LaneChangeTargetNearStopped => _engine.LaneChangeTargetNearStopped;
    public System.ReadOnlySpan<long> LaneChangeIntoStoppedDetail => _engine.LaneChangeIntoStoppedDetail;

    // #15 cooperative-LC diagnostic passthrough: cumulative count of SpeedAdvice writes issued from the
    // STRATEGIC informFollower path (Engine.TryStrategicLaneChange). >0 confirms cooperation actually
    // fires; 0 on every parity/bench golden (both underlying Engine flags default false there).
    public long CoopAdviceIssued => _engine.CoopAdviceIssued;

    // DIAGNOSTIC (#15 SUMO cross-check): when non-null, every successful car spawn is appended here
    // (departTime, fromEdge, toEdge) so the exact procedural demand can be exported to a SUMO .rou.xml
    // and run through vanilla SUMO for an apples-to-apples throughput comparison. Null (default) = no
    // recording, no cost.
    public List<(double Depart, string From, string To)>? SpawnLog { get; set; }

    // docs/LIVE-CITY-ARBITRARY-NET-TASKS.md A2: read-only diagnostic exposing the resolved vehicle
    // spawn-edge set (route-file scrape, or the net.xml drivable-edge fallback when the scrape is
    // empty) -- lets a test assert the demo's edge set is unchanged and that a no-rou-file dataset's
    // fallback produced a sane vehicle-allowed, non-internal edge set.
    public IReadOnlyList<(string Id, int Lane)> CropEdges => _cropEdges;

    public int OccupiedCrossings => _crossingOccupancy?.OccupiedCount ?? 0;

    public int PeakOccupiedCrossings { get; private set; }

    public int CarYieldObservations { get; private set; }

    // The high-realism (ORCA-promotion) InterestField pocket, for viewers to render (SUMO world coords).
    public double HighRealismPocketX { get; private set; }
    public double HighRealismPocketY { get; private set; }
    public double HighRealismPromoteRadius { get; private set; }
    public double HighRealismDemoteRadius { get; private set; }

    // Diagnostic-only (docs/LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md §1), ADDITIVE, read-only: a passthrough
    // to the ped LOD manager's per-ped internal state (Model/HighIndex/dwell timers/route/pos), for the
    // headless --live-city-pedtrace harness to correlate server-side LOD transitions against the wire.
    // Empty when peds are disabled (`_manager` null). Never consulted by Step() or any sim behavior.
    public IEnumerable<Sim.Pedestrians.Lod.PedLodDiag> PedLodDiagnostics(double now)
        => _manager?.DiagnosticSnapshot(now) ?? Array.Empty<Sim.Pedestrians.Lod.PedLodDiag>();

    // #15 camera-driven LC-realism zone (docs/LIVE-CITY-CAMERA-REALISM-ZONE-DESIGN.md). The per-area
    // lane-change realism gate in Step() tests against THIS zone (not the static ped-ORCA pocket above),
    // so the viewer can move it to the camera look-at (Follow) or freeze it (Locked). SUMO world coords;
    // radius <= 0 disables the gate (all cars high realism). Initialised to the static pocket (Central).
    private double _lcZoneX;
    private double _lcZoneY;
    private double _lcZoneR;

    // Task B-guard opt-out latch (LIVECITY_PEDYIELD=0). Read in the ctor and honoured by every later
    // SetLcRealismZone push, so the A/B arm never re-arms the yield zone behind the flag's back.
    private readonly bool _pedYieldEnabled;
    public double LcZoneX => _lcZoneX;
    public double LcZoneY => _lcZoneY;
    public double LcZoneRadius => _lcZoneR;

    // Task B-guard: the ENGINE's live car->ped yield zone (docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md §3.0).
    // Read-only pass-throughs so a viewer can render exactly the region the yield is armed over, and so a
    // test can confirm it tracks SetLcRealismZone rather than drifting from the highlighted zone. Radius
    // stays 0 for the whole run when LIVECITY_PEDYIELD=0 (the A/B baseline arm).
    public double PedYieldZoneX => _engine.CrowdYieldZoneX;
    public double PedYieldZoneY => _engine.CrowdYieldZoneY;
    public double PedYieldZoneRadius => _engine.CrowdYieldZoneRadius;

    // Set the LC-realism zone (the viewer pushes this once per step BEFORE Step(), for Follow/Locked
    // modes). Demo-only: parity/bench drive Engine directly, never LiveCitySim, so goldens never call this
    // and the classification stays byte-identical (Central mode leaves the zone on the static pocket).
    public void SetLcRealismZone(double centreX, double centreY, double radius)
    {
        _lcZoneX = centreX;
        _lcZoneY = centreY;
        _lcZoneR = radius;

        // Task B-guard: the car->ped yield region follows the same zone (see the ctor's own comment).
        if (_pedYieldEnabled)
        {
            _engine.SetCrowdYieldZone(centreX, centreY, radius);
        }

        // Unify ped ORCA with the high-realism zone: peds within the zone promote to full ORCA (turn
        // high-power) wherever the viewer looks. The promote radius follows the zone radius (owner
        // requirement: honor the zone radius regardless of perf), with a proportional demote-radius
        // hysteresis band. Position mutates in place; a radius change rebuilds the (readonly-radius) source.
        var centre = new Vec2(centreX, centreY);
        if (radius > 0.0 && Math.Abs(radius - _orcaSource.PromoteRadius) > 0.5)
        {
            _field.Remove(_orcaSourceId);
            _orcaSource = new InterestSource(centre, radius, radius * 1.3);
            _orcaSourceId = _field.Register(_orcaSource);
        }
        else
        {
            _orcaSource.Position = centre;
        }
    }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.4 (no crop): the shared crop test every crop-filter
    // site (the ctor's local `In`/`InV`, `Sample()`, `SampleCars()`) funnels through -- always "inside"
    // when `_cropEnabled` is false (RouteGraph/road-net mode), the pinned rectangle test otherwise
    // (Navmesh/demo mode, byte-identical to the pre-Stage-C behaviour).
    private bool InCrop(double x, double y) => !_cropEnabled || (x >= _x0 && x <= _x1 && y >= _y0 && y <= _y1);

    // Deterministic SplitMix64, seeded from LiveCityConfig.CarRngSeed -- identical constants/order to
    // SceneGen.BuildLiveCity's `NextRng`, so two LiveCitySim instances with the same seed spawn the same
    // sequence of cars.
    private uint NextRng()
    {
        _rng += 0x9E3779B97F4A7C15UL;
        var z = _rng;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (uint)(z ^ (z >> 31));
    }

    // #15 per-area realism LOD (docs/LIVE-CITY-15-PER-AREA-LOD-DESIGN.md): a car at (x,y) is LOW realism for
    // lane changing iff it is strictly OUTSIDE the high-realism pocket (distance from the pocket centre >
    // promoteRadius). A non-positive radius disables the gate (all cars high realism). Pure function of
    // position => deterministic, order-independent; unit-tested directly.
    public static bool IsLowRealismLaneChangePos(double x, double y, double pocketX, double pocketY, double promoteRadius)
    {
        if (promoteRadius <= 0.0)
        {
            return false;
        }

        var dx = x - pocketX;
        var dy = y - pocketY;
        return (dx * dx) + (dy * dy) > promoteRadius * promoteRadius;
    }

    // Perf diagnostics (docs/LIVE-CITY-PERF-DESIGN.md P1): opt-in per-phase wall-time accounting for
    // Step(), mirroring Engine.ProfilePhases EXACTLY in shape (a bool gate, a name->ticks dictionary,
    // PhaseStart/PhaseEnd via Stopwatch.GetTimestamp -- see Engine.cs's own ProfilePhases for the
    // pattern this copies). OFF by default and then effectively free: one bool test per phase per
    // step, GetTimestamp is never called, nothing allocates. The setter also forwards to the wrapped
    // Engine so its own phases profile together with this host's; PhaseTicks below merges them in,
    // prefixed "engine." to stay distinguishable. Sim.BenchLiveCity's --profile turns this on and
    // prints the breakdown. Never read by any sim algorithm -> zero behavioral effect, parity-inert
    // (LiveCitySim is never constructed by a golden/parity/bench path).
    public bool ProfilePhases
    {
        get => _profilePhases;
        set
        {
            _profilePhases = value;
            _engine.ProfilePhases = value;
            // B2 (LIVE-CITY-PERF-SESSION-LOG.md): also forward to the ped LOD manager AND the ped
            // demand layer so their sub-phases -- PedLodManager's (rebuildIndex/idsSort/frozenPos/
            // lodDecide/promoteApply/demoteApply/orcaStep/publishSamples/publishHeartbeats) and
            // PedDemand's own (spawnDue/despawnArrivals, the work in PedDemand.Step OUTSIDE the
            // _lodManager.Step call) -- profile alongside this host's. Both are merged in below,
            // prefixed "ped.", as one breakdown OF this host's "pedDemandStep" phase.
            if (_manager is not null)
            {
                _manager.ProfilePhases = value;
            }

            if (_demand is not null)
            {
                _demand.ProfilePhases = value;
            }
        }
    }

    private bool _profilePhases;
    private readonly Dictionary<string, long> _phaseTicks = new();
    // B3 (docs/LIVE-CITY-PERF-SESSION-LOG.md): allocated-bytes counterpart to _phaseTicks, same merge
    // shape (own + "engine." + "ped." prefixes). See Engine.cs's own PhaseBytes for the process-wide-
    // vs-per-thread rationale (Parallel.For phases would be undercounted by a per-thread counter).
    private readonly Dictionary<string, long> _phaseBytes = new();

    // This host's own phases, plus the wrapped Engine's (prefixed "engine.") merged in. When nothing
    // has been profiled (the common case, ProfilePhases off) `_engine.PhaseTicks` is empty and this
    // returns `_phaseTicks` (itself empty) directly -- no allocation. The merge only happens when a
    // caller actually reads this after a profiled run.
    public IReadOnlyDictionary<string, long> PhaseTicks
    {
        get
        {
            var lodTicks = _manager?.PhaseTicks;
            var demandTicks = _demand?.PhaseTicks;
            var hasLod = lodTicks is { Count: > 0 };
            var hasDemand = demandTicks is { Count: > 0 };
            if (_engine.PhaseTicks.Count == 0 && !hasLod && !hasDemand)
            {
                return _phaseTicks;
            }

            var merged = new Dictionary<string, long>(_phaseTicks);
            foreach (var kv in _engine.PhaseTicks)
            {
                merged["engine." + kv.Key] = kv.Value;
            }

            if (hasLod)
            {
                foreach (var kv in lodTicks!)
                {
                    merged["ped." + kv.Key] = kv.Value;
                }
            }

            if (hasDemand)
            {
                foreach (var kv in demandTicks!)
                {
                    merged["ped." + kv.Key] = kv.Value;
                }
            }

            return merged;
        }
    }

    // B3: same merge as PhaseTicks above, for allocated bytes.
    public IReadOnlyDictionary<string, long> PhaseBytes
    {
        get
        {
            var lodBytes = _manager?.PhaseBytes;
            var demandBytes = _demand?.PhaseBytes;
            var hasLod = lodBytes is { Count: > 0 };
            var hasDemand = demandBytes is { Count: > 0 };
            if (_engine.PhaseBytes.Count == 0 && !hasLod && !hasDemand)
            {
                return _phaseBytes;
            }

            var merged = new Dictionary<string, long>(_phaseBytes);
            foreach (var kv in _engine.PhaseBytes)
            {
                merged["engine." + kv.Key] = kv.Value;
            }

            if (hasLod)
            {
                foreach (var kv in lodBytes!)
                {
                    merged["ped." + kv.Key] = kv.Value;
                }
            }

            if (hasDemand)
            {
                foreach (var kv in demandBytes!)
                {
                    merged["ped." + kv.Key] = kv.Value;
                }
            }

            return merged;
        }
    }

    // netstandard2.1 (Unity/Godot) has no GC.GetTotalAllocatedBytes -- degrades to "always 0 bytes"
    // there, same rationale as Engine.cs's TotalAllocatedBytes.
#if NET8_0_OR_GREATER
    private static long TotalAllocatedBytes() => GC.GetTotalAllocatedBytes(precise: false);
#else
    private static long TotalAllocatedBytes() => 0L;
#endif

    private readonly struct PhaseSample
    {
        public readonly long Ticks;
        public readonly long Bytes;
        public PhaseSample(long ticks, long bytes) { Ticks = ticks; Bytes = bytes; }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private PhaseSample PhaseStart() => _profilePhases
        ? new PhaseSample(System.Diagnostics.Stopwatch.GetTimestamp(), TotalAllocatedBytes())
        : default;

    private void PhaseEnd(string name, PhaseSample start)
    {
        if (!_profilePhases)
        {
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start.Ticks;
        _phaseTicks.TryGetValue(name, out var acc);
        _phaseTicks[name] = acc + elapsed;

        var elapsedBytes = TotalAllocatedBytes() - start.Bytes;
        _phaseBytes.TryGetValue(name, out var accBytes);
        _phaseBytes[name] = accBytes + elapsedBytes;
    }

    // Advances the coupled sim by one tick (Dt seconds, per LiveCityConfig.Dt), then publishes the
    // resulting frame onto both wires. Reproduces SceneGen.BuildLiveCity's per-tick order exactly:
    // (a) spawn cars up to the cap on crop drivable edges -> (b) step the ped demand -> (c) gather this
    // tick's WALKING low-power ped positions -> (d) refresh the crossing-occupancy gate -> (e) step the
    // engine (which queries the now-current CrowdSource).
    // docs/LIVE-CITY-THREADED-TICK-DESIGN.md §4 hazard 3 / §5 "render -> sim writes become messages".
    //
    // A host that runs `Step()` on its own producer thread must NOT let its render/UI thread call
    // `SetLcRealismZone` / `SetCarDensity` / `SetPedDensity` / `Dt=` directly: those mutate live sim state
    // (`SetLcRealismZone` rebuilds the ORCA interest source; `SetPedDensity` pokes `PedDemand`), so a
    // concurrent call lands mid-step. The `Request*` methods below instead park the value in a single slot
    // -- LAST WRITER WINS, which is the right semantics for a UI dial or a camera-driven zone -- and the
    // producer applies it at a defined point: the very top of the next `Step()`, before any spawn logic.
    //
    // Deliberately a plain `lock` rather than an interlocked/lock-free dance: it is taken at most once per
    // rendered frame and once per step, always uncontended in practice, and it costs ~20 ns against a
    // ~100 ms step. There is nothing to win here and correctness is obvious.
    //
    // Single-threaded callers (every test, every bench, the parity path -- which never constructs this
    // host at all) keep using the `Set*` methods unchanged, so nothing existing changes behaviour.
    private readonly object _requestLock = new();
    private bool _reqZone;
    private double _reqZoneX, _reqZoneY, _reqZoneR;
    private bool _reqCars;
    private int _reqCarTarget;
    private int? _reqCarPerStep;
    private bool _reqPeds;
    private int _reqPedCap;
    private double _reqPedRate;
    private bool _reqDt;
    private double _reqDtValue;

    public void RequestLcRealismZone(double centreX, double centreY, double radius)
    {
        lock (_requestLock)
        {
            _reqZone = true;
            _reqZoneX = centreX;
            _reqZoneY = centreY;
            _reqZoneR = radius;
        }
    }

    public void RequestCarDensity(int targetConcurrent, int? spawnPerStep = null)
    {
        lock (_requestLock)
        {
            _reqCars = true;
            _reqCarTarget = targetConcurrent;
            _reqCarPerStep = spawnPerStep;
        }
    }

    public void RequestPedDensity(int populationCap, double spawnRatePerSecond)
    {
        lock (_requestLock)
        {
            _reqPeds = true;
            _reqPedCap = populationCap;
            _reqPedRate = spawnRatePerSecond;
        }
    }

    public void RequestDt(double dt)
    {
        lock (_requestLock)
        {
            _reqDt = true;
            _reqDtValue = dt;
        }
    }

    // Drain the request slots and apply them on THIS thread (the producer's). Copies out under the lock and
    // applies outside it, so a `Set*` call -- which can rebuild the interest source -- never runs while a
    // requester is blocked.
    private void ApplyPendingRequests()
    {
        bool zone, cars, peds, dtSet;
        double zx, zy, zr, rate, dt;
        int carTarget, pedCap;
        int? carPerStep;

        lock (_requestLock)
        {
            zone = _reqZone; zx = _reqZoneX; zy = _reqZoneY; zr = _reqZoneR;
            cars = _reqCars; carTarget = _reqCarTarget; carPerStep = _reqCarPerStep;
            peds = _reqPeds; pedCap = _reqPedCap; rate = _reqPedRate;
            dtSet = _reqDt; dt = _reqDtValue;
            _reqZone = _reqCars = _reqPeds = _reqDt = false;
        }

        if (dtSet) Dt = dt;
        if (cars) SetCarDensity(carTarget, carPerStep);
        if (peds) SetPedDensity(pedCap, rate);
        if (zone) SetLcRealismZone(zx, zy, zr);
    }

    public void Step()
    {
        // §5: apply any render-thread requests at this fixed point -- BEFORE `dt` is read, so a requested
        // tick-rate change takes effect on the step that observes it rather than the one after.
        ApplyPendingRequests();

        var dt = _cfg.Dt;

        // D1: the ped-density knobs are read off the by-reference `_cfg` every tick, exactly as the car
        // knobs below already are. One fixed point, before any spawn logic, so a mid-run config change is
        // felt by this tick's ped spawn pass rather than the next one.
        var tMirror = PhaseStart();
        MirrorPedDensity();
        PhaseEnd("pedDensityMirror", tMirror);

        // (a) spawn cars up to the cap on crop drivable edges.
        var tSpawn = PhaseStart();
        if (_cropEdges.Count >= 2)
        {
            var live = _engine.VehicleHandles.Length;

            // A3 (design §1b): how many insertions to ATTEMPT this step.
            //   closed-loop (default) -- up to CarSpawnPerStep, and only while below the occupancy cap. The
            //                            cap makes inflow a function of our own drain, which is why this
            //                            mode cannot measure discharge.
            //   open-loop             -- a fixed rate, occupancy IGNORED, paced by a fractional-credit
            //                            accumulator so any real-valued rate is expressible. Queue growth is
            //                            then free to run away, which is the whole measurement.
            var attempts = _cfg.CarSpawnPerStep;
            if (_cfg.CarInflowVehPerSec is { } inflow)
            {
                _openLoopSpawnCredit += inflow * dt;
                attempts = (int)Math.Floor(_openLoopSpawnCredit);
                _openLoopSpawnCredit -= attempts;
            }

            for (var s = 0; s < attempts
                 && (_cfg.CarInflowVehPerSec is not null || live < _cfg.CarTargetConcurrent); s++)
            {
                var (fromId, _) = _cropEdges[(int)(NextRng() % (uint)_cropEdges.Count)];
                var (toId, _) = _cropEdges[(int)(NextRng() % (uint)_cropEdges.Count)];
                if (fromId == toId) continue;
                try
                {
                    _engine.SpawnVehicle(_vtype, fromId, toId, departPos: 5.0, departSpeed: 0.0, departBestLane: true);
                    SpawnLog?.Add((_now, fromId, toId));
                    live++;

                    // B1 (docs/DENSITY-DIFF-HARNESS-DESIGN.md §2, -TASKS.md): record-at-spawn -- fires
                    // only when a sink was supplied. Recomputes the SAME from/to route via the
                    // dedicated `_demandRouter` (see its field remark) so the recorded edges are
                    // exactly what Engine just inserted. Recorded HERE (spawn-call success), not at
                    // actual physical/lane insertion -- design §2 caveat 2: a vehicle Engine later
                    // refuses to admit onto the road (InsertionFollowerGapCheck) still appears here,
                    // which is the intended "record-at-spawn" contract, not a bug.
                    if (_recordDemandSink is not null && _demandRouter is not null)
                    {
                        var routeEdges = _demandRouter.Route(fromId, toId);
                        if (routeEdges is not null && routeEdges.Count > 0)
                        {
                            _recordVehCounter++;
                            _recordDemandSink.RecordVehicle(
                                "rec" + _recordVehCounter.ToString(CultureInfo.InvariantCulture),
                                _now,
                                "best", // departBestLane: true above -- SUMO's departLane="best".
                                departPos: 5.0,
                                departSpeed: 0.0,
                                RecordVTypeId,
                                routeEdges);
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        PhaseEnd("carSpawn", tSpawn);

        // (b) step the ped demand; capture the wire-event cursor first so the batch published this tick
        // includes exactly what this Step call emits (mirrors PedSimSource.Tick). Skipped when
        // PedestriansEnabled==false -- no demand was built, `_demand` is null (docs/LIVE-CITY-
        // ARBITRARY-NET-DESIGN.md §6): cars run alone and `_pedPublisher` simply never sees an event.
        // NOTE (LIVE-CITY-PERF-DESIGN.md P1): this one call is where ped steering, LOD promote/demote,
        // and InterestField queries all happen (PedDemand.Step -> PedLodManager.Step, in
        // src/Sim.Pedestrians -- out of this task's file scope), so "pedDemandStep" below is reported
        // as ONE fused phase; it is not separable at this seam without touching Sim.Pedestrians.
        var beforeCount = _pedPublisher.Events.Count;
        var tPedDemand = PhaseStart();
        _demand?.Step(_now, dt, _field, NoEntities);
        PhaseEnd("pedDemandStep", tPedDemand);
        var tNext = _now + dt;

        // (c) gather this tick's WALKING low-power ped positions (empty when peds are disabled).
        var tGather = PhaseStart();
        _movingLowPowerPositions.Clear();
        if (_demand is not null && _manager is not null)
        {
            foreach (var id in _demand.LiveIds)
            {
                if (_manager.ModelOf(id) == PedDrModel.FreeKinematic)
                {
                    continue;
                }

                // PERF (TASK 2, docs/LIVE-CITY-PERF-SESSION-LOG.md): PedDemand.DespawnArrivals, called
                // moments ago inside `_demand.Step(...)` above, already computed this exact (id, tNext)
                // pose while checking arrivals -- reuse it instead of re-invoking
                // AnimTagOf(id, tNext)+PositionOf(id, tNext) (two more ActivityTimeline.PoseAt calls).
                // Falls back to the direct calls, byte-identical to the pre-existing behaviour, if the
                // cache doesn't have this id/time for any reason (e.g. a future caller of this loop
                // outside the normal Step() sequence).
                if (!_demand.TryGetLastPose(id, tNext, out var animTag, out var pos))
                {
                    animTag = _manager.AnimTagOf(id, tNext);
                    pos = _manager.PositionOf(id, tNext);
                }

                if (animTag == ActivityTimeline.WalkAnimTag)
                {
                    _movingLowPowerPositions.Add(pos);
                }
            }
        }

        PhaseEnd("pedLowPowerGather", tGather);

        // (d) refresh the crossing-occupancy gate from the current walking peds. Skipped when
        // CrossingsEnabled==false (no crossings, or peds disabled entirely) -- `_crossingOccupancy` is
        // null; `OccupiedCrossings` reads 0 and `PeakOccupiedCrossings` never advances.
        var tCrossing = PhaseStart();
        if (_crossingOccupancy is not null)
        {
            _crossingOccupancy.Update(_movingLowPowerPositions);
            if (_crossingOccupancy.OccupiedCount > PeakOccupiedCrossings) PeakOccupiedCrossings = _crossingOccupancy.OccupiedCount;
        }

        PhaseEnd("crossingOccupancy", tCrossing);

        // (d2) #15 per-area realism LOD (docs/LIVE-CITY-15-PER-AREA-LOD-DESIGN.md): classify each live car's
        // lane-change realism from its PREVIOUS-step position vs the static high-realism pocket, BEFORE the
        // engine steps. Only under cooperative LC (otherwise the global cheap-swap path already applies to
        // all). A car inside the pocket cooperates (no pure-lateral float, into-occupied vetoes on); a car
        // outside takes the cheap flow-preserving swap (float permitted -- distant/unobserved). Cars not yet
        // in the previous snapshot (spawned this step) stay high-realism (cooperative) by default. Pure
        // function of the frozen previous snapshot + the static pocket => deterministic, order-independent.
        // Never runs on a golden (parity/bench drive Engine directly, not LiveCitySim) => flag stays false.
        var tLcLod = PhaseStart();
        if (_cfg.CooperativeLaneChange && _lcZoneR > 0.0)
        {
            for (var i = 0; i < _lastSnapshot.Count; i++)
            {
                var low = IsLowRealismLaneChangePos(
                    _lastSnapshot.PosX[i], _lastSnapshot.PosY[i],
                    _lcZoneX, _lcZoneY, _lcZoneR);
                _engine.SetLowRealismLaneChange(_lastSnapshot.Handles[i], low);
            }
        }

        PhaseEnd("lcRealismLod", tLcLod);

        // (e) step the engine -- its CrowdSource query now sees the current gates + promoted peds.
        var tEngine = PhaseStart();
        _engine.Step();
        PhaseEnd("engineStep", tEngine);
        _now = tNext;

        // Tally trip completions this step (Engine.Events is fresh each Step) -- the #15 arrival signal.
        var tArrival = PhaseStart();
        foreach (var ev in _engine.Events)
        {
            if (ev.Kind == SimEventKind.Arrived) ArrivedTotal++;
        }

        if (_engine.VehicleHandles.Length > PeakCars) PeakCars = _engine.VehicleHandles.Length;
        if (_demand is not null && _demand.LiveCount > PeakPeds) PeakPeds = _demand.LiveCount;
        PhaseEnd("arrivalAndPeakTally", tArrival);

        // Car-yield metric: for each occupied crossing disc, count it once if any car within 10 m has
        // Speed < 2.0 m/s -- a car braking beside a ped-occupied crossing.
        var tYield = PhaseStart();
        CarYieldObservations += CountYieldObservationsThisStep();
        PhaseEnd("carYieldMetric", tYield);

        // ---- publish: capture the engine snapshot, then publish both wires ----
        var tSnapshot = PhaseStart();
        var snap = SimulationSnapshot.Capture(_engine);
        _lastSnapshot = snap;
        PhaseEnd("snapshotCapture", tSnapshot);

        var tPublishCars = PhaseStart();
        if (!_vehGeometryPublished)
        {
            _vehPublisher.PublishGeometryOnce(Network, _vehBus.Sink);
            _vehGeometryPublished = true;
        }

        _vehPublisher.PublishStep(snap, _vehBus.Sink);
        _vehBus.Source.Pump();

        // Stage C (C1) tee: also publish this step onto the record sink, if one was supplied -- geometry
        // once (its own publish-once latch, independent of `_vehGeometryPublished` above), then the frame,
        // through the DEDICATED `_recordPublisher` (see its field comment for why it must not be shared).
        if (_recordVehSink is not null && _recordPublisher is not null)
        {
            if (!_recordGeometryPublished)
            {
                _recordPublisher.PublishGeometryOnce(Network, _recordVehSink);
                _recordGeometryPublished = true;
            }

            _recordPublisher.PublishStep(snap, _recordVehSink);
        }

        PhaseEnd("publishCars", tPublishCars);

        var tPublishPeds = PhaseStart();

        // docs/LIVE-CITY-THREADED-TICK-DESIGN.md §6 Stage 3 (+ log item A6). This block used to allocate a
        // FRESH `List<PedEvent>` every step and leave `_pedPublisher.Events` growing without bound -- one
        // list plus one heap record per published ped per step, which at 20 000 peds is the dominant
        // remaining per-tick allocation and, over a long session, an unbounded leak.
        //
        // Now: drain this step's tail into a REUSED list, forward it, then drop the history we just took.
        // `beforeCount` above is therefore 0 on every subsequent step, which is exactly right -- the
        // publisher holds only the current step's batch. The per-id send counters are unaffected, so every
        // POC-3 counter assertion still reads the same numbers.
        _pedPublisher.DrainInto(beforeCount, _pedEventBatch);

        _pedWirePublisher.Publish(_pedEventBatch);

        // Stage E (E3) tee: also publish this tick's ped event batch through the DEDICATED
        // `_recordPedPublisher`, if a ped record/DDS sink was supplied -- mirrors the car tee just above.
        _recordPedPublisher?.Publish(_pedEventBatch);

        // Both consumers have taken the batch, so the history is dead weight from here.
        LastPedEventBatchCount = _pedEventBatch.Count;
        _pedPublisher.ClearEvents();
        PhaseEnd("publishPeds", tPublishPeds);
    }

    // Stage 3: the reused per-step ped-event batch (see the publishPeds block). Grows once to the peak
    // batch size and is then allocation-free.
    private readonly List<PedEvent> _pedEventBatch = new();

    // Stage 3 diagnostics (docs/LIVE-CITY-THREADED-TICK-DESIGN.md §6). Read-only, host-side, never mutate
    // anything -- these exist so the bounded-history and pooled-buffer claims are ASSERTABLE rather than
    // asserted in prose. `PedEventHistoryCount` should be a per-step batch size (it was a running total
    // before A6); `PedBusBuffersAllocated` should stop climbing once the crowd stops growing.
    public int PedEventHistoryCount => _pedPublisher.Events.Count;

    // How many events the LAST step actually handed to the wire. Paired with `PedEventHistoryCount` this is
    // what makes the bounded-history claim falsifiable: the history must be 0 after a step (drained), AND
    // this must be non-zero (so the drain was not vacuously empty -- peds really were being published).
    public int LastPedEventBatchCount { get; private set; }
    public int PedBusBuffersAllocated => _pedBus.BuffersAllocated;
    public int PedBusPendingEntries => _pedBus.PendingEntries;

    private readonly WorldDisc[] _gateProbeScratch = new WorldDisc[4];

    // Increment once per occupied crossing disc that has at least one car within 10 m braking (Speed <
    // 2.0 m/s) beside it -- the "car stopped for a ped on a crosswalk" proxy. A ped's own moving-low-power
    // position is confirmed to actually BE an occupied-crossing gate disc via crossingOccupancy's public
    // QueryNear (a tiny-radius self-query returns >=1 iff that exact point was gated this tick), so this
    // never double-counts a walking ped that is merely near -- not on -- a crossing.
    private int CountYieldObservationsThisStep()
    {
        if (_crossingOccupancy is null || _crossingOccupancy.OccupiedCount == 0) return 0;

        var count = 0;
        var cpx = _engine.PosX;
        var cpy = _engine.PosY;
        var speed = _engine.Speed;
        var carN = cpx.Length;

        // With zero cars the inner loop below can never find a `near` car, so the result is provably 0
        // either way -- skip the O(on-crossing peds) QueryNear scan entirely. Byte-identical: the counter
        // only ever increments inside `if (near) count++`, which requires carN > 0 to reach `near = true`.
        if (carN == 0) return 0;

        foreach (var p in _movingLowPowerPositions)
        {
            // Is this exact ped position an occupied-crossing gate disc (i.e. is the ped ON a crossing)?
            var onCrossing = _crossingOccupancy.QueryNear(p.X, p.Y, 0.01, _gateProbeScratch) > 0;
            if (!onCrossing) continue;

            var near = false;
            for (var i = 0; i < carN; i++)
            {
                var dx = cpx[i] - p.X;
                var dy = cpy[i] - p.Y;
                if ((dx * dx) + (dy * dy) > 100.0) continue; // 10 m radius
                if (speed[i] < 2.0) { near = true; break; }
            }

            if (near) count++;
        }

        return count;
    }

    // Cars-only readback into a REUSED buffer, for callers that need just the vehicle Handle->Name/pose
    // table every frame (e.g. the viewer's click-select name map) and must NOT pay to materialise the whole
    // ped crowd. Sample() below builds a fresh cars+peds snapshot each call -- at a large LIVECITY_PEDS that
    // per-frame ped-list allocation is the dominant GC pressure (measured), so this avoids it entirely.
    private readonly List<LiveCityCar> _carSampleScratch = new();
    public IReadOnlyList<LiveCityCar> SampleCars()
    {
        _carSampleScratch.Clear();
        for (var i = 0; i < _lastSnapshot.Count; i++)
        {
            var x = _lastSnapshot.PosX[i];
            var y = _lastSnapshot.PosY[i];
            if (!InCrop(x, y)) continue;
            _carSampleScratch.Add(new LiveCityCar(
                _lastSnapshot.Handles[i], x, y, _lastSnapshot.PosZ[i], _lastSnapshot.Angle[i],
                _lastSnapshot.Length[i], _lastSnapshot.Width[i], _lastSnapshot.VehicleId[i]));
        }

        return _carSampleScratch;
    }

    // docs/LIVE-CITY-PED-CROSSING-SIGNALS-DESIGN.md T1: passthrough to Engine.SampleControlledCrossingSignals()
    // for the viewer's mini pedestrian-crossing signal heads. Read-only; the returned list is the engine's
    // own reused buffer (valid until the next call).
    public IReadOnlyList<(int LaneHandle, char State)> SampleCrossingSignals() => _engine.SampleControlledCrossingSignals();

    // issue #15 residual chase (docs/LIVE-CITY-15-RESIDUAL-REPRO.md): an ENGINE-AUTHORITATIVE per-vehicle
    // witness for confirming the turn-lane-segregation hypothesis -- LiveCityCar carries no lane/pos/posLat/
    // speed/TL, so this reaches straight into the live Engine's read columns. Diagnostic accessor only
    // (host-side, read-only, never mutates the engine -> parity-untouched); the smoke witness gates it on
    // an env flag so normal runs pay nothing. GapAhead = longitudinal distance to the nearest same-lane
    // car ahead (PositiveInfinity if none); Tl = the controlling TL link's state char for the car's lane
    // ('\0' if the lane is not TL-controlled).
    // Tl = the "any-green wins" summary char for the lane; TlLinks = the DISTINCT states of every TL link
    // controlling this lane (e.g. "Gr" == one movement green, another red -> a car held by its own red
    // turn-arrow under a lane that reads green for a different movement). NextMouthGap = pos of the nearest
    // car on this car's NEXT lane (across the junction) measured from that lane's start (+inf if the exit
    // lane is empty or unknown) -- a small value means the junction EXIT is occupied at its mouth, so the
    // car holds even though its OWN lane is clear ahead (keep-clear / cross-junction car-following, which
    // the same-lane GapAhead cannot see).
    // TlWire = the state char the VIEWER actually renders for this car's lane, read from the published
    // wire (VehicleSource.TlStateByLane) rather than the engine -- so `Tl != TlWire` means the rendered
    // signal head disagrees with the engine's authoritative phase (a "stopped under a green-rendered
    // head while the engine has it red" render bug).
    public readonly record struct CarAuthWitness(
        VehicleHandle Handle, string LaneId, double Pos, double PosLat, double Speed, char Tl, double GapAhead,
        string TlLinks, double NextMouthGap, char TlWire, byte Binder, byte JyArm, float JyFoeSpeed);

    public IReadOnlyList<CarAuthWitness> WitnessAuthoritative()
    {
        var handles = _engine.VehicleHandles;
        var laneH = _engine.LaneHandles;
        var laneIds = _engine.LaneIds;
        var pos = _engine.Pos;
        var posLat = _engine.PosLat;
        var speed = _engine.Speed;
        var tlLaneH = _engine.TlLaneHandles;
        var tlStates = _engine.TlStates;
        var nextLaneH = _engine.NextLaneHandles;
        var binders = _engine.BindingConstraints;   // which speed constraint bound each car
        var jyArms = _engine.JunctionYieldArms;      // which junction-yield arm bound (+0x80 priority)
        var jyFoeSpd = _engine.JunctionYieldFoeSpeeds; // bound junction foe's speed (-1 none)
        var wireTl = _vehBus.Source.TlStateByLane; // what the viewer renders
        var n = handles.Length;

        var outList = new List<CarAuthWitness>(n);
        for (var i = 0; i < n; i++)
        {
            // GapAhead: nearest same-lane car with a greater longitudinal pos.
            var gap = double.PositiveInfinity;
            for (var j = 0; j < n; j++)
            {
                if (j == i || laneH[j] != laneH[i]) continue;
                var d = pos[j] - pos[i];
                if (d > 0.0 && d < gap) gap = d;
            }

            // TL for the car's lane: `tl` = any-green-wins summary; `tlLinks` = the distinct states of
            // every link controlling this lane (so a car held by its OWN movement's red under a lane that
            // is green for another movement is visible as e.g. "Gr").
            var tl = '\0';
            var links = string.Empty;
            for (var k = 0; k < tlLaneH.Length; k++)
            {
                if (tlLaneH[k] != laneH[i]) continue;
                var c = (char)tlStates[k];
                if (links.IndexOf(c) < 0) links += c;
                if ((c is 'G' or 'g') && tl is not ('G' or 'g')) tl = c;
                else if (tl == '\0') tl = c;
            }

            // NextMouthGap: nearest car on the car's NEXT lane (across the junction), measured from that
            // lane's start -- a small value => the exit is occupied at its mouth (keep-clear / cross-
            // junction leader), which the same-lane GapAhead misses.
            var nextMouthGap = double.PositiveInfinity;
            var nl = i < nextLaneH.Length ? nextLaneH[i] : -1;
            if (nl >= 0)
            {
                for (var j = 0; j < n; j++)
                {
                    if (laneH[j] != nl) continue;
                    if (pos[j] < nextMouthGap) nextMouthGap = pos[j];
                }
            }

            var tlWire = wireTl.TryGetValue(laneH[i], out var wb) ? (char)wb : '\0';

            outList.Add(new CarAuthWitness(
                handles[i], i < laneIds.Length ? laneIds[i] : string.Empty,
                pos[i], posLat[i], speed[i], tl, gap, links, nextMouthGap, tlWire,
                i < binders.Length ? binders[i] : (byte)0,
                i < jyArms.Length ? jyArms[i] : (byte)0,
                i < jyFoeSpd.Length ? jyFoeSpd[i] : -1f));
        }

        return outList;
    }

    // Reads back one frame of the coupled scene: cars from the last captured snapshot (crop-filtered),
    // peds from the demand's live ids (crop-filtered), and the crossing-occupancy peak.
    public LiveCitySnapshot Sample()
    {
        var cars = new List<LiveCityCar>(_lastSnapshot.Count);
        for (var i = 0; i < _lastSnapshot.Count; i++)
        {
            var x = _lastSnapshot.PosX[i];
            var y = _lastSnapshot.PosY[i];
            if (!InCrop(x, y)) continue;
            cars.Add(new LiveCityCar(
                _lastSnapshot.Handles[i], x, y, _lastSnapshot.PosZ[i], _lastSnapshot.Angle[i],
                _lastSnapshot.Length[i], _lastSnapshot.Width[i], _lastSnapshot.VehicleId[i]));
        }

        // Empty when PedestriansEnabled==false -- `_demand`/`_manager` are null and there is nothing to
        // sample (docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §6).
        var peds = new List<LiveCityPed>(_demand?.LiveCount ?? 0);
        if (_demand is not null && _manager is not null)
        {
            foreach (var id in _demand.LiveIds)
            {
                var p = _manager.PositionOf(id, _now);
                if (!InCrop(p.X, p.Y)) continue;
                var model = _manager.ModelOf(id);
                var animTag = _manager.AnimTagOf(id, _now);
                var regime = model == PedDrModel.FreeKinematic ? PedRegime.HighPower
                    : animTag == ActivityTimeline.WalkAnimTag ? PedRegime.LowPowerWalking
                    : PedRegime.Paused;
                // C3 (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.5a): the ped's real surface elevation,
                // interpolated along the path it is walking. Exactly 0.0 on a 2-D net -- the demo and
                // every committed 2-D scenario -- so their snapshots stay bit-identical.
                peds.Add(new LiveCityPed(id, p.X, p.Y, _manager.ElevationOf(id, _now), regime, animTag));
            }
        }

        return new LiveCitySnapshot(cars, peds, _crossingOccupancy?.OccupiedCount ?? 0);
    }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.4, -TASKS.md C1: road-net mode's realism-pocket/
    // LC-zone default centre -- the AABB (over every parsed lane shape, every edge, vehicle or
    // pedestrian) of the WHOLE net, since road-net mode has no crop to centre on instead. Falls back
    // to the origin only for a pathological net with no lane geometry at all (never happens for a
    // real net.xml; defensive, not exercised by the committed fixture).
    // TRI-STATE env override for an engine gate: unset => keep the engine's OWN default; "1" => on;
    // anything else (in practice "0") => off.
    //
    // WHY NOT `GetEnvironmentVariable(name) == "1"`, which is what every one of these lines used to be:
    // that form is a two-state override that silently FORCES OFF whenever the variable is absent. It was
    // harmless while every gate defaulted to false and became a live bug the moment the defaults flipped to
    // true -- the demo would have run with all seven gates disabled while the engine, the goldens and every
    // other host had them enabled, and the resulting "the demo still gridlocks" report would have looked
    // like a failed fix rather than a wiring mistake. The A/B diagnostics are unaffected because they set
    // every gate EXPLICITLY to "1"/"0" (see AllLiveCityGateVars), which both forms honour identically.
    private static bool EnvGate(string name, bool engineDefault)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? engineDefault : v == "1";
    }

    private static Vec2 ComputeNetAabbCentre(NetworkModel model)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var edge in model.Edges)
        {
            foreach (var lane in edge.Lanes)
            {
                foreach (var p in lane.Shape)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }
        }

        if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY))
        {
            return Vec2.Zero;
        }

        return new Vec2((minX + maxX) / 2.0, (minY + maxY) / 2.0);
    }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.5, -TASKS.md C3: whole-net O/D sampling for road-net
    // mode -- one point (the shape midpoint) per sidewalk lane centreline, no crop, ordered by Id
    // (ordinal) so construction is fully deterministic: two `LiveCitySim`s built from the SAME
    // `PedNetwork` sample the IDENTICAL O/D set. Capped at `maxEndpoints` via the SAME deterministic
    // seeded-stride convention the Navmesh branch's own endpoint cap uses above -- no `System.Random`
    // anywhere.
    private static List<Vec2> SampleSidewalkCentrelineEndpoints(IReadOnlyList<PedLane> sidewalks, int maxEndpoints = 90)
    {
        var ordered = new List<PedLane>(sidewalks);
        ordered.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        var allPoints = new List<Vec2>(ordered.Count);
        foreach (var lane in ordered)
        {
            var shape = lane.Shape;
            if (shape.Count == 0) continue;
            allPoints.Add(shape[shape.Count / 2]);
        }

        var odPoints = new List<Vec2>();
        if (allPoints.Count <= maxEndpoints)
        {
            odPoints.AddRange(allPoints);
        }
        else
        {
            var stride = (double)allPoints.Count / maxEndpoints;
            for (var k = 0; k < maxEndpoints; k++) odPoints.Add(allPoints[(int)(k * stride)]);
        }

        return odPoints;
    }

    // docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §5.6, -TASKS.md A2: fallback spawn-edge source for a
    // dataset with no (or an empty) scenario.rou.xml -- every edge in the parsed net with at least one
    // lane a road vehicle may use (`Lane.AllowsRoadVehicle`), excluding internal (":"-prefixed)
    // junction-interior edges. Iterates `model.Edges` (not `EdgesById`) so the result order is the
    // deterministic net.xml parse order, not a dictionary's unspecified enumeration order.
    private static IReadOnlyList<string> DeriveDrivableEdgesFromNetwork(NetworkModel model)
    {
        var edges = new List<string>();
        foreach (var edge in model.Edges)
        {
            if (edge.Id.StartsWith(":", StringComparison.Ordinal)) continue;

            var hasVehicleLane = false;
            foreach (var lane in edge.Lanes)
            {
                if (lane.AllowsRoadVehicle) { hasVehicleLane = true; break; }
            }

            if (hasVehicleLane) edges.Add(edge.Id);
        }

        return edges;
    }

    // Read the union of drivable edge ids from the committed car route file(s) -- every `edges="..."`
    // token, in first-seen order. Copied from SceneGen.ReadDrivableEdges and then (docs/EXTERNAL-NET-
    // LOADING-DESIGN.md §1) generalised from one path to a LIST, because a `.sumocfg`'s `<route-files>`
    // routinely names several files and the real demand is not the first of them. A listed file that
    // does not exist, or that contains no `edges="..."` at all (a vType file), simply contributes
    // nothing. With a single existing path the result is identical to the pre-list form, token for
    // token and in the same order -- the demo's edge set is unchanged.
    private static IReadOnlyList<string> ReadDrivableEdges(IReadOnlyList<string> rouPaths)
    {
        var edges = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rouPath in rouPaths)
        {
            if (string.IsNullOrEmpty(rouPath) || !File.Exists(rouPath)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(rouPath), "edges=\"([^\"]*)\""))
            {
                foreach (var tok in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (seen.Add(tok)) edges.Add(tok);
                }
            }
        }

        return edges;
    }

    public void Dispose()
    {
        _vehBus.Sink.Dispose();
        _vehBus.Source.Dispose();
        _pedBus.Sink.Dispose();
        _pedBus.Source.Dispose();
    }
}
