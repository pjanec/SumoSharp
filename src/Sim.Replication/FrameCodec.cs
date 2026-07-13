using System.Buffers.Binary;
using Sim.Core;

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
public static class FrameCodec
{
    public const byte Version = 1;
    public const byte KindVehicle = 1;
    public const byte KindCrowd = 2;

    public const int HeaderSize = 16;
    public const int VehicleRecordSize = 48;
    public const int CrowdRecordSize = 32;

    public static int VehicleFrameSize(int count) => HeaderSize + count * VehicleRecordSize;
    public static int CrowdFrameSize(int count) => HeaderSize + count * CrowdRecordSize;

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
