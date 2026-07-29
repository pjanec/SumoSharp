// Sim.MeasureWriteRate -- how many vehicle updates does the DECIMATED replication stream actually need
// per second, and why?
//
// WHAT THIS ANSWERS. Every consumer of the vehicle wire (the Godot DDS viewer, a Spectacle polyline, a
// future IG-native port) reads the same stream produced by `DrErrorPublishPolicy`. The write COUNT is a
// property of the SCENARIO, not of the consumer: porting the render side does not make a car change
// acceleration or lane any less often. So before investing in a consumer-side port, measure the demand.
//
//   fires/car/s low  -> the count is light, the wall is consumer-side work, a port can help.
//   fires/car/s high -> the publish thresholds are too tight for this net; loosening PosTol/LatTol/
//                       MaxInterval is far cheaper than any port, and helps every consumer at once.
//
// The per-reason split is what distinguishes those two, which is why DrErrorPublishPolicy carries
// attribution counters.
//
// WHY A SEPARATE TOOL rather than a flag on Sim.BenchLiveCity: that bench is the coupled cars+peds PERF
// instrument and its output is a timing/allocation report. This asks a different question with a different
// report, and CLAUDE.md measurement-discipline #8 says commit the instrument -- a probe that is run once
// and reverted makes its own number unfalsifiable and poisons every later comparison.
//
// Usage:
//   dotnet run -c Release --project src/Sim.MeasureWriteRate -- --dataset <dir>   --cars N [...]
//   dotnet run -c Release --project src/Sim.MeasureWriteRate -- --sumocfg <path>  --cars N [...]
//
//   --cars N        target concurrent cars (default 500).  --peds N   ped cap (default 0 = vehicles only)
//   --steps N       MEASURED steps after warm-up (default 240).  --hz N   sim rate (default = config's)
//   --warmup N      max steps to reach the target before measuring (default 2000)
//   --csv <path>    append one row per run
//   --quiet         suppress the per-interval progress lines
using System.Globalization;
using Sim.Core;
using Sim.LiveCity;
using Sim.Replication;

var inv = CultureInfo.InvariantCulture;

string? sumocfgPath = null;
string? datasetDir = null;
var targetCars = 500;
var targetPeds = 0;
var measureSteps = 240;
var warmupCap = 2000;
double? hz = null;
string? csvPath = null;
var quiet = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sumocfg" when i + 1 < args.Length: sumocfgPath = args[++i]; break;
        case "--dataset" when i + 1 < args.Length: datasetDir = args[++i]; break;
        case "--cars" when i + 1 < args.Length: targetCars = int.Parse(args[++i], inv); break;
        case "--peds" when i + 1 < args.Length: targetPeds = int.Parse(args[++i], inv); break;
        case "--steps" when i + 1 < args.Length: measureSteps = int.Parse(args[++i], inv); break;
        case "--warmup" when i + 1 < args.Length: warmupCap = int.Parse(args[++i], inv); break;
        case "--hz" when i + 1 < args.Length: hz = double.Parse(args[++i], inv); break;
        case "--csv" when i + 1 < args.Length: csvPath = args[++i]; break;
        case "--quiet": quiet = true; break;
        case "-h":
        case "--help":
            Console.WriteLine(
                "usage: --dataset <dir> | --sumocfg <path>  [--cars N] [--peds N] [--steps N]\n"
                + "       [--warmup N] [--hz N] [--csv <path>] [--quiet]\n\n"
                + "Measures the decimated vehicle write rate (DrErrorPublishPolicy) under LiveCity demand:\n"
                + "fires/s, fires/car/s, the per-reason split, per-car inter-update interval, bytes/s, and\n"
                + "the producer real-time factor.");
            return 0;
        default:
            Console.Error.WriteLine($"error: unknown or incomplete argument '{args[i]}' (try --help).");
            return 2;
    }
}

if (sumocfgPath is null && datasetDir is null)
{
    Console.Error.WriteLine("error: one of --sumocfg <path> or --dataset <dir> is required (try --help).");
    return 2;
}

var cfg = sumocfgPath is not null
    ? LiveCityConfig.ForSumocfg(sumocfgPath)
    : LiveCityConfig.ForDataset(datasetDir!);

// Set the targets on the CONFIG rather than calling SetCarDensity/RequestCarDensity afterwards: the config
// path has no request/apply timing to reason about, so "did the target take effect?" is not a question.
cfg.CarTargetConcurrent = targetCars;
cfg.PedPopulationCap = targetPeds;
if (targetPeds == 0)
{
    cfg.PedSpawnRatePerSecond = 0.0;
}

