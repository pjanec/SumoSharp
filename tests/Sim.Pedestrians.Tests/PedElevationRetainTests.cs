using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Sim.Pedestrians;
using Xunit;

namespace Sim.Pedestrians.Tests;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.2/§3.3, -TASKS.md C1: `PedNetworkParser` RETAINS the third
// coordinate of every ped-lane shape instead of discarding it, as `Sim.Ingest.NetworkParser` already
// does on the vehicle side.
//
// The contract of this channel is INDEX ALIGNMENT with the 2-D shape, and NULL (not empty, not zeros)
// on a 2-D net -- that null is what keeps every committed 2-D scenario bit-identical and what lets a
// consumer tell "no elevation data" from "at sea level". Both are asserted here rather than assumed.
public class PedElevationRetainTests
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
            // fall through to the walk-up fallback
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

    // The 3-D georeferenced fixture (a synthetic stand-in for a SumoData Geneva cut).
    private static string Net3D() => Path.Combine(RepoRoot(), "scenarios", "_ped", "georef_min", "scenario.net.xml");

    // The 2-D demo net -- the bit-identical-regression reference.
    private static string Net2D() => Path.Combine(RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml");

    // ---- C1·SC1: every channel present and index-aligned on a 3-D net ------------------------------

    [Fact]
    public void On3DNet_EveryPedGeometryCarriesZ_IndexAlignedWithItsShape()
    {
        var net = PedNetworkParser.Load(Net3D());

        Assert.NotEmpty(net.Sidewalks);
        Assert.NotEmpty(net.Crossings);
        Assert.NotEmpty(net.WalkingAreas);

        foreach (var sw in net.Sidewalks)
        {
            Assert.True(sw.ShapeZ is not null, $"sidewalk {sw.Id} lost its elevation channel");
            Assert.Equal(sw.Shape.Count, sw.ShapeZ!.Count);
        }

        foreach (var cr in net.Crossings)
        {
            Assert.True(cr.ShapeZ is not null, $"crossing {cr.Id} lost its elevation channel");
            Assert.Equal(cr.Shape.Count, cr.ShapeZ!.Count);
        }

        foreach (var wa in net.WalkingAreas)
        {
            Assert.True(wa.PolygonZ is not null, $"walkingarea {wa.Id} lost its elevation channel");
            Assert.Equal(wa.Polygon.Count, wa.PolygonZ!.Count);
        }
    }

    // ---- C1·SC4: the crossing OUTLINE keeps its z too (it is what a crosswalk polygon is built from)

    [Fact]
    public void On3DNet_CrossingOutlineAlsoKeepsItsZ()
    {
        var net = PedNetworkParser.Load(Net3D());

        var withOutline = net.Crossings.Where(c => c.Outline.Count > 0).ToList();
        Assert.NotEmpty(withOutline);

        foreach (var cr in withOutline)
        {
            Assert.True(cr.OutlineZ is not null, $"crossing {cr.Id} outline lost its elevation channel");
            Assert.Equal(cr.Outline.Count, cr.OutlineZ!.Count);
        }
    }

    // ---- C1·SC2: values equal the net file's own 3rd components ------------------------------------

    [Fact]
    public void On3DNet_ParsedElevationsEqualTheNetFilesThirdComponents()
    {
        // Read the expectation OUT OF THE XML rather than hardcoding it, so this cannot drift if the
        // fixture is regenerated.
        var netPath = Net3D();
        var net = PedNetworkParser.Load(netPath);
        var doc = XDocument.Load(netPath);

        // The sidewalk with the MOST vertices, so the comparison exercises a real polyline rather than
        // a two-point segment (this fixture's sidewalks are mostly straight, so don't assume >2).
        var sidewalk = net.Sidewalks.OrderByDescending(s => s.Shape.Count).First();
        Assert.True(sidewalk.Shape.Count >= 2);
        var laneEl = doc.Root!.Elements("edge")
            .Elements("lane")
            .First(l => (string?)l.Attribute("id") == sidewalk.Id);

        var expected = ((string)laneEl.Attribute("shape")!)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(tok => double.Parse(tok.Split(',')[2], CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(expected.Count, sidewalk.ShapeZ!.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], sidewalk.ShapeZ[i], 9);
        }
    }

    // ---- C1·SC3: a 2-D net yields NULL, not an empty array and not zeros ---------------------------

    [Fact]
    public void On2DDemoNet_EveryElevationChannelIsNull_NotEmptyAndNotZeros()
    {
        // This is the assertion that keeps §3.3's parity-inertness claim honest: on the demo net there
        // is no elevation channel at all, so nothing downstream can accidentally consume zeros.
        var net = PedNetworkParser.Load(Net2D());

        Assert.NotEmpty(net.Sidewalks);

        foreach (var sw in net.Sidewalks)
        {
            Assert.Null(sw.ShapeZ);
        }

        foreach (var cr in net.Crossings)
        {
            Assert.Null(cr.ShapeZ);
            Assert.Null(cr.OutlineZ);
        }

        foreach (var wa in net.WalkingAreas)
        {
            Assert.Null(wa.PolygonZ);
        }
    }

    // ---- the null-vs-zeros distinction, stated directly on the parser ------------------------------

    [Fact]
    public void AShapeWithNoThirdComponent_YieldsNull_EvenWhenOtherLanesHaveZ()
    {
        // All-or-nothing per shape, mirroring Sim.Ingest.NetworkParser.ParseShapeZ: a 2-D vertex
        // anywhere in a shape means that shape has no usable elevation profile, so null rather than a
        // half-populated array a consumer would index blindly.
        var dir = Path.Combine(Path.GetTempPath(), "ped-z-mixed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var netPath = Path.Combine(dir, "net.xml");
            File.WriteAllText(netPath,
                "<net>\n"
                + "  <edge id=\"sw_flat\" from=\"A\" to=\"B\">\n"
                + "    <lane id=\"sw_flat_0\" index=\"0\" allow=\"pedestrian\" width=\"2.0\" speed=\"1.5\" length=\"10.0\" shape=\"0,0 10,0\"/>\n"
                + "  </edge>\n"
                + "  <edge id=\"sw_hill\" from=\"B\" to=\"C\">\n"
                + "    <lane id=\"sw_hill_0\" index=\"0\" allow=\"pedestrian\" width=\"2.0\" speed=\"1.5\" length=\"10.0\" shape=\"10,0,5.5 20,0,7.25\"/>\n"
                + "  </edge>\n"
                + "  <edge id=\"sw_partial\" from=\"C\" to=\"D\">\n"
                + "    <lane id=\"sw_partial_0\" index=\"0\" allow=\"pedestrian\" width=\"2.0\" speed=\"1.5\" length=\"10.0\" shape=\"20,0,7.25 30,0\"/>\n"
                + "  </edge>\n"
                + "</net>\n");

            var net = PedNetworkParser.Load(netPath);

            var flat = net.Sidewalks.Single(s => s.Id == "sw_flat_0");
            Assert.Null(flat.ShapeZ);

            var hill = net.Sidewalks.Single(s => s.Id == "sw_hill_0");
            Assert.Equal(new[] { 5.5, 7.25 }, hill.ShapeZ);

            var partial = net.Sidewalks.Single(s => s.Id == "sw_partial_0");
            Assert.Null(partial.ShapeZ);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
