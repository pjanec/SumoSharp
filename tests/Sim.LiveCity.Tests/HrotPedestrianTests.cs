using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sim.Pedestrians.Lod;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.LiveCity.Tests;

// docs/HROT-INTEGRATION-DESIGN.md H14 -- pedestrians on the HROT wire, through LiveCitySim: PedIdProvider names, the
// ped lifecycle on the sim's own in-memory ped wire (PedSource), RemovePedestrian, and the serving node's reads
// (CopyLivePedestrians, PedestriansWithin). Runs on the committed `scenarios/_ped/roadnet_min` fixture (a synthetic
// ped-equipped 3x3 grid, RouteGraph mode -- the arbitrary-net path HROT uses).
public class HrotPedestrianTests
{
    private readonly ITestOutputHelper _out;
    public HrotPedestrianTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root (Traffic.sln) not found above " + AppContext.BaseDirectory);
    }

    private static LiveCityConfig Config(Func<string>? names)
    {
        var cfg = LiveCityConfig.ForDataset(Path.Combine(RepoRoot(), "scenarios", "_ped", "roadnet_min"));
        cfg.PedPopulationCap = 60;
        cfg.PedSpawnRatePerSecond = 10.0;
        cfg.CarTargetConcurrent = 5;
        cfg.PedIdProvider = names;
        return cfg;
    }

    [Fact]
    public void H14_ProviderNamesRideThePedWire_AndTheEmbeddedReaderSeesNameAndPose()
    {
        var issued = new List<string>();
        using var sim = new LiveCitySim(Config(() => { var n = "hrot-" + (5000 + issued.Count); issued.Add(n); return n; }));
        Assert.True(sim.PedestriansEnabled);
        var remote = new PedRemoteReconstructor(sim.PedSource);

        for (var i = 0; i < 80; i++)
        {
            sim.Step();
            remote.Pump(sim.Time);
        }

        var spawns = sim.PedSource.Lifecycles.Where(l => l.Kind == PedLifecycleKind.Spawn).ToList();
        Assert.NotEmpty(spawns);
        Assert.All(spawns, s => Assert.Contains(s.Name, issued));
        Assert.Equal(spawns.Count, spawns.Select(s => s.Name).Distinct().Count());

        // The serving node's read and the embedded wire reader agree: same names, same positions at sim.Time.
        var local = new List<LivePedestrian>();
        Assert.True(sim.CopyLivePedestrians(local) > 0);
        var wire = new List<RemotePedPose>();
        remote.CopyLivePeds(sim.Time, wire);
        Assert.Equal(local.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal), wire.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        var worst = 0.0;
        foreach (var p in local)
        {
            var w = wire.Single(r => r.Name == p.Name);
            worst = Math.Max(worst, Math.Sqrt(((w.X - p.X) * (w.X - p.X)) + ((w.Y - p.Y) * (w.Y - p.Y))));
        }

        _out.WriteLine($"{local.Count} live peds; worst wire-vs-server position gap at sim.Time: {worst:F4} m");
        Assert.True(worst < 0.05, $"worst gap {worst} m");

        // "peds within r": the ped itself at r = 0, and everybody inside a huge radius.
        var names = new List<string>();
        Assert.Contains(local[0].Name, (sim.PedestriansWithin(local[0].X, local[0].Y, 0.0, names), names).names);
        Assert.Equal(local.Count, sim.PedestriansWithin(local[0].X, local[0].Y, 1e6, names));
    }

    [Fact]
    public void H14_RemovePedestrian_MidRun_ReturnsItsState_ThenSteps_AndTheWireDespawnsIt()
    {
        var n = 0;
        using var sim = new LiveCitySim(Config(() => "ped:" + (++n)));
        var remote = new PedRemoteReconstructor(sim.PedSource);
        for (var i = 0; i < 40; i++)
        {
            sim.Step();
            remote.Pump(sim.Time);
        }

        var live = new List<LivePedestrian>();
        sim.CopyLivePedestrians(live);
        var victim = live[live.Count / 2];
        Assert.True(remote.TryGetId(victim.Name, out var wireId));

        Assert.True(sim.RemovePedestrian(victim.Name, out var state));
        _out.WriteLine($"removed {victim.Name}: ({state.X:F2},{state.Y:F2},{state.Z:F2}) v=({state.Vx:F2},{state.Vy:F2}) heading={state.Heading:F3}");
        Assert.Equal(victim.X, state.X, 9);
        Assert.Equal(victim.Y, state.Y, 9);
        Assert.InRange(Math.Sqrt((state.Vx * state.Vx) + (state.Vy * state.Vy)), 0.0, 1.3 * 1.5);
        Assert.InRange(state.Heading, -Math.PI, Math.PI);
        Assert.False(sim.RemovePedestrian(victim.Name, out _));
        Assert.False(sim.RemovePedestrian("nobody", out _));

        // Without another Step (a paused host): the Despawn is already on the wire.
        remote.Pump(sim.Time);
        Assert.False(remote.TryGetId(victim.Name, out _));

        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 40; i++)
            {
                sim.Step();
                remote.Pump(sim.Time);
            }
        });
        Assert.Null(ex);

        Assert.Contains(sim.PedSource.Lifecycles, l => l.Kind == PedLifecycleKind.Despawn && (int)l.Handle.Index == wireId);
        Assert.False(remote.TryGetId(victim.Name, out _));
        Assert.DoesNotContain(wireId, remote.KnownIds);
        sim.CopyLivePedestrians(live);
        Assert.DoesNotContain(live, p => p.Name == victim.Name);
    }

    [Fact]
    public void H14_WithoutAProvider_ThePedWireCarriesNoLifecycle_AndNamesAreTheIdDigits()
    {
        using var sim = new LiveCitySim(Config(null));
        for (var i = 0; i < 40; i++) sim.Step();
        sim.PedSource.Pump();
        Assert.DoesNotContain(sim.PedSource.Lifecycles, l => l.Kind == PedLifecycleKind.Spawn || l.Kind == PedLifecycleKind.Despawn);

        var live = new List<LivePedestrian>();
        Assert.True(sim.CopyLivePedestrians(live) > 0);
        Assert.All(live, p => Assert.True(int.TryParse(p.Name, out _), p.Name));
        Assert.True(sim.RemovePedestrian(live[0].Name, out _)); // removable by its digits
        var ex = Record.Exception(() => { for (var i = 0; i < 10; i++) sim.Step(); });
        Assert.Null(ex);
    }
}