if (hz is not null)
{
    cfg.Dt = 1.0 / hz.Value;
}

// Fill the net quickly -- this study is about the STEADY-STATE rate, so time spent ramping is pure cost.
// Scaled to the target so a 4000-car run does not spend thousands of steps filling.
cfg.CarSpawnPerStep = Math.Max(5, targetCars / 25);

// ---------------------------------------------------------------------------------------------------
// The sink. Counting `records.Length` per PublishFrame gives the total decimated fires with no change to
// the policy; keying last-fire sim time by Handle gives the per-car inter-update interval, which is the
// number that says whether a car is being sent at heartbeat pace or far above it.
// ---------------------------------------------------------------------------------------------------
// The sink needs lane -> edge identity to tell a REAL lateral lane change (both lanes on the same edge)
// from a car simply driving onto the next lane of its route. Built once from the parsed net.
var netModel = Sim.Ingest.NetworkParser.Parse(cfg.ResolveNetPath());
var laneEdge = new string[netModel.LanesByHandle.Count];
for (var i = 0; i < netModel.LanesByHandle.Count; i++)
{
    laneEdge[i] = netModel.LanesByHandle[i].EdgeId;
}

var sink = new CountingSink(laneEdge);

using var sim = new LiveCitySim(cfg, sink);

var policy = sim.RecordPublisher?.Policy as DrErrorPublishPolicy;
if (policy is null)
{
    Console.Error.WriteLine(
        "error: the record publisher's policy is not a DrErrorPublishPolicy -- per-reason attribution is\n"
        + "unavailable, so this run could not distinguish 'thresholds too tight' from 'inherently busy'.");
    return 1;
}

var scenarioLabel = sumocfgPath ?? datasetDir!;
Console.WriteLine($"scenario   : {scenarioLabel}");
Console.WriteLine($"net        : {cfg.ResolveNetPath()}");
Console.WriteLine(
    $"sim step   : dt = {cfg.Dt.ToString("F4", inv)} s  ({(1.0 / cfg.Dt).ToString("F2", inv)} Hz)   "
    + "[measured from the config, not assumed]");
Console.WriteLine(
    $"policy     : PosTol = {policy.PosTol.ToString("F2", inv)} m  "
    + $"LatTol = {policy.LatTol.ToString("F2", inv)} m  "
    + $"MaxInterval = {policy.MaxInterval.ToString("F1", inv)} s  "
    + $"=> heartbeat floor {(1.0 / policy.MaxInterval).ToString("F3", inv)} fires/car/s");
Console.WriteLine($"targets    : cars = {targetCars}  peds = {targetPeds}");
Console.WriteLine();

// ---------------------------------------------------------------------------------------------------
// Warm-up. Measuring before the car count reaches the target would average a nearly-empty net into the
// result, and measuring before the fire rate settles would count the first-sighting burst (every car's
// first frame fires, since SecondsSinceLastSent starts at +inf).
// ---------------------------------------------------------------------------------------------------
var warmSteps = 0;
var reached = false;
for (; warmSteps < warmupCap; warmSteps++)
{
    sim.Step();
    if (sim.CurrentCars >= targetCars)
    {
        reached = true;
        break;
    }
}

var carsAtWarmEnd = sim.CurrentCars;
if (!reached)
{
    // Not an error -- a net can simply be unable to hold the target. But it MUST be reported, because
    // fires/car/s is normalised by the achieved count and the target is then a fiction.
    Console.WriteLine(
        $"WARNING: target {targetCars} cars NOT reached in {warmupCap} warm-up steps -- "
        + $"achieved {carsAtWarmEnd}. Every number below is normalised by the ACHIEVED count.");
}
else
{
    Console.WriteLine($"warm-up    : target reached after {warmSteps + 1} steps ({carsAtWarmEnd} cars live)");
}

// Let the rate settle past the first-sighting burst before counting: one full heartbeat interval, so every
// car that entered during the ramp has had a chance to fall into its steady cadence.
var settleSteps = (int)Math.Ceiling(policy.MaxInterval / cfg.Dt);
for (var i = 0; i < settleSteps; i++)
{
    sim.Step();
}

Console.WriteLine($"settle     : {settleSteps} steps (one MaxInterval) discarded before counting");
Console.WriteLine();

