using System;
using System.Globalization;
using System.IO;

namespace Sim.Pedestrians;

// P8-3xP8-4 recorder (docs/COORDINATION-pedestrian-x-subarea.md §3; PEDESTRIAN-P8-BACKLOG.md "P8-5"): writes a
// SUMO-schema FCD stream of `<person>` rows -- the pedestrian half of the shared car+ped replay contract. It
// mirrors Sim.Harness.FcdWriterObserver's vehicle writer exactly (same `<fcd-export>` root, one `<timestep>`
// per frame, full round-trippable "R" precision) but emits `<person>` children, so a viz that already reads
// the vehicle FCD renders these peds beside the cars.
//
// Emitted attributes are the geometry-bearing subset SUMO also writes: id, x, y, angle (SUMO bearing:
// degrees clockwise from north), speed, type, slope. `edge`/`pos` are intentionally OMITTED -- they need a
// world->edge resolver mid-route and a shared car+ped edge space, which is the P8-5 concern (backlog); the
// world-frame x/y is the render source. Add edge/pos here if a consumer needs edge-keyed person rows.
public sealed class PersonFcdWriter : IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private bool _rootOpen;
    private bool _timestepOpen;
    private bool _closed;

    public PersonFcdWriter(string path)
        : this(new StreamWriter(path, append: false), ownsWriter: true)
    {
    }

    public PersonFcdWriter(TextWriter writer, bool ownsWriter = false)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _ownsWriter = ownsWriter;
        _writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        _writer.WriteLine("<fcd-export>");
        _rootOpen = true;
    }

    public void BeginTimestep(double time)
    {
        if (_timestepOpen)
        {
            throw new InvalidOperationException("timestep already open; call EndTimestep first.");
        }

        _writer.Write("    <timestep time=\"");
        _writer.Write(Fmt(time));
        _writer.WriteLine("\">");
        _timestepOpen = true;
    }

    public void WritePerson(string id, double x, double y, double angle, double speed, string type = "ped")
    {
        if (!_timestepOpen)
        {
            throw new InvalidOperationException("no open timestep; call BeginTimestep first.");
        }

        _writer.Write("        <person id=\"");
        _writer.Write(Escape(id));
        _writer.Write("\" x=\"");
        _writer.Write(Fmt(x));
        _writer.Write("\" y=\"");
        _writer.Write(Fmt(y));
        _writer.Write("\" angle=\"");
        _writer.Write(Fmt(angle));
        _writer.Write("\" speed=\"");
        _writer.Write(Fmt(speed));
        _writer.Write("\" type=\"");
        _writer.Write(Escape(type));
        _writer.Write("\" slope=\"");
        _writer.Write(Fmt(0.0));
        _writer.WriteLine("\"/>");
    }

    public void EndTimestep()
    {
        if (!_timestepOpen)
        {
            throw new InvalidOperationException("no open timestep to end.");
        }

        _writer.WriteLine("    </timestep>");
        _timestepOpen = false;
    }

    // SUMO FCD angle: degrees clockwise from north (0 = +y, 90 = +x). bearing = atan2(dx, dy). A zero motion
    // vector reports `fallback` (caller passes the ped's previous angle, or 0 at spawn) so a stationary ped
    // does not snap to an arbitrary heading.
    public static double BearingDegrees(double dx, double dy, double fallback = 0.0)
    {
        if (dx == 0.0 && dy == 0.0)
        {
            return fallback;
        }

        var deg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
        if (deg < 0.0)
        {
            deg += 360.0;
        }

        return deg;
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

        if (_timestepOpen)
        {
            EndTimestep();
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
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
    }
}
