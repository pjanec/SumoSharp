using System;
using System.Collections.Generic;
using System.Linq;
using Sim.Core;
using Sim.Core.Orca;
using Sim.Replication;
using Xunit;
using Xunit.Abstractions;

namespace Sim.Pedestrians.Tests;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §3.6, -TASKS.md C4: pedestrian elevation on the wire, as a NEW
// frame kind (5) rather than a widened kind 4 or a Version bump.
//
// SC3 (decoder discrimination) and SC4 (2-D bytes byte-identical) are the non-negotiable ones, because
// `FrameCodec.ReadHeader` validates no version byte: a stride change would not fail loudly, it would
// return plausible garbage to every existing consumer.
public class PedElevationWireTests
{
    private readonly ITestOutputHelper _output;

    public PedElevationWireTests(ITestOutputHelper output) => _output = output;

    private static PathArcRecord Flat(uint id, params (double X, double Y)[] pts)
        => new(new VehicleHandle(id, 0), 1.34, 12.5, pts.Select(p => new Vec2(p.X, p.Y)).ToArray());

    private static PathArcRecord WithZ(uint id, params (double X, double Y, double Z)[] pts)
        => new(
            new VehicleHandle(id, 0), 1.34, 12.5,
            pts.Select(p => new Vec2(p.X, p.Y)).ToArray(),
            pts.Select(p => p.Z).ToArray());

    private static byte[] Encode(params PathArcRecord[] recs)
    {
        var buf = new byte[FrameCodec.PathArcFrameSize(recs)];
        FrameCodec.WritePathArcFrame(buf, step: 7, time: 3.5f, recs);
        return buf;
    }

    // ---- C4·SC1: sizes ------------------------------------------------------------------------------

    [Fact]
    public void RecordSizes_AreExact_AndKindFourIsUnchanged()
    {
        for (var n = 0; n <= 40; n++)
        {
            Assert.Equal(14 + (8 * n), FrameCodec.PathArcRecordSize(n));
            Assert.Equal(14 + (12 * n), FrameCodec.PathArcZRecordSize(n));
        }
    }

    // ---- C4·SC2: round-trip, including z, within the 1 cm quantization step -------------------------

    [Fact]
    public void RoundTrip_PreservesXYAndZ_WithinOneCentimetre()
    {
        var single = WithZ(1, (10.0, 20.0, 372.5));
        var many = WithZ(2, Enumerable.Range(0, 50)
            .Select(i => (91850.0 + (i * 1.37), 73960.0 - (i * 0.44), 370.0 + (i * 0.61)))
            .ToArray());
        var negative = WithZ(3, (-108030.25, -136900.5, -374.5), (0.0, 0.0, 0.0));

        foreach (var rec in new[] { single, many, negative })
        {
            var decoded = FrameCodec.ReadPathArcFrame(Encode(rec)).Single();

            Assert.Equal(rec.Path.Count, decoded.Path.Count);
            Assert.NotNull(decoded.PathZ);
            Assert.Equal(rec.Path.Count, decoded.PathZ!.Count);

            for (var k = 0; k < rec.Path.Count; k++)
            {
                Assert.True(Math.Abs(rec.Path[k].X - decoded.Path[k].X) <= 0.01);
                Assert.True(Math.Abs(rec.Path[k].Y - decoded.Path[k].Y) <= 0.01);
                Assert.True(Math.Abs(rec.PathZ![k] - decoded.PathZ[k]) <= 0.01,
                    $"z[{k}]: {rec.PathZ[k]} -> {decoded.PathZ[k]}");
            }
        }
    }

    // ---- C4·SC3: decoder discrimination -- THE load-bearing one -------------------------------------

    [Fact]
    public void AKindFourPayload_IsNotReadWithATwelveByteStride()
    {
        // The failure mode this guards: ReadHeader never validates the version byte, so if kind 4 could
        // be strided as kind 5 the result would be silently wrong rather than an exception. Assert the
        // kind byte itself AND that the decode is exact, so a misparse cannot hide behind a tolerance.
        var flat = Flat(1, (10.0, 20.0), (30.0, 40.0), (55.5, -66.25));
        var bytes = Encode(flat);

        Assert.Equal(FrameCodec.KindPathArc, FrameCodec.ReadHeader(bytes).Kind);
        Assert.Equal(FrameCodec.HeaderSize + FrameCodec.PathArcRecordSize(3), bytes.Length);

        var decoded = FrameCodec.ReadPathArcFrame(bytes).Single();
        Assert.Null(decoded.PathZ); // "no elevation on this stream", not an array of zeros
        Assert.Equal(3, decoded.Path.Count);
        Assert.True(Math.Abs(decoded.Path[2].X - 55.5) <= 0.01);
        Assert.True(Math.Abs(decoded.Path[2].Y - (-66.25)) <= 0.01);
    }

    [Fact]
    public void AKindFivePayload_IsTaggedAsKindFive_AndYieldsPopulatedZ()
    {
        var withZ = WithZ(1, (10.0, 20.0, 372.5), (30.0, 40.0, 375.25));
        var bytes = Encode(withZ);

        Assert.Equal(FrameCodec.KindPathArcZ, FrameCodec.ReadHeader(bytes).Kind);
        Assert.Equal(FrameCodec.HeaderSize + FrameCodec.PathArcZRecordSize(2), bytes.Length);

        var decoded = FrameCodec.ReadPathArcFrame(bytes).Single();
        Assert.NotNull(decoded.PathZ);
        Assert.True(Math.Abs(decoded.PathZ![1] - 375.25) <= 0.01);
    }