// ---------------------------------------------------------------------------------------------------
// Measure.
// ---------------------------------------------------------------------------------------------------
sink.ResetForMeasurement(sim.Time);
policy.ResetReasonCounters();

var carSamples = new List<int>(measureSteps);
var simT0 = sim.Time;
var sw = System.Diagnostics.Stopwatch.StartNew();

for (var step = 1; step <= measureSteps; step++)
{
    sim.Step();
    carSamples.Add(sim.CurrentCars);

    if (!quiet && step % 60 == 0)
    {
        var simSoFar = sim.Time - simT0;
        Console.WriteLine(
            $"  step {step,4}  simT +{simSoFar,6:F1}s  cars {sim.CurrentCars,5}  "
            + $"fires {sink.TotalFires,8}  fires/s {(sink.TotalFires / Math.Max(simSoFar, 1e-9)),8:F1}");
    }
}

sw.Stop();

var simElapsed = sim.Time - simT0;
var wallElapsed = sw.Elapsed.TotalSeconds;
var meanCars = carSamples.Count > 0 ? carSamples.Average() : 0.0;
var fires = sink.TotalFires;
var firesPerSimSecond = fires / Math.Max(simElapsed, 1e-9);
var firesPerCarPerSecond = meanCars > 0 ? firesPerSimSecond / meanCars : 0.0;
var rtf = simElapsed / Math.Max(wallElapsed, 1e-9);

// Bytes: the record payload, and the whole framed cost (a 16-byte header per frame that carried >=1 record).
var recordBytesPerSecond = firesPerSimSecond * FrameCodec.VehicleRecordSize;
var framedBytes = sink.NonEmptyFrames * (long)FrameCodec.HeaderSize
                  + fires * (long)FrameCodec.VehicleRecordSize;
var framedBytesPerSecond = framedBytes / Math.Max(simElapsed, 1e-9);

var (meanGap, p95Gap, gapSamples) = sink.IntervalStats();

Console.WriteLine();
Console.WriteLine("================ RESULT ================");
Console.WriteLine($"sim seconds measured        : {simElapsed.ToString("F1", inv)} s over {measureSteps} steps");
Console.WriteLine($"wall seconds                : {wallElapsed.ToString("F1", inv)} s");
Console.WriteLine($"live cars (mean over run)   : {meanCars.ToString("F0", inv)}"
                  + $"  (min {carSamples.Min()}, max {carSamples.Max()})");
Console.WriteLine($"total fires                 : {fires}");
Console.WriteLine($"FIRES/SECOND (sim time)     : {firesPerSimSecond.ToString("F1", inv)}");
Console.WriteLine($"FIRES/CAR/SECOND            : {firesPerCarPerSecond.ToString("F3", inv)}"
                  + $"   (heartbeat-only floor = {(1.0 / policy.MaxInterval).ToString("F3", inv)})");
Console.WriteLine($"PRODUCER REAL-TIME FACTOR   : {rtf.ToString("F2", inv)}x"
                  + (rtf < 1.0 ? "   <-- BELOW 1.0: the producer cannot keep up" : ""));
Console.WriteLine();
Console.WriteLine($"bytes/s (records only)      : {recordBytesPerSecond.ToString("F0", inv)} B/s"
                  + $"  ({(recordBytesPerSecond / 1024.0).ToString("F1", inv)} KiB/s)");
Console.WriteLine($"bytes/s (records + framing) : {framedBytesPerSecond.ToString("F0", inv)} B/s"
                  + $"  ({(framedBytesPerSecond / 1024.0).ToString("F1", inv)} KiB/s)"
                  + $"   [{sink.NonEmptyFrames} non-empty frames x {FrameCodec.HeaderSize} B header]");
Console.WriteLine();
Console.WriteLine("per-reason split (first condition that fired, in policy short-circuit order):");
var pTotal = Math.Max(policy.FiresTotal, 1);
Console.WriteLine($"  laneChange : {policy.FiresLaneChanged,10}  {(100.0 * policy.FiresLaneChanged / pTotal),5:F1}%");
Console.WriteLine(
    $"     of which SAME-EDGE (a real lateral lane change): {sink.LaneChangeSameEdge,10}"
    + $"  {(100.0 * sink.LaneChangeSameEdge / pTotal),5:F1}% of all fires");
Console.WriteLine(
    $"     of which NEW-EDGE (drove onto the next lane)   : {sink.LaneChangeNewEdge,10}"
    + $"  {(100.0 * sink.LaneChangeNewEdge / pTotal),5:F1}% of all fires");
