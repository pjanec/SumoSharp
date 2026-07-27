using System.Buffers.Binary;
using Sim.Core;
using Sim.Core.Orca;

namespace Sim.Replication;

// SUMOSHARP-DEADRECKONING.md §4.3 — the CANONICAL packed wire format for a replication frame. One
// little-endian byte layout is the single source of truth: it is sent verbatim over TCP/UDP and rides DDS
// as an opaque blob payload (SumoSharp.Replication.Dds), so one codec serves every transport. Fully
// allocation-free (writes into / reads from caller spans); deterministic; netstandard2.1-clean
// (BinaryPrimitives + BitConverter bit-casts, no net-only APIs).
//
// Header (16 B): version(1) kind(1) reserved(2) step(u32) time(f32) count(u32).
// VehicleRecord (48 B): index(u32) gen(u16) model(u8) pad(u8) laneHandle(i32)
//                       pos/posLat/speed/accel/latSpeed(5*f32) upcoming[4](4*i32).
// CrowdRecord (32 B): index(u32) gen(u16) pad(u16) x/y/z/vx/vy/radius(6*f32).
//
// POC-7b additions (docs/PEDESTRIAN-POC-PLAN.md POC-7; docs/PEDESTRIAN-DESIGN.md §7) — additive, new
// KIND values, existing kinds/layouts above are byte-for-byte unchanged:
//
// PedFreeKinematicRecord (18 B, QUANTIZED): handle-index(u32) x_cm(i32) y_cm(i32) vx_cmps(i16)
//   vy_cmps(i16) radius_cm(u16). Position tradeoff (chosen: int32-cm absolute):
//     - int32 cm absolute (chosen): 8 B for (x,y), range +-21,474,836 m (>> any realistic world extent,
//       no per-frame/chunk origin bookkeeping, no "ped wandered out of chunk range" failure mode).
//       ~1 cm precision (round-to-nearest-cm on write).
//     - int16 cm relative to a per-frame/chunk origin (NOT chosen): would shave the record to ~14 B, but
//       needs a maintained origin per frame/spatial-chunk and silently clamps/wraps if a ped strays
//       further than +-327.67 m from that origin -- real risk for a population spread across a city-scale
//       net. Left as a documented follow-up if the extra ~2 B/record/step ever matters (it does not at
//       the measured scale here, see docs/PEDESTRIAN-POC7B-FINDINGS.md).
//   `Radius` IS still carried per record here (unlike the car stack's "physical dims sent once" pattern)
//   because the task spec calls for it in this compact record; it is small (2 B) and rarely changes, so
//   the cost is negligible. `Handle` carries only the u32 index (the generation is NOT on this hot-path
//   wire record -- it is authoritative on the separate lifecycle topic which already keys by full handle;
//   dropping it here is a deliberate size/robustness tradeoff for the high-rate stream).
//
// PathArcRecord (variable, 14 B + 8 B/point): handle-index(u32) speed_f32(4) startTime_f32(4)
//   pointCount(u16) points[pointCount] * (x_cm(i32) y_cm(i32)). Sent ONCE per ped (lifecycle topic), so
//   its per-step amortized cost is what matters, not its raw size; see the frame-size helper below.
public static class FrameCodec
{
    public const byte Version = 1;
    public const byte KindVehicle = 1;
    public const byte KindCrowd = 2;
    public const byte KindPedFreeKinematic = 3;
    public const byte KindPathArc = 4;

