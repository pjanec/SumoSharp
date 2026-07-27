using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CityLib;
using Sim.LiveCity;
using Xunit;

namespace CityLib.Tests;

// docs/EXTERNAL-NET-VIEWER-DESIGN.md / -TASKS.md, Stage T (T1/T2/T3): the Godot City3D viewer's half of
// the external-net work -- loading an arbitrary SumoData cut, recentering it for float precision, and the
// live density dials.
//
// Everything here is headless: CityLib is Godot-free by design, so the placement math, the frame, the
// crop, and the live setters can all be asserted without a running Godot. The fixture is the committed
// scenarios/_ped/georef_min (design §6) -- georeferenced UTM32N, 3-D, ~(91850, 73960) far from the origin.
public class ExternalNetViewerTests
{
    private static string RepoRoot()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && Directory.Exists(Path.Combine(output, "scenarios")))
            {
                return output;
            }
        }
        catch
        {
            // fall through
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scenarios")) && File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("could not resolve the SumoSharp repo root.");
    }

    private static string FixtureCfg()
        => Path.Combine(RepoRoot(), "scenarios", "_ped", "georef_min", "scenario.sumocfg");

    // ---- T2: the frame ---------------------------------------------------------------------------

    [Fact]
    public void IdentityFrame_IsBitwiseIdenticalToTheLegacyTransform()
    {
        // The whole no-regression argument for T2 rests on this: every unconverted path and every
        // existing test keeps its exact numbers because Identity is the same arithmetic.
        var samples = new (double X, double Y, double Z)[]
        {
            (0, 0, 0), (10, 20, 3), (-108030.0, -136900.0, -374.5),
            (91850.5, 73960.25, 372.5), (2055, 2895, 0), (1e-7, -1e-7, 1e-7),
        };

        foreach (var (x, y, z) in samples)
        {
            var legacy = CoordinateTransform.SumoToGodot(x, y, z);
            var framed = SumoGodotFrame.Identity.ToGodot(x, y, z);

            Assert.Equal(BitConverter.SingleToInt32Bits(legacy.X), BitConverter.SingleToInt32Bits(framed.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(legacy.Y), BitConverter.SingleToInt32Bits(framed.Y));
            Assert.Equal(BitConverter.SingleToInt32Bits(legacy.Z), BitConverter.SingleToInt32Bits(framed.Z));
        }
    }

    [Fact]
    public void RecenteredFrame_KeepsCentimetreDetail_WhereTheIdentityFrameLosesIt()
    {
        // The test that proves the PROBLEM exists, not merely that the fix compiles: at the fixture's
        // ~9e4 magnitude a float has ~8 mm of resolution, so the identity frame cannot represent a
        // quarter-millimetre offset at all, while the recentered frame carries it exactly.
        const double baseX = 91850.5;
        const double baseY = 73960.25;
        const double z = 372.5;
        const double tinyOffset = 0.00025; // 0.25 mm

        var frame = new SumoGodotFrame(baseX, baseY, z);

        var idA = CoordinateTransform.SumoToGodot(baseX, baseY, z);
        var idB = CoordinateTransform.SumoToGodot(baseX + tinyOffset, baseY, z);
        Assert.Equal(idA.X, idB.X); // the offset vanished entirely in float

        var frA = frame.ToGodot(baseX, baseY, z);
        var frB = frame.ToGodot(baseX + tinyOffset, baseY, z);
        Assert.NotEqual(frA.X, frB.X);
        Assert.Equal(tinyOffset, frB.X - frA.X, 6);
    }

    [Fact]
    public void RecenteredFrame_RoundTripsThroughToSumo_AtLargeMagnitudes()
    {
        var frame = new SumoGodotFrame(91850.0, 73960.0, 385.0);
        const double x = 91903.75;
        const double y = 73902.5;

        var (gx, _, gz) = frame.ToGodot(x, y, 371.0);
        var (backX, backY) = frame.ToSumo(gx, gz);

        Assert.Equal(x, backX, 3);
        Assert.Equal(y, backY, 3);
    }

    [Fact]
    public void IdentityFrame_ToSumo_IsTheLegacyInverse()
    {
        // Before the frame existed, Main.CameraLcZone inverted the mapping by hand as (gx, -gz). Identity
        // must reproduce that exactly, or the demo's realism zone would shift.
        var (x, y) = SumoGodotFrame.Identity.ToSumo(123.5f, -456.25f);
        Assert.Equal(123.5, x, 6);
        Assert.Equal(456.25, y, 6);
    }

    [Fact]
    public void GroundToGodot_IsDatumRelative_AndMatchesToGodotOnAFlatNet()
    {
        // 2-D net (originZ = 0): the two must agree, which is what keeps the demo's overlays byte-identical.
        var flat = new SumoGodotFrame(2475.0, 2475.0, 0.0);
        Assert.Equal(flat.ToGodot(2500.0, 2500.0, -0.05), flat.GroundToGodot(2500.0, 2500.0, -0.05));

        // 3-D net: the ground overlay stays just below Y=0 instead of sinking to -385.
        var hilly = new SumoGodotFrame(91850.0, 73960.0, 385.0);
        var (_, groundY, _) = hilly.GroundToGodot(91900.0, 74000.0, -0.05);
        Assert.True(Math.Abs(groundY - (-0.05f)) < 1e-4f, $"ground overlay should stay at the datum, got {groundY}");

        var (_, absoluteY, _) = hilly.ToGodot(91900.0, 74000.0, -0.05);
        Assert.True(absoluteY < -380f, $"absolute mapping of z=-0.05 should sink to ~-385, got {absoluteY}");
    }

    [Fact]
    public void ForNetwork_CentresOnTheNetAabb_AndPutsEveryLaneWithinAFewHundredMetres()
    {
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);

        var frame = SumoGodotFrame.ForNetwork(source.Network);

        Assert.True(frame.OriginX > 50000.0, $"expected a far-from-origin net; originX={frame.OriginX}");
        Assert.InRange(frame.OriginZ, 360.0, 410.0);

        var maxAbs = 0.0f;
        foreach (var lane in source.Network.LanesById.Values)
        {
            for (var i = 0; i < lane.Shape.Count; i++)
            {
                var z = lane.ShapeZ is { Count: > 0 } zs && i < zs.Count ? zs[i] : 0.0;
                var (gx, gy, gz) = frame.ToGodot(lane.Shape[i].X, lane.Shape[i].Y, z);
                maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(gx), Math.Max(Math.Abs(gy), Math.Abs(gz))));
            }
        }

        // Everything lands within the net's own half-extent of the origin -- the point of the recenter.
        Assert.True(maxAbs < 1000f, $"recentered geometry should be small; max |coord| = {maxAbs}");
    }

    [Fact]
    public void ForNetwork_OnANetWithNoGeometry_IsIdentity()
    {
        // Defensive branch (never a real net.xml), asserted through the real parser rather than by
        // hand-constructing a NetworkModel.
        var dir = Path.Combine(Path.GetTempPath(), "city3d-emptynet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var netPath = Path.Combine(dir, "net.xml");
            File.WriteAllText(netPath, "<net></net>\n");
            var empty = Sim.Ingest.NetworkParser.Parse(netPath);

            Assert.True(SumoGodotFrame.ForNetwork(empty).IsIdentity);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- T2 SC4: no placement bypasses the frame --------------------------------------------------

    [Fact]
    public void NoPlacementCallsCoordinateTransformDirectly()
    {
        // A source-level guard, because a missed call site type-checks perfectly while being an origin's
        // distance wrong -- it would render that one piece of geometry 90 km from everything else. The
        // static CoordinateTransform.SumoToGodot survives only as the definition of the identity frame
        // (and in tests, which assert exactly that equivalence).
        var city3d = new DirectoryInfo(Path.Combine(RepoRoot(), "demos", "City3D"));
        var offenders = new List<string>();

        foreach (var file in city3d.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (file.Name == "CoordinateTransform.cs" || file.Name == "SumoGodotFrame.cs") continue;
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.Directory?.Name == "CityLib.Tests") continue; // tests assert the equivalence itself

            var lineNo = 0;
            foreach (var line in File.ReadLines(file.FullName))
            {
                lineNo++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue; // a comment mentioning it is fine
                if (Regex.IsMatch(line, @"CoordinateTransform\s*\.\s*SumoToGodot\s*\("))
                {
                    offenders.Add($"{file.Name}:{lineNo}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "every placement must go through a SumoGodotFrame; direct SumoToGodot calls at: "
            + string.Join(", ", offenders));
    }

    // ---- T1: loading an arbitrary cut --------------------------------------------------------------

    [Fact]
    public void LiveCitySource_FromSumocfg_LoadsTheCutAndStepsWithCarsAndPeds()
    {
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);

        for (var i = 0; i < 200; i++)
        {
            source.Tick();
        }

        var snap = source.Sample();
        Assert.True(snap.Cars.Count > 0, "expected cars on the arbitrary cut");
        Assert.True(source.PedestriansEnabled);
        Assert.True(snap.Peds.Count > 0, "expected peds on the arbitrary cut");
    }

    [Fact]
    public void LiveCitySource_OnAnArbitraryNet_CropIsTheWholeNet_NotThePinnedDemoBlock()
    {
        // T1 SC3. The pinned X0..Y1 is the DEMO's hero block; on a Geneva cut it is 90 km from any road,
        // so a viewer that framed its camera and built its meshes to it would render an empty scene.
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);

        var (x0, y0, x1, y1) = source.Crop;
        Assert.NotEqual(cfg.X0, x0);
        Assert.True(x1 > x0 && y1 > y0);

        foreach (var lane in source.Network.LanesById.Values)
        {
            foreach (var (x, y) in lane.Shape)
            {
                Assert.InRange(x, x0, x1);
                Assert.InRange(y, y0, y1);
            }
        }
    }

    [Fact]
    public void LiveCitySource_OnTheDemo_KeepsThePinnedCrop()
    {
        // The other half of T1 SC3: the demo must be untouched.
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        using var source = new LiveCitySource(cfg);

        Assert.Equal((cfg.X0, cfg.Y0, cfg.X1, cfg.Y1), source.Crop);
    }

    // ---- T2 end to end: the meshes the viewer actually builds are small and correctly offset --------

    [Fact]
    public void RoadMeshes_BuiltThroughTheNetFrame_AreSmallAndAPureTranslationOfTheIdentityBuild()
    {
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);
        var frame = SumoGodotFrame.ForNetwork(source.Network);

        var lane = source.Network.LanesById.Values.First(l => l.Shape.Count >= 2);

        var recentered = RoadMeshBuilder.Build(frame, lane.Shape, lane.ShapeZ, lane.Width);
        var raw = RoadMeshBuilder.Build(SumoGodotFrame.Identity, lane.Shape, lane.ShapeZ, lane.Width);

        Assert.Equal(raw.Vertices.Length, recentered.Vertices.Length);
        Assert.True(recentered.Vertices.Length > 0);

        for (var i = 0; i < recentered.Vertices.Length; i += 3)
        {
            // Same shape, shifted by exactly the origin -- so nothing but the offset changed.
            // Tolerance is 0.05 m: the RAW build is the one that has already lost ~cm of precision at
            // 9e4, so the comparison can only ever be as tight as the raw side's own float resolution.
            AssertClose(raw.Vertices[i + 0] - (float)frame.OriginX, recentered.Vertices[i + 0]);
            AssertClose(raw.Vertices[i + 1] - (float)frame.OriginZ, recentered.Vertices[i + 1]);
            AssertClose(raw.Vertices[i + 2] + (float)frame.OriginY, recentered.Vertices[i + 2]);

            Assert.True(Math.Abs(recentered.Vertices[i + 0]) < 1000f);
            Assert.True(Math.Abs(recentered.Vertices[i + 1]) < 1000f);
            Assert.True(Math.Abs(recentered.Vertices[i + 2]) < 1000f);
        }
    }

    [Fact]
    public void CrosswalkStripes_On3DNet_RideOnTheLaneSurface_NotAtAbsoluteZero()
    {
        // Regression guard for the flat-marking assumption: the zebra used to be emitted at absolute
        // z = 0.02 regardless of the lane's elevation, which on this fixture is ~370 m below the road.
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);
        var frame = SumoGodotFrame.ForNetwork(source.Network);

        var crossing = source.Network.LanesById.Values.First(
            l => CrosswalkBuilder.IsCrossingLaneId(l.Id) && l.Shape.Count >= 2 && l.ShapeZ is { Count: > 0 });

        var (mesh, stripes) = CrosswalkBuilder.Build(frame, crossing.Shape, crossing.Width, shapeZ: crossing.ShapeZ);
        Assert.True(stripes > 0);

        for (var i = 1; i < mesh.Vertices.Length; i += 3)
        {
            // Godot Y of a stripe must sit within a couple of metres of the recentered road surface,
            // i.e. near the lane's own (z - originZ), never at -originZ.
            Assert.InRange(mesh.Vertices[i], (float)(crossing.ShapeZ![0] - frame.OriginZ) - 2f,
                (float)(crossing.ShapeZ![0] - frame.OriginZ) + 2f);
        }
    }

    private static void AssertClose(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected ~{expected}, got {actual}");

    // ---- T3: the live density dials ----------------------------------------------------------------

    [Fact]
    public void SetCarTarget_MovesTheLiveCount_WithoutRebuildingTheSource()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.CarTargetConcurrent = 20;
        using var source = new LiveCitySource(cfg);
        var identity = source; // the slider must never swap the source out from under the scene

        for (var i = 0; i < 120; i++)
        {
            source.Tick();
        }

        Assert.Equal(20, source.CarTarget);

        source.SetCarTarget(90);
        Assert.Equal(90, source.CarTarget);

        for (var i = 0; i < 120; i++)
        {
            source.Tick();
        }

        Assert.True(source.CurrentCars > 30, $"expected the car count to climb toward 90, got {source.CurrentCars}");
        Assert.Same(identity, source);
    }

    [Fact]
    public void SetPedDensity_MovesTheLiveCount_WithoutRebuildingTheSource()
    {
        var cfg = LiveCityConfig.ForRepoRoot(RepoRoot());
        cfg.PedPopulationCap = 40;
        using var source = new LiveCitySource(cfg);
        var identity = source;

        for (var i = 0; i < 200; i++)
        {
            source.Tick();
        }

        Assert.Equal(40, source.PedCap);

        source.SetPedDensity(150, 15.0);
        Assert.Equal(150, source.PedCap);
        Assert.Equal(15.0, source.PedSpawnRate);

        for (var i = 0; i < 200; i++)
        {
            source.Tick();
        }

        Assert.True(source.CurrentPeds > 40, $"expected the crowd to grow past the old cap, got {source.CurrentPeds}");
        Assert.Same(identity, source);
    }

    // ---- T2 SC5: the camera -> zone -> ring round-trip ---------------------------------------------

    [Fact]
    public void CameraGroundPoint_RoundTripsThroughTheZone_BackToTheSameGodotPoint()
    {
        // Main's LC-realism zone makes a FULL round trip every frame in Follow mode:
        //   camera raycast (Godot ground point) -> ToSumo -> LiveCitySource.SetLcRealismZone
        //   -> LcZoneX/Y (SUMO) -> GroundToGodot -> the ring's transform.
        // If either direction used a different origin, the ring would sit an origin's distance from
        // where the user is looking -- and the car-yields-ped zone with it. This asserts the whole loop
        // on a recentered net, which is the case that would expose the mismatch.
        var cfg = LiveCityConfig.ForSumocfg(FixtureCfg());
        using var source = new LiveCitySource(cfg);
        var frame = SumoGodotFrame.ForNetwork(source.Network);

        // A point the camera might be looking at: 60 m east / 40 m north of the scene centre, in Godot.
        const float lookGodotX = 60f;
        const float lookGodotZ = -40f;

        var (sumoX, sumoY) = frame.ToSumo(lookGodotX, lookGodotZ);
        source.SetLcRealismZone(sumoX, sumoY, radius: 70.0);

        // The sim must have received the point in the NET's own frame, i.e. out near the cut's real
        // coordinates -- not the small recentered numbers.
        Assert.True(source.LcZoneX > 50000.0, $"the zone must be pushed in SUMO coords; got {source.LcZoneX}");

        var (ringX, _, ringZ) = frame.GroundToGodot(source.LcZoneX, source.LcZoneY, 0.0);
        Assert.True(Math.Abs(ringX - lookGodotX) < 1.0f, $"ring X drifted: {ringX} vs {lookGodotX}");
        Assert.True(Math.Abs(ringZ - lookGodotZ) < 1.0f, $"ring Z drifted: {ringZ} vs {lookGodotZ}");
    }



}