Console.WriteLine(
    $"     of which INTERNAL (entered/left a junction)    : {sink.LaneChangeInternal,10}"
    + $"  {(100.0 * sink.LaneChangeInternal / pTotal),5:F1}% of all fires"
    + "   [subset of NEW-EDGE]");
Console.WriteLine($"  posError   : {policy.FiresPos,10}  {(100.0 * policy.FiresPos / pTotal),5:F1}%");
Console.WriteLine($"  latError   : {policy.FiresLat,10}  {(100.0 * policy.FiresLat / pTotal),5:F1}%");
Console.WriteLine($"  heartbeat  : {policy.FiresHeartbeat,10}  {(100.0 * policy.FiresHeartbeat / pTotal),5:F1}%");

// Cross-check: the policy's own tally must equal what the sink counted. If these disagree, something other
// than this policy is emitting records (or a fire was double-counted) and the split cannot be trusted.
if (policy.FiresTotal != fires)
{
    Console.WriteLine();
    Console.WriteLine(
        $"  WARNING: policy tally {policy.FiresTotal} != sink tally {fires} (delta "
        + $"{policy.FiresTotal - fires}). The per-reason split does not account for every emitted record;\n"
        + "  treat the split as indicative only and find the other emitter before quoting it.");
}
else
{
    Console.WriteLine($"  (cross-check OK: policy tally == sink tally == {fires})");
}

Console.WriteLine();
Console.WriteLine($"per-car inter-update interval: mean {meanGap.ToString("F3", inv)} s"
                  + $"   p95 {p95Gap.ToString("F3", inv)} s   over {gapSamples} intervals");
Console.WriteLine($"one-time lane geometry      : {sink.GeometryBytes} B"
                  + $"  ({(sink.GeometryBytes / 1024.0 / 1024.0).ToString("F2", inv)} MiB)"
                  + $" for {sink.GeometryLanes} lanes");
Console.WriteLine("========================================");

if (csvPath is not null)
{
    var exists = File.Exists(csvPath);
    using var w = new StreamWriter(csvPath, append: true);
    if (!exists)
    {
        w.WriteLine("scenario,targetCars,meanCars,dt,simSeconds,wallSeconds,fires,firesPerSec,"
                    + "firesPerCarPerSec,rtf,bytesPerSec,framedBytesPerSec,laneChange,pos,lat,heartbeat,"
                    + "meanGapSec,p95GapSec,geometryBytes,geometryLanes");
    }

    w.WriteLine(string.Join(",",
        Path.GetFileName(scenarioLabel), targetCars, meanCars.ToString("F1", inv),
        cfg.Dt.ToString("F4", inv), simElapsed.ToString("F2", inv), wallElapsed.ToString("F2", inv),
        fires, firesPerSimSecond.ToString("F2", inv), firesPerCarPerSecond.ToString("F4", inv),
        rtf.ToString("F3", inv), recordBytesPerSecond.ToString("F0", inv),
        framedBytesPerSecond.ToString("F0", inv),
        policy.FiresLaneChanged, policy.FiresPos, policy.FiresLat, policy.FiresHeartbeat,
        meanGap.ToString("F4", inv), p95Gap.ToString("F4", inv),
        sink.GeometryBytes, sink.GeometryLanes));
    Console.WriteLine($"csv appended: {csvPath}");
}

return 0;

// -------------------------------------------------------------------------------------------------------
// A sink that only counts. Deliberately does no encoding: the point is to measure what the POLICY emits,
// so adding a codec round-trip here would fold the encoder's cost into a number that is about write RATE.
// Byte figures are computed from FrameCodec's own constants instead.
// -------------------------------------------------------------------------------------------------------
internal sealed class CountingSink : IReplicationSink
{
    private readonly Dictionary<VehicleHandle, double> _lastFireSimTime = new();
    private readonly Dictionary<VehicleHandle, int> _lastFireLane = new();
    private readonly List<double> _gaps = new();
    private readonly string[] _laneEdge;
    private double _frameTime;

    public CountingSink(string[] laneEdge) => _laneEdge = laneEdge;