    // C4 (docs/EXTERNAL-NET-LOADING-DESIGN.md §3.6): the z-carrying PathArc frame. A NEW KIND rather
    // than a wider kind 4 or a `Version` bump, for two measured reasons:
    //
    //   1. `ReadHeader` reads the version byte but NEVER VALIDATES it, so widening kind 4's point stride
    //      would make every previously-written payload silently misparse into garbage instead of failing
    //      loudly. A distinct kind byte makes the two structurally distinguishable and lets one reader
    //      accept both.
    //   2. `Version` is global across all four frame kinds, so bumping it to signal a PathArc change
    //      would force lockstep upgrades of the vehicle, crowd and ped-kinematic frames, which have
    //      nothing to do with elevation.
    //
    // Kind 4 is UNCHANGED, and the publisher still emits kind 4 whenever there is no elevation, so a
    // 2-D net's bytes are byte-for-byte identical to before this existed.
    public const byte KindPathArcZ = 5;

    public const int HeaderSize = 16;
    public const int VehicleRecordSize = 48;
    public const int CrowdRecordSize = 32;
    public const int PedFreeKinematicRecordSize = 18;

    // cm-precision quantization scale shared by the ped wire records.
    private const double CmScale = 100.0;

    public static int VehicleFrameSize(int count) => HeaderSize + count * VehicleRecordSize;
    public static int CrowdFrameSize(int count) => HeaderSize + count * CrowdRecordSize;
    public static int PedFreeKinematicFrameSize(int count) => HeaderSize + count * PedFreeKinematicRecordSize;

    public readonly struct FrameHeader
    {
        public FrameHeader(byte version, byte kind, uint step, float time, int count)
        {
            Version = version; Kind = kind; Step = step; Time = time; Count = count;
        }

        public byte Version { get; }
        public byte Kind { get; }
        public uint Step { get; }
        public float Time { get; }
        public int Count { get; }
    }

