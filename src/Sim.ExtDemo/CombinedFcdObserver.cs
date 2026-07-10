using System.Globalization;
using Sim.Core;
using Sim.Ingest;

namespace Sim.ExtDemo;

// The D9 export-seam consumer this whole demo is built around (mirrors src/Sim.Harness/
// FcdWriterObserver.cs's schema/format byte-for-byte for the SUMO rows -- see that file's own
// header for the "full double precision, never round to a golden's coarse --precision" rule,
// which applies here identically). The only difference from FcdWriterObserver: at the END of
// each frame (OnFrameEnd), after every SUMO vehicle has been written, this also writes one
// <vehicle> row per ACTIVE external agent, computed from the network's lane geometry rather than
// from the engine (external agents are never engine-simulated vehicles -- see ExternalAgent.cs).
//
// Both kinds of row use the SAME <vehicle id x y angle type speed pos lane/> element shape, so
// Sim.Viz's existing FcdParser (which is schema-driven, not source-driven) reads this file
// exactly like any golden/engine FCD -- external agents are just more rows.
public sealed class CombinedFcdObserver : ISimExportObserver, IDisposable
{
    private readonly TextWriter _writer;
    private readonly NetworkModel _network;
    private readonly IReadOnlyList<ExternalAgentDef> _agents;
    private bool _rootOpen;
    private bool _closed;

    public CombinedFcdObserver(string path, NetworkModel network, IReadOnlyList<ExternalAgentDef> agents)
        : this(new StreamWriter(path, append: false), network, agents)
    {
    }

    public CombinedFcdObserver(TextWriter writer, NetworkModel network, IReadOnlyList<ExternalAgentDef> agents)
    {
        _writer = writer;
        _network = network;
        _agents = agents;
        _writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        _writer.WriteLine("<fcd-export>");
        _rootOpen = true;
    }

    public void OnFrameBegin(double time)
    {
        _writer.Write("    <timestep time=\"");
        _writer.Write(Fmt(time));
        _writer.WriteLine("\">");
    }

    public void OnVehicleExported(in VehicleExportSnapshot s) =>
        WriteVehicleRow(s.VehicleId, s.X, s.Y, s.Angle, s.VehicleType, s.Speed, s.Pos, s.Lane);

    public void OnFrameEnd(double time)
    {
        foreach (var agent in _agents)
        {
            if (!agent.ActiveAt(time))
            {
                continue;
            }

            var pos = agent.FrontPosAt(time);
            var lane = _network.LanesById[agent.LaneId];

            // B6: place the agent CENTRE in world space with the SAME lane->world transform the
            // engine uses for its collision math (EXTERNAL-AGENTS-VIZ.md section 4):
            // PositionAtOffset(shape, frontPos - length/2, latPos). The 3-arg overload shifts the
            // centreline point perpendicular by latPos (+LEFT of travel, the B6 convention), so
            // the drawn agent box lines up exactly with the footprint the cars react to.
            var centreAlong = pos - agent.Length / 2.0;
            var latPos = agent.LatPosAt(time);
            var (x, y, angleDeg) = LaneGeometry.PositionAtOffset(lane.Shape, centreAlong, latPos);
            var speed = agent.IsCar ? agent.Speed : 0.0;

            WriteVehicleRow(agent.RenderId, x, y, angleDeg, agent.RenderType, speed, pos, agent.LaneId);
        }

        _writer.WriteLine("    </timestep>");
    }

    private void WriteVehicleRow(string id, double x, double y, double angle, string type, double speed, double pos, string lane)
    {
        _writer.Write("        <vehicle id=\"");
        _writer.Write(Escape(id));
        _writer.Write("\" x=\"");
        _writer.Write(Fmt(x));
        _writer.Write("\" y=\"");
        _writer.Write(Fmt(y));
        _writer.Write("\" angle=\"");
        _writer.Write(Fmt(angle));
        _writer.Write("\" type=\"");
        _writer.Write(Escape(type));
        _writer.Write("\" speed=\"");
        _writer.Write(Fmt(speed));
        _writer.Write("\" pos=\"");
        _writer.Write(Fmt(pos));
        _writer.Write("\" lane=\"");
        _writer.Write(Escape(lane));
        _writer.WriteLine("\"/>");
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private void CloseRoot()
    {
        if (_closed)
        {
            return;
        }

        if (_rootOpen)
        {
            _writer.WriteLine("</fcd-export>");
            _writer.Flush();
            _rootOpen = false;
        }

        _closed = true;
    }

    public void Dispose()
    {
        CloseRoot();
        _writer.Dispose();
    }
}
