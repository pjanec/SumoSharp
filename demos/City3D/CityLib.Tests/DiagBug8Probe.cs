using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Sim.Core;
using Sim.Viewer.Motion;
using Xunit;
using Xunit.Abstractions;

namespace CityLib.Tests;

// TEMPORARY diagnostic probe for Bug 8 (backward creep on a decel-to-stop/crawling car), using REAL SUMO
// dynamics (not a synthetic linear decel) at the report's delay=1.0s. Logs SIGNED longitudinal progress
// (both raw arc-length via DrClock.Resolve, and world-space via KinematicReconstructor) for every low-speed
// vehicle frame, flagging backward steps and whether a new packet ("wireChanged") landed that frame.
public class DiagBug8Probe
{
    private readonly ITestOutputHelper _output;
    public DiagBug8Probe(ITestOutputHelper output) => _output = output;

    private const int FramesPerTick = 3;
    private const int FrameMillis = 15;
    private const double Delay = 1.0; // matches the report's City3D default

    [Fact]
    public void Probe_RealSumo_LowSpeedVehicles_SignedArcAndWorldDeltas()
    {
        var (net, rou, cfg) = Paths("09-traffic-light");
        using var sim = new SimSource(net, rou, cfg);

        var clock = new DrClock();
        var recon = new KinematicReconstructor { LookAheadMeters = 3.0, LookAheadLengthFactor = 0.5, CoarseFeed = true };

        var lastArcPos = new Dictionary<VehicleHandle, double>();
        var lastWorld = new Dictionary<VehicleHandle, (double X, double Y)>();
        var lastHeading = new Dictionary<VehicleHandle, float>();
        var lastHistCount = new Dictionary<VehicleHandle, int>();

        int arcBackward = 0, worldBackward = 0;
        var arcLog = new List<string>();
        var worldLog = new List<string>();

        for (var t = 0; t < 200; t++)
        {
            sim.Tick();
            for (var f = 0; f < FramesPerTick; f++)
            {
                Thread.Sleep(FrameMillis);
                sim.Source.Pump();
                clock.Pump(sim.Source.LatestVehicleSampleTime);

                foreach (var kv in sim.Source.History)
                {
                    var handle = kv.Key;
                    var hist = kv.Value;
                    if (hist.Count == 0 || !sim.Source.Dims.TryGetValue(handle, out var dims))
                    {
                        continue;
                    }

                    var wireChanged = !lastHistCount.TryGetValue(handle, out var prevCount) || hist.Count != prevCount;
                    lastHistCount[handle] = hist.Count;

                    DrClock.Resolved resolved;
                    try
                    {
                        resolved = clock.Resolve(hist, Delay, sim.LocalLanes);
                    }
                    catch (KeyNotFoundException)
                    {
                        continue;
                    }

                    if (resolved.State.Speed > 0.6)
                    {
                        // Only care about the near-stop/crawl regime the report describes.
                        lastArcPos.Remove(handle);
                        lastWorld.Remove(handle);
                        continue;
                    }

                    var arcPos = resolved.State.Pos;
                    if (lastArcPos.TryGetValue(handle, out var prevArc))
                    {
                        var d = arcPos - prevArc;
                        if (d < -1e-6)
                        {
                            arcBackward++;
                            if (arcLog.Count < 40)
                            {
                                arcLog.Add($"t={sim.Time:F2} veh={handle} spd={resolved.State.Speed:F3} accel={resolved.State.Accel:F3} prevArc={prevArc:F5} arc={arcPos:F5} d={d:F5} wireChanged={wireChanged}");
                            }
                        }
                    }
                    lastArcPos[handle] = arcPos;

                    var r = recon.Resolve(handle, resolved, sim.LocalLanes, (dims.Length, dims.Width), 1f / 60f);
                    if (!r.Ok)
                    {
                        continue;
                    }

                    if (lastWorld.TryGetValue(handle, out var prevW) && lastHeading.TryGetValue(handle, out var prevH))
                    {
                        // Signed "along travel" displacement: project the world step onto the PREVIOUS heading.
                        var m = (90.0 - prevH) * Math.PI / 180.0;
                        var hx = Math.Cos(m);
                        var hy = Math.Sin(m);
                        var dx = r.CenterX - prevW.X;
                        var dy = r.CenterY - prevW.Y;
                        var along = dx * hx + dy * hy;
                        if (along < -1e-6)
                        {
                            worldBackward++;
                            if (worldLog.Count < 40)
                            {
                                worldLog.Add($"t={sim.Time:F2} veh={handle} spd={resolved.State.Speed:F3} prevCenter=({prevW.X:F5},{prevW.Y:F5}) center=({r.CenterX:F5},{r.CenterY:F5}) along={along:F5} wireChanged={wireChanged}");
                            }
                        }
                    }
                    lastWorld[handle] = (r.CenterX, r.CenterY);
                    lastHeading[handle] = r.HeadingDeg;
                }
            }
        }

        _output.WriteLine($"arcBackward={arcBackward}");
        foreach (var l in arcLog) _output.WriteLine("  ARC " + l);
        _output.WriteLine($"worldBackward={worldBackward}");
        foreach (var l in worldLog) _output.WriteLine("  WORLD " + l);

        // Diagnostic only -- always "passes" so its output is captured; the numbers are what matter.
        Assert.True(true);
    }

    private static (string Net, string Rou, string Cfg) Paths(string scenario)
    {
        var d = Path.Combine(RepoRoot(), "scenarios", scenario);
        return (Path.Combine(d, "net.net.xml"), Path.Combine(d, "rou.rou.xml"), Path.Combine(d, "config.sumocfg"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above test assembly).");
    }
}