    // WHY THIS SPLIT EXISTS. The policy's `laneChanged` signal is `laneHandle != lastPublishedLane` -- ANY
    // change of lane identity. That is NOT the same as "the driver changed lanes": a car driving straight
    // onto the next street, or through a junction's internal lanes, changes lane identity too. On a real
    // urban net most of it is the latter, and the distinction decides whether the write rate is tunable.
    //
    // An EARLIER attempt at this split used PublishSignals.LaneChangingOrManoeuvring and reported 0%
    // manoeuvres -- which measured NOTHING, because that flag tracks LATERAL steering (sublane coupling,
    // overtake spill, give-way drift, crowd swerve), all structurally impossible in a discrete-lane run with
    // no peds and no lcOpposite. An ordinary LC2013 lane change does not set it either. Comparing the two
    // lanes' EDGE ids is the test that actually discriminates.
    public long LaneChangeSameEdge { get; private set; }   // same edge  => a real lateral lane change
    public long LaneChangeNewEdge { get; private set; }    // new edge   => route progression
    public long LaneChangeInternal { get; private set; }   // new edge AND the new lane is a junction lane

    public long TotalFires { get; private set; }

    public long NonEmptyFrames { get; private set; }

    public int GeometryBytes { get; private set; }

    public int GeometryLanes { get; private set; }

    // Geometry is published once, BEFORE measurement starts, so it is captured outside the counters and
    // survives ResetForMeasurement -- it is a one-time transfer cost, not part of the per-second rate.
    public void PublishGeometry(IReadOnlyList<GeometryCodec.LaneGeo> lanes)
    {
        GeometryLanes = lanes.Count;
        var total = GeometryCodec.HeaderSize;
        for (var i = 0; i < lanes.Count; i++)
        {
            total += GeometryCodec.LaneSize(lanes[i]);
        }

        GeometryBytes = total;
    }

    public void PublishLifecycle(in LifecycleRecord record)
    {
        // Spawn/despawn announcements are durable and once-per-vehicle, not part of the per-step write rate.
    }

    public void PublishFrame(uint step, double time, ReadOnlySpan<VehicleRecord> movers)
    {
        _frameTime = time;
        if (movers.Length == 0)
        {
            return;
        }

        NonEmptyFrames++;
        TotalFires += movers.Length;

        for (var i = 0; i < movers.Length; i++)
        {
            var h = movers[i].Handle;
            var lane = movers[i].LaneHandle;

            if (_lastFireLane.TryGetValue(h, out var prevLane) && prevLane != lane)
            {
                var from = prevLane >= 0 && prevLane < _laneEdge.Length ? _laneEdge[prevLane] : null;
                var to = lane >= 0 && lane < _laneEdge.Length ? _laneEdge[lane] : null;
                if (from is not null && to is not null && from == to)
                {
                    LaneChangeSameEdge++;
                }
                else
                {
                    LaneChangeNewEdge++;
                    // SUMO names internal (junction) edges with a leading ':'.
                    if (to is not null && to.StartsWith(':'))
                    {
                        LaneChangeInternal++;
                    }
                }
            }

            _lastFireLane[h] = lane;

            if (_lastFireSimTime.TryGetValue(h, out var prev))
            {
                // Only a gap between two fires of the SAME car counts. A car's first fire has no
                // predecessor, so it contributes no interval -- including it would report a spuriously
                // short mean, since "time since a car that did not exist last sent" is not an interval.
                _gaps.Add(time - prev);
            }

            _lastFireSimTime[h] = time;
        }
    }

    public void PublishTrafficLights(uint step, double time, IReadOnlyList<TlCodec.TlEntry> lights)
    {
        // Low-rate and not part of the vehicle write rate this tool measures.
    }

    public void ResetForMeasurement(double now)
    {
        TotalFires = 0;
        NonEmptyFrames = 0;
        LaneChangeSameEdge = 0;
        LaneChangeNewEdge = 0;
        LaneChangeInternal = 0;
        _gaps.Clear();

        // Keep the last-fire times: a car mid-cadence at the measurement boundary should contribute its
        // real next interval. Rebasing them to `now` would manufacture a short first gap for every car.
        _frameTime = now;
    }

    public (double Mean, double P95, int Count) IntervalStats()
    {
        if (_gaps.Count == 0)
        {
            return (0.0, 0.0, 0);
        }

        var sorted = _gaps.ToArray();
        Array.Sort(sorted);
        var mean = 0.0;
        for (var i = 0; i < sorted.Length; i++)
        {
            mean += sorted[i];
        }

        mean /= sorted.Length;
        var idx = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return (mean, sorted[idx], sorted.Length);
    }

    public void Dispose()
    {
        // Owns nothing.
    }
}
