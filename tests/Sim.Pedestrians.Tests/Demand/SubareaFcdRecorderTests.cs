using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests.Demand;

// P8-3xP8-4 recorder (docs/COORDINATION-pedestrian-x-subarea.md §3; PEDESTRIAN-P8-BACKLOG.md): the end-to-end
// person-FCD stream that drives the committed box with the weighted demand (P8-3) sized by the density knob
// (P8-4a). Proves the emitted stream is well-formed SUMO FCD (`<fcd-export>`/`<timestep>`/`<person>`), every
// person row carries the geometry attributes a viz needs and sits inside the box, the crowd matches the
// dialed cap, and the whole recording is byte-for-byte deterministic (the shared-replay contract needs a
// reproducible stream).
public class SubareaFcdRecorderTests
{
    private readonly ITestOutputHelper _out;

    public SubareaFcdRecorderTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Traffic.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string BoxDir() => Path.Combine(RepoRoot(), "scenarios", "_ped", "subarea-box");

    private static (string Xml, SubareaFcdRecorder.Result Result) Record(SubareaFcdRecorder.Options opt)
    {
        var sb = new StringBuilder();
        SubareaFcdRecorder.Result result;
        using (var sw = new StringWriter(sb))
        using (var writer = new PersonFcdWriter(sw))
        {
            result = SubareaFcdRecorder.Record(BoxDir(), writer, opt);
        }

        return (sb.ToString(), result);
    }

    [Fact]
    public void EmitsWellFormedPersonFcd_InsideTheBox_MatchingTheDialedCap()
    {
        var opt = new SubareaFcdRecorder.Options { Dial = 0.05, Seconds = 40.0, Dt = 0.1 };
        var (xml, result) = Record(opt);

        var doc = XDocument.Parse(xml); // well-formed
        var root = doc.Root!;
        Assert.Equal("fcd-export", root.Name.LocalName);

        var timesteps = root.Elements("timestep").ToList();
        Assert.Equal(result.Frames, timesteps.Count);
        Assert.True(result.PopulationCap > 0, "the dialed cap should be positive");

        // Timesteps advance by dt (first frame is at t = dt).
        Assert.Equal(opt.Dt, double.Parse(timesteps[0].Attribute("time")!.Value, CultureInfo.InvariantCulture), precision: 9);

        var totalPersonRows = 0;
        var maxConcurrent = 0;
        foreach (var ts in timesteps)
        {
            var persons = ts.Elements("person").ToList();
            maxConcurrent = Math.Max(maxConcurrent, persons.Count);
            Assert.True(persons.Count <= result.PopulationCap, $"a frame had {persons.Count} persons over cap {result.PopulationCap}");

            foreach (var p in persons)
            {
                totalPersonRows++;
                // Every geometry attribute the viz reads is present and numeric.
                foreach (var attr in new[] { "x", "y", "angle", "speed" })
                {
                    Assert.True(double.TryParse(p.Attribute(attr)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                        $"person missing/invalid {attr}");
                }

                Assert.StartsWith("p", p.Attribute("id")!.Value); // stable ped id
                Assert.Equal("ped", p.Attribute("type")!.Value);

                var x = double.Parse(p.Attribute("x")!.Value, CultureInfo.InvariantCulture);
                var y = double.Parse(p.Attribute("y")!.Value, CultureInfo.InvariantCulture);
                Assert.InRange(x, -5.0, 805.0); // inside the 0,0 -> 800,800 box (small tolerance)
                Assert.InRange(y, -5.0, 805.0);
            }
        }

        Assert.True(totalPersonRows > 0, "recorder produced no person rows");
        Assert.Equal(result.PeakLive, maxConcurrent);
        Assert.True(result.Spawns > 0);

        _out.WriteLine($"[P8-3x4] fcd: frames={result.Frames} cap={result.PopulationCap} peak={result.PeakLive} " +
                       $"rows={totalPersonRows} spawns={result.Spawns} arrivals={result.Arrivals} " +
                       $"walkableKm={result.WalkableLengthKm:F3} endpoints={result.Endpoints}");
    }

    [Fact]
    public void Recording_IsByteForByteDeterministic()
    {
        var opt = new SubareaFcdRecorder.Options { Dial = 0.05, Seconds = 30.0, Dt = 0.1 };
        var (xml1, r1) = Record(opt);
        var (xml2, r2) = Record(opt);

        Assert.Equal(xml1, xml2);                 // byte-identical stream
        Assert.Equal(r1, r2);                      // and identical stats
        _out.WriteLine($"[P8-3x4] deterministic: {xml1.Length} chars identical across two recordings");
    }

    [Fact]
    public void Dial_ScalesTheRecordedCrowd()
    {
        var (_, low) = Record(new SubareaFcdRecorder.Options { Dial = 0.02, Seconds = 40.0 });
        var (_, high) = Record(new SubareaFcdRecorder.Options { Dial = 0.08, Seconds = 40.0 });

        Assert.True(high.PopulationCap > low.PopulationCap, "a higher dial should raise the cap");
        Assert.True(high.PeakLive >= low.PeakLive, "a higher dial should not shrink the recorded crowd");
        _out.WriteLine($"[P8-3x4] dial scaling: low cap={low.PopulationCap} peak={low.PeakLive}; " +
                       $"high cap={high.PopulationCap} peak={high.PeakLive}");
    }
}