    public static FrameHeader ReadHeader(ReadOnlySpan<byte> src)
    {
        if (src.Length < HeaderSize) throw new ArgumentException("frame shorter than header.", nameof(src));
        var version = src[0];
        var kind = src[1];
        // src[2..4] reserved
        var step = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
        var time = ReadF32(src.Slice(8, 4));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12, 4));
        return new FrameHeader(version, kind, step, time, count);
    }

    // --- Vehicle frame ---

    public static int WriteVehicleFrame(Span<byte> dst, uint step, float time, ReadOnlySpan<VehicleRecord> recs)
    {
        var size = VehicleFrameSize(recs.Length);
        if (dst.Length < size) throw new ArgumentException("destination too small for the vehicle frame.", nameof(dst));

        WriteHeader(dst, KindVehicle, step, time, recs.Length);
        var o = HeaderSize;
        for (var i = 0; i < recs.Length; i++)
        {
            ref readonly var r = ref recs[i];
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(o, 4), r.Handle.Index); o += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(o, 2), r.Handle.Generation); o += 2;
            dst[o++] = (byte)r.Model;
            dst[o++] = 0; // pad
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o, 4), r.LaneHandle); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Pos); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.PosLat); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Speed); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Accel); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.LatSpeed); o += 4;
            for (var k = 0; k < UpcomingLanes.Count; k++) { BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o, 4), r.Upcoming[k]); o += 4; }
        }

        return size;
    }

    // Reads up to dst.Length records; returns the number read (== min(header.Count, dst.Length)).
    public static int ReadVehicleFrame(ReadOnlySpan<byte> src, Span<VehicleRecord> dst)
    {
        var h = ReadHeader(src);
        if (h.Kind != KindVehicle) throw new ArgumentException("not a vehicle frame.", nameof(src));
        var n = Math.Min(h.Count, dst.Length);
        var o = HeaderSize;
        Span<int> up = stackalloc int[UpcomingLanes.Count];
        for (var i = 0; i < n; i++)
        {
            var index = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(o, 4)); o += 4;
            var gen = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(o, 2)); o += 2;
            var model = (DrModel)src[o++];
            o++; // pad
            var lane = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(o, 4)); o += 4;
            var pos = ReadF32(src.Slice(o, 4)); o += 4;
            var posLat = ReadF32(src.Slice(o, 4)); o += 4;
            var speed = ReadF32(src.Slice(o, 4)); o += 4;
            var accel = ReadF32(src.Slice(o, 4)); o += 4;
            var latSpeed = ReadF32(src.Slice(o, 4)); o += 4;
            for (var k = 0; k < UpcomingLanes.Count; k++) { up[k] = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(o, 4)); o += 4; }
            dst[i] = new VehicleRecord(new VehicleHandle(index, gen), model, lane, pos, posLat, speed, accel, latSpeed, new UpcomingLanes(up));
        }

        return n;
    }

    // --- Crowd frame ---

    public static int WriteCrowdFrame(Span<byte> dst, uint step, float time, ReadOnlySpan<CrowdRecord> recs)
    {
        var size = CrowdFrameSize(recs.Length);
        if (dst.Length < size) throw new ArgumentException("destination too small for the crowd frame.", nameof(dst));

        WriteHeader(dst, KindCrowd, step, time, recs.Length);
        var o = HeaderSize;
        for (var i = 0; i < recs.Length; i++)
        {
            ref readonly var r = ref recs[i];
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(o, 4), r.Handle.Index); o += 4;
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(o, 2), r.Handle.Generation); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(o, 2), 0); o += 2; // pad
            WriteF32(dst.Slice(o, 4), (float)r.X); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Y); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Z); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Vx); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Vy); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Radius); o += 4;
        }

        return size;
    }

    public static int ReadCrowdFrame(ReadOnlySpan<byte> src, Span<CrowdRecord> dst)
    {
        var h = ReadHeader(src);
        if (h.Kind != KindCrowd) throw new ArgumentException("not a crowd frame.", nameof(src));
        var n = Math.Min(h.Count, dst.Length);
        var o = HeaderSize;
        for (var i = 0; i < n; i++)
        {
            var index = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(o, 4)); o += 4;
            var gen = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(o, 2)); o += 2;
            o += 2; // pad
            var x = ReadF32(src.Slice(o, 4)); o += 4;
            var y = ReadF32(src.Slice(o, 4)); o += 4;
            var z = ReadF32(src.Slice(o, 4)); o += 4;
            var vx = ReadF32(src.Slice(o, 4)); o += 4;
            var vy = ReadF32(src.Slice(o, 4)); o += 4;
            var radius = ReadF32(src.Slice(o, 4)); o += 4;
            dst[i] = new CrowdRecord(new VehicleHandle(index, gen), x, y, z, vx, vy, radius);
        }

        return n;
    }

    // --- Ped FreeKinematic frame (POC-7b, quantized) ---

    public static int WritePedFreeKinematicFrame(Span<byte> dst, uint step, float time, ReadOnlySpan<PedFreeKinematicRecord> recs)
    {
        var size = PedFreeKinematicFrameSize(recs.Length);
        if (dst.Length < size) throw new ArgumentException("destination too small for the ped free-kinematic frame.", nameof(dst));

        WriteHeader(dst, KindPedFreeKinematic, step, time, recs.Length);
        var o = HeaderSize;
        for (var i = 0; i < recs.Length; i++)
        {
            ref readonly var r = ref recs[i];
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(o, 4), r.Handle.Index); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o, 4), QuantizeCm32(r.X)); o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o, 4), QuantizeCm32(r.Y)); o += 4;
            BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(o, 2), QuantizeCmPerS16(r.Vx)); o += 2;
            BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(o, 2), QuantizeCmPerS16(r.Vy)); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(o, 2), QuantizeCm16(r.Radius)); o += 2;
        }

        return size;
    }

    public static int ReadPedFreeKinematicFrame(ReadOnlySpan<byte> src, Span<PedFreeKinematicRecord> dst)
    {
        var h = ReadHeader(src);
        if (h.Kind != KindPedFreeKinematic) throw new ArgumentException("not a ped free-kinematic frame.", nameof(src));
        var n = Math.Min(h.Count, dst.Length);
        var o = HeaderSize;
        for (var i = 0; i < n; i++)
        {
            var index = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(o, 4)); o += 4;
            var x = DequantizeCm32(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(o, 4))); o += 4;
            var y = DequantizeCm32(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(o, 4))); o += 4;
            var vx = DequantizeCmPerS16(BinaryPrimitives.ReadInt16LittleEndian(src.Slice(o, 2))); o += 2;
            var vy = DequantizeCmPerS16(BinaryPrimitives.ReadInt16LittleEndian(src.Slice(o, 2))); o += 2;
            var radius = DequantizeCm16(BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(o, 2))); o += 2;
            // Generation is not carried on this wire record (see header comment) -- decoded handles use
            // generation 0; a consumer that needs the authoritative generation resolves it from the
            // lifecycle topic keyed by the same index.
            dst[i] = new PedFreeKinematicRecord(new VehicleHandle(index, 0), x, y, vx, vy, radius);
        }

        return n;
    }

    // --- PathArc frame (POC-7b, sent once per ped on the low-rate/durable topic) ---

    // Variable-length record: 4 (handle index) + 4 (speed f32) + 4 (startTime f32) + 2 (point count)
    // + 8 * pointCount (x_cm i32, y_cm i32 per point).
    public static int PathArcRecordSize(int pointCount) => 14 + pointCount * 8;

    // C4: the kind-5 record -- the IDENTICAL 14-byte header, then 12 B/point (x_cm, y_cm, z_cm), with z
    // using the same int32-centimetre quantization as x and y (so it inherits the same ~1 cm precision
    // and +-21474 km range; Swiss elevations of 199-1634 m sit trivially inside it). No new quantization
    // scheme and no origin bookkeeping.
    public static int PathArcZRecordSize(int pointCount) => 14 + pointCount * 12;

    // True iff any record carries elevation. Decides the frame KIND: all-or-nothing per frame, so a
    // reader never has to ask per record.
    private static bool AnyElevation(ReadOnlySpan<PathArcRecord> recs)
    {
        for (var i = 0; i < recs.Length; i++)
        {
            if (recs[i].PathZ is { Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    public static int PathArcFrameSize(ReadOnlySpan<PathArcRecord> recs)
    {
        var withZ = AnyElevation(recs);
        var size = HeaderSize;
        for (var i = 0; i < recs.Length; i++)
        {
            size += withZ ? PathArcZRecordSize(recs[i].Path.Count) : PathArcRecordSize(recs[i].Path.Count);
        }

        return size;
    }

    public static int WritePathArcFrame(Span<byte> dst, uint step, float time, ReadOnlySpan<PathArcRecord> recs)
    {
        var size = PathArcFrameSize(recs);
        if (dst.Length < size) throw new ArgumentException("destination too small for the path-arc frame.", nameof(dst));

        // Kind 4 unless SOMETHING in this frame has elevation. On a 2-D net this branch is never taken
        // and every byte below is written exactly as it was before kind 5 existed.
        var withZ = AnyElevation(recs);
        WriteHeader(dst, withZ ? KindPathArcZ : KindPathArc, step, time, recs.Length);
        var o = HeaderSize;
        for (var i = 0; i < recs.Length; i++)
        {
            ref readonly var r = ref recs[i];
            BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(o, 4), r.Handle.Index); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.Speed); o += 4;
            WriteF32(dst.Slice(o, 4), (float)r.StartTime); o += 4;
            var path = r.Path;
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(o, 2), (ushort)path.Count); o += 2;

            // A record with no z inside a z-carrying frame writes zeros, keeping the frame's stride
            // uniform -- the reader must never have to branch per record.
            var pathZ = r.PathZ;
            for (var k = 0; k < path.Count; k++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o, 4), QuantizeCm32(path[k].X)); o += 4;
                BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o, 4), QuantizeCm32(path[k].Y)); o += 4;
                if (withZ)
                {
                    var z = pathZ is not null && k < pathZ.Count ? pathZ[k] : 0.0;
                    BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o, 4), QuantizeCm32(z)); o += 4;
                }
            }
        }

        return size;
    }

    // Allocates: unlike the fixed-size frames above, a PathArc record's point count is only known once
    // decoded, so (unlike the alloc-free fixed-record reads) this allocates one array per record plus the
    // result array. Acceptable here: PathArc decoding happens once per ped's path lifetime (spawn/
    // demotion), never per step.
    public static PathArcRecord[] ReadPathArcFrame(ReadOnlySpan<byte> src)
    {
        var h = ReadHeader(src);

        // Accepts BOTH kinds and strides by what the kind byte says, never by what the caller hopes.
        // This is the discrimination the design calls load-bearing: because ReadHeader validates no
        // version byte, reading a kind-4 payload with a 12-byte stride would not fail, it would return
        // plausible garbage.
        var withZ = h.Kind == KindPathArcZ;
        if (h.Kind != KindPathArc && !withZ)
        {
            throw new ArgumentException("not a path-arc frame.", nameof(src));
        }

        var result = new PathArcRecord[h.Count];
        var o = HeaderSize;
        for (var i = 0; i < h.Count; i++)
        {
            var index = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(o, 4)); o += 4;
            var speed = ReadF32(src.Slice(o, 4)); o += 4;
            var startTime = ReadF32(src.Slice(o, 4)); o += 4;
            var pointCount = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(o, 2)); o += 2;
            var points = new Vec2[pointCount];
            var elevations = withZ ? new double[pointCount] : null;
            for (var k = 0; k < pointCount; k++)
            {
                var x = DequantizeCm32(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(o, 4))); o += 4;
                var y = DequantizeCm32(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(o, 4))); o += 4;
                points[k] = new Vec2(x, y);
                if (withZ)
                {
                    elevations![k] = DequantizeCm32(BinaryPrimitives.ReadInt32LittleEndian(src.Slice(o, 4))); o += 4;
                }
            }

            // A kind-4 frame yields PathZ == null -- "this stream carries no elevation" -- rather than
            // an array of zeros a consumer could mistake for sea level.
            result[i] = new PathArcRecord(new VehicleHandle(index, 0), speed, startTime, points, elevations);
        }

        return result;
    }

    // --- cm-precision quantization helpers (POC-7b) ---

    private static int QuantizeCm32(double meters) =>
        (int)Math.Round(Math.Clamp(meters * CmScale, int.MinValue, int.MaxValue), MidpointRounding.AwayFromZero);

    private static double DequantizeCm32(int q) => q / CmScale;

    private static short QuantizeCmPerS16(double metersPerSecond) =>
        (short)Math.Round(Math.Clamp(metersPerSecond * CmScale, short.MinValue, short.MaxValue), MidpointRounding.AwayFromZero);

    private static double DequantizeCmPerS16(short q) => q / CmScale;

    private static ushort QuantizeCm16(double meters) =>
        (ushort)Math.Round(Math.Clamp(meters * CmScale, 0, ushort.MaxValue), MidpointRounding.AwayFromZero);

    private static double DequantizeCm16(ushort q) => q / CmScale;

    private static void WriteHeader(Span<byte> dst, byte kind, uint step, float time, int count)
    {
        dst[0] = Version;
        dst[1] = kind;
        dst[2] = 0; dst[3] = 0; // reserved
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), step);
        WriteF32(dst.Slice(8, 4), time);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(12, 4), (uint)count);
    }

    // float <-> LE bytes via int bits (BinaryPrimitives.Write/ReadSingleLittleEndian is net5+, absent on ns2.1).
    private static void WriteF32(Span<byte> dst, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(dst, BitConverter.SingleToInt32Bits(value));

    private static float ReadF32(ReadOnlySpan<byte> src) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(src));
}
