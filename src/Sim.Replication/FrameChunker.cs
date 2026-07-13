namespace Sim.Replication;

// SUMOSHARP-DEADRECKONING.md §4.3 — splitting one tick's records into fixed-cap chunks for a transport that
// bounds a single sample/datagram. Two callers, one arithmetic:
//   * the DDS opaque-blob topic (DdsWireFrame) caps by a BYTE budget -> MaxRecordsForPayload,
//   * the DDS structured-batch topic (DdsVehicleBatch) caps by a fixed SAMPLE count.
// Pure integer arithmetic; no transport dependency; ns2.1-clean. Keeping it here (not in the DDS package)
// means the chunk math is covered by the hermetic gate even though the DDS types are not.
public static class FrameChunker
{
    // How many fixed-size records fit in `maxPayloadBytes` after the 16 B frame header. 0 if the budget
    // cannot even hold the header + one record.
    public static int MaxRecordsForPayload(int maxPayloadBytes, int recordSize)
    {
        if (recordSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recordSize), "record size must be positive.");
        }

        var body = maxPayloadBytes - FrameCodec.HeaderSize;
        return body <= 0 ? 0 : body / recordSize;
    }

    // Number of chunks needed to carry `recordCount` records at <= maxPerChunk each. Zero records -> zero
    // chunks (nothing to publish this tick; liveness rides the separate lifecycle topic, not an empty batch).
    public static int ChunkCount(int recordCount, int maxPerChunk)
    {
        if (maxPerChunk <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPerChunk), "chunk cap must be positive.");
        }

        if (recordCount <= 0)
        {
            return 0;
        }

        return (recordCount + maxPerChunk - 1) / maxPerChunk;
    }

    // The [Start, Start+Count) slice of the record array for chunk `chunkIndex`. Count is `maxPerChunk` for
    // every chunk but the last, which carries the remainder. Throws if chunkIndex is out of range.
    public static (int Start, int Count) ChunkRange(int chunkIndex, int recordCount, int maxPerChunk)
    {
        var total = ChunkCount(recordCount, maxPerChunk);
        if ((uint)chunkIndex >= (uint)total)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex),
                $"chunk {chunkIndex} out of range for {recordCount} records at {maxPerChunk}/chunk ({total} chunks).");
        }

        var start = chunkIndex * maxPerChunk;
        var count = Math.Min(maxPerChunk, recordCount - start);
        return (start, count);
    }
}
