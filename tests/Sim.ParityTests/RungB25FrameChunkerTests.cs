using Sim.Core;
using Sim.Replication;
using Xunit;

namespace Sim.ParityTests;

// SUMOSHARP-DEADRECKONING.md §4.3 — FrameChunker splits one tick's records into fixed-cap chunks for a
// transport that bounds a single sample (the DDS opaque-blob topic by byte budget, the structured-batch
// topic by sample count). Pure arithmetic + an end-to-end that composes chunking with FrameCodec to carry a
// >1-chunk frame and reassemble it byte-for-byte. No external deps -> hermetic. (The DDS types that consume
// this live in SumoSharp.Replication.Dds, which is out of Traffic.sln and cannot run here.)
public class RungB25FrameChunkerTests
{
    [Fact]
    public void MaxRecordsForPayload_AccountsForHeader_AndFloorsToWholeRecords()
    {
        // 64 KiB blob, 48 B vehicle records: (65536 - 16 header) / 48 = 1365.
        Assert.Equal(1365, FrameChunker.MaxRecordsForPayload(64 * 1024, FrameCodec.VehicleRecordSize));
        // Exactly header + 2 records.
        Assert.Equal(2, FrameChunker.MaxRecordsForPayload(FrameCodec.HeaderSize + 2 * FrameCodec.VehicleRecordSize, FrameCodec.VehicleRecordSize));
        // A budget that cannot hold the header (or header but no record) -> 0.
        Assert.Equal(0, FrameChunker.MaxRecordsForPayload(FrameCodec.HeaderSize, FrameCodec.VehicleRecordSize));
        Assert.Equal(0, FrameChunker.MaxRecordsForPayload(8, FrameCodec.VehicleRecordSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameChunker.MaxRecordsForPayload(1024, 0));
    }

    [Theory]
    [InlineData(0, 256, 0)]     // no records -> no chunks (liveness is on the lifecycle topic, not an empty batch)
    [InlineData(1, 256, 1)]
    [InlineData(256, 256, 1)]   // exactly one full chunk
    [InlineData(257, 256, 2)]
    [InlineData(1000, 256, 4)]  // 256+256+256+232
    public void ChunkCount_CeilDivides(int recordCount, int maxPerChunk, int expected)
    {
        Assert.Equal(expected, FrameChunker.ChunkCount(recordCount, maxPerChunk));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameChunker.ChunkCount(recordCount, 0));
    }

    [Fact]
    public void ChunkRange_CoversEveryRecord_WithRemainderLast_AndRejectsOutOfRange()
    {
        const int total = 1000, cap = 256;
        var seen = 0;
        var chunks = FrameChunker.ChunkCount(total, cap);
        for (var c = 0; c < chunks; c++)
        {
            var (start, count) = FrameChunker.ChunkRange(c, total, cap);
            Assert.Equal(seen, start);                       // chunks are contiguous, in order
            Assert.True(count is > 0 and <= cap);
            if (c < chunks - 1) Assert.Equal(cap, count);     // only the last chunk is short
            seen += count;
        }

        Assert.Equal(total, seen);                            // every record covered exactly once
        Assert.Equal(232, FrameChunker.ChunkRange(chunks - 1, total, cap).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameChunker.ChunkRange(chunks, total, cap));
    }

    // End-to-end: a frame too big for one 64 KiB blob is split, each chunk packed by FrameCodec, then read
    // back and reassembled to the exact original records. This is how the DDS blob topic carries 10k+ movers.
    [Fact]
    public void ChunkedBlobRoundTrip_ReassemblesLargeFrame()
    {
        const int n = 3000; // > 1365 (one 64 KiB chunk) -> forces 3 chunks
        var recs = new VehicleRecord[n];
        for (var i = 0; i < n; i++)
        {
            recs[i] = new VehicleRecord(
                new VehicleHandle((uint)i, (ushort)(i % 7)), DrModel.LaneArc, i * 2,
                pos: i * 0.5, posLat: 0.0, speed: (i % 30) * 1.0, accel: 0.0, latSpeed: 0.0,
                new UpcomingLanes(stackalloc int[] { i * 2, i * 2 + 1 }));
        }

        var maxPerChunk = FrameChunker.MaxRecordsForPayload(64 * 1024, FrameCodec.VehicleRecordSize);
        var chunks = FrameChunker.ChunkCount(n, maxPerChunk);
        Assert.Equal(3, chunks);

        var reassembled = new VehicleRecord[n];
        var got = 0;
        var blob = new byte[64 * 1024];
        for (var c = 0; c < chunks; c++)
        {
            var (start, count) = FrameChunker.ChunkRange(c, n, maxPerChunk);
            var written = FrameCodec.WriteVehicleFrame(blob, step: 5, time: 5.0f, recs.AsSpan(start, count));
            Assert.True(written <= blob.Length);

            var outp = new VehicleRecord[count];
            Assert.Equal(count, FrameCodec.ReadVehicleFrame(blob, outp));
            outp.CopyTo(reassembled.AsSpan(got));
            got += count;
        }

        Assert.Equal(n, got);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(recs[i].Handle, reassembled[i].Handle);
            Assert.Equal(recs[i].LaneHandle, reassembled[i].LaneHandle);
            Assert.Equal(recs[i].Pos, reassembled[i].Pos);   // float-exact values chosen above
            Assert.Equal(recs[i].Speed, reassembled[i].Speed);
            Assert.Equal(recs[i].Upcoming[0], reassembled[i].Upcoming[0]);
            Assert.Equal(recs[i].Upcoming[1], reassembled[i].Upcoming[1]);
        }
    }
}