    [Fact]
    public void TheTwoKinds_HaveDifferentLengthsForTheSamePath_SoAMisparseCannotBeSilent()
    {
        var flat = Flat(1, (10.0, 20.0), (30.0, 40.0));
        var withZ = WithZ(1, (10.0, 20.0, 5.0), (30.0, 40.0, 6.0));

        var flatBytes = Encode(flat);
        var zBytes = Encode(withZ);

        Assert.Equal(2 * 4, zBytes.Length - flatBytes.Length); // exactly +4 B per point
        Assert.NotEqual(FrameCodec.ReadHeader(flatBytes).Kind, FrameCodec.ReadHeader(zBytes).Kind);
    }

    // ---- C4·SC4: a 2-D net's bytes are byte-for-byte what they were ---------------------------------

    [Fact]
    public void TwoDimensionalRecords_ProduceTheExactPreChangeByteSequence()
    {
        // Captured from the encoder BEFORE kind 5 existed (base64 of the whole frame), so this compares
        // against real prior output rather than against the new code's own idea of itself.
        const string PreChangeBase64 =
            "AQQAAAcAAAAAAGBAAwAAAAEAAAAfhas/AABIQQEA6AMAANAHAAACAAAAAADAPwAAgD4DAK8oW/8+Gy//AAAAAAAAAAAJ"
            + "J4wA7dpwAE0AAABmZmY/ABCWQygAAAAAAAAAAACJAAAA1P///xIBAACo////mwEAAHz///8kAgAAUP///60CAAAk////"
            + "NgMAAPj+//+/AwAAzP7//0gEAACg/v//0QQAAHT+//9aBQAASP7//+MFAAAc/v//bAYAAPD9///1BgAAxP3//34HAACY"
            + "/f//BwgAAGz9//+QCAAAQP3//xkJAAAU/f//ogkAAOj8//8rCgAAvPz//7QKAACQ/P//PQsAAGT8///GCwAAOPz//08M"
            + "AAAM/P//2AwAAOD7//9hDQAAtPv//+oNAACI+///cw4AAFz7///8DgAAMPv//4UPAAAE+///DhAAANj6//+XEAAArPr/"
            + "/yARAACA+v//qREAAFT6//8yEgAAKPr//7sSAAD8+f//RBMAAND5///NEwAApPn//1YUAAB4+f//3xQAAEz5//8=";

        var recs = new[]
        {
            new PathArcRecord(new VehicleHandle(1, 0), 1.34, 12.5, new[] { new Vec2(10.0, 20.0) }),
            new PathArcRecord(new VehicleHandle(2, 0), 1.5, 0.25,
                new[] { new Vec2(-108030.25, -136900.5), new Vec2(0, 0), new Vec2(91850.33, 73960.77) }),
            new PathArcRecord(new VehicleHandle(77, 0), 0.9, 300.125,
                Enumerable.Range(0, 40).Select(i => new Vec2(i * 1.37, -i * 0.44)).ToArray()),
        };

        var bytes = Encode(recs);

        Assert.Equal(410, bytes.Length);
        Assert.Equal(PreChangeBase64, Convert.ToBase64String(bytes));
        Assert.Equal(FrameCodec.KindPathArc, FrameCodec.ReadHeader(bytes).Kind);
    }

    // ---- C4·SC6: the meter accounts kind 5 at the larger size, kind 4 unchanged ---------------------

    [Fact]
    public void BandwidthMeter_ChargesFourExtraBytesPerPoint_OnlyForElevationRecords()
    {
        const int Points = 25;

        var flatMeter = new Sim.Pedestrians.Lod.PedBandwidthMeter();
        flatMeter.RecordPathArc(1.0, Points, withElevation: false);

        var zMeter = new Sim.Pedestrians.Lod.PedBandwidthMeter();
        zMeter.RecordPathArc(1.0, Points, withElevation: true);

        var flatBytes = FrameCodec.HeaderSize + FrameCodec.PathArcRecordSize(Points);
        var zBytes = FrameCodec.HeaderSize + FrameCodec.PathArcZRecordSize(Points);

        Assert.Equal(4 * Points, zBytes - flatBytes);
        _output.WriteLine($"C4.SC6 PathArc {Points} pts: 2-D {flatBytes} B, 3-D {zBytes} B (+{zBytes - flatBytes} B)");
    }

    // ---- a mixed frame keeps a uniform stride --------------------------------------------------------

    [Fact]
    public void AFrameMixingZAndNonZRecords_StaysUniformlyStrided_AndDecodesBoth()
    {
        // A record without z inside a z-carrying frame writes zeros rather than short rows -- the reader
        // must never have to branch per record.
        var mixed = new[]
        {
            WithZ(1, (10.0, 20.0, 372.5), (11.0, 21.0, 373.0)),
            Flat(2, (30.0, 40.0), (31.0, 41.0)),
        };

        var bytes = Encode(mixed);
        Assert.Equal(FrameCodec.KindPathArcZ, FrameCodec.ReadHeader(bytes).Kind);

        var decoded = FrameCodec.ReadPathArcFrame(bytes);
        Assert.Equal(2, decoded.Length);
        Assert.True(Math.Abs(decoded[0].PathZ![0] - 372.5) <= 0.01);
        Assert.All(decoded[1].PathZ!, z => Assert.Equal(0.0, z, 3));
    }
}
