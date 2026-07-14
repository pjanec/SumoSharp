using CycloneDDS.Runtime;
using Sim.Replication.Dds;

namespace Sim.Viewer.Core;

// docs/SUMOSHARP-NATIVE-VIEWER.md "remote control": the reverse DDS channel that lets a view-only `--mode
// remote` (no engine) drive the `--mode publish` process's EngineHost. The remote WRITES DdsViewerCommand;
// the publisher READS them and applies to its host. Kind values are the wire contract (DdsViewerCommand.Kind).
public enum ViewerCommandKind : byte
{
    Pause = 0,            // Flag: 1 = pause, 0 = resume
    SetSpeed = 1,         // Value = x real-time
    Restart = 2,
    ClearObstacles = 3,
    SetRandomTraffic = 4, // Flag: 1 = on, 0 = off
    InjectObstacle = 5,   // X,Y = world point
    SetStepLength = 6,    // Value = sim step length seconds (sandbox only)
}

// Remote side: publishes commands. WriterId keys the instance (unique per remote process) so several remotes
// don't clobber each other's samples; Seq monotonically increases so the publisher can dedup re-deliveries.
public sealed class DdsCommandWriter : IDisposable
{
    private readonly DdsWriter<DdsViewerCommand> _writer;
    private readonly int _writerId;
    private uint _seq;

    public DdsCommandWriter(DdsParticipant participant)
    {
        // RELIABLE + TRANSIENT_LOCAL: a command must not be dropped, and a remote that connects before the
        // publisher still has its latest command delivered once the publisher appears (Seq dedup keeps that
        // idempotent).
        _writer = new DdsWriter<DdsViewerCommand>(participant, DdsTopicNames.Commands, DdsQos.DurableLatest());
        _writerId = Environment.ProcessId;
    }

    public void Send(ViewerCommandKind kind, double value = 0.0, double x = 0.0, double y = 0.0, bool flag = false)
    {
        _writer.Write(new DdsViewerCommand
        {
            WriterId = _writerId,
            Seq = ++_seq,
            Kind = (byte)kind,
            Value = value,
            X = x,
            Y = y,
            Flag = (byte)(flag ? 1 : 0),
        });
        Console.WriteLine($"CMD SENT kind={kind} seq={_seq} value={value:F2} flag={flag}");
    }

    public void Dispose() => _writer.Dispose();
}

// Publisher side: reads commands and applies them to the EngineHost. Call PumpApply() once per publish loop.
// Per-WriterId Seq dedup so each command applies exactly once even under RELIABLE re-delivery / late join.
public sealed class DdsCommandReader : IDisposable
{
    private readonly DdsReader<DdsViewerCommand> _reader;
    private readonly Dictionary<int, uint> _lastSeqByWriter = new();

    public DdsCommandReader(DdsParticipant participant)
    {
        _reader = new DdsReader<DdsViewerCommand>(participant, DdsTopicNames.Commands, DdsQos.DurableLatest());
    }

    public void PumpApply(EngineHost host)
    {
        using var loan = _reader.Take(maxSamples: 64);
        foreach (var sample in loan)
        {
            if (!sample.IsValid)
            {
                continue;
            }

            var c = sample.Data;
            if (_lastSeqByWriter.TryGetValue(c.WriterId, out var last) && c.Seq <= last)
            {
                continue; // already applied (or a stale re-delivery)
            }

            _lastSeqByWriter[c.WriterId] = c.Seq;
            Console.WriteLine($"CMD APPLIED kind={(ViewerCommandKind)c.Kind} seq={c.Seq} value={c.Value:F2} flag={c.Flag}");
            Apply(host, c);
        }
    }

    private static void Apply(EngineHost host, DdsViewerCommand c)
    {
        switch ((ViewerCommandKind)c.Kind)
        {
            case ViewerCommandKind.Pause: host.SetPaused(c.Flag != 0); break;
            case ViewerCommandKind.SetSpeed: host.SetSpeed(c.Value); break;
            case ViewerCommandKind.Restart: host.Restart(); break;
            case ViewerCommandKind.ClearObstacles: host.ClearObstacles(); break;
            case ViewerCommandKind.SetRandomTraffic: host.SetRandomTraffic(c.Flag != 0); break;
            case ViewerCommandKind.InjectObstacle: host.InjectObstacleAtWorld(c.X, c.Y); break;
            case ViewerCommandKind.SetStepLength: host.SetStepLength(c.Value); break;
        }
    }

    public void Dispose() => _reader.Dispose();
}
