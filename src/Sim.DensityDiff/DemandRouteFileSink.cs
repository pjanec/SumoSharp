using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Sim.DensityDiff;

// docs/DENSITY-DIFF-HARNESS-DESIGN.md §2, -TASKS.md B1: the caller-owned sink backing
// Sim.LiveCity.IDemandRecordSink -- LiveCitySim never sees this type, only the interface (the
// established `_recordVehSink`/`_recordPedSink` tee shape: LiveCitySim knows nothing about files).
// Streams a SUMO .rou.xml directly (no buffering) since LiveCitySim already calls RecordVehicle in
// ascending depart order (Step() spawns happen in time order), so no caller-side sort is needed --
// SUMO's route-file loader rejects an unsorted file by default, and this sink satisfies that for free.
public sealed class DemandRouteFileSink : Sim.LiveCity.IDemandRecordSink, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly List<double> _recordedDeparts = new();
    private bool _vTypeWritten;
    private bool _closed;

    public int VehicleCount { get; private set; }

    // SC3 witness: every depart time this sink actually wrote, in call order -- lets the caller
    // compare against LiveCitySim's own independent SpawnLog diagnostic value-for-value rather than
    // just count-for-count.
    public IReadOnlyList<double> RecordedDeparts => _recordedDeparts;

    public DemandRouteFileSink(string path)
    {
        _writer = new StreamWriter(path, append: false);
        _writer.WriteLine("<routes>");
    }

    public void RecordVType(string vTypeId, string vClass, double sigma)
    {
        if (_vTypeWritten) return;
        _vTypeWritten = true;
        _writer.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "  <vType id=\"{0}\" vClass=\"{1}\" sigma=\"{2:F2}\"/>",
            Escape(vTypeId), Escape(vClass), sigma));
    }

    public void RecordVehicle(
        string id,
        double departSeconds,
        string departLane,
        double departPos,
        double departSpeed,
        string vTypeId,
        IReadOnlyList<string> routeEdges)
    {
        VehicleCount++;
        _recordedDeparts.Add(departSeconds);
        _writer.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "  <vehicle id=\"{0}\" depart=\"{1:F2}\" departLane=\"{2}\" departPos=\"{3:F2}\" departSpeed=\"{4:F2}\" type=\"{5}\">",
            Escape(id), departSeconds, Escape(departLane), departPos, departSpeed, Escape(vTypeId)));
        _writer.WriteLine("    <route edges=\"" + Escape(string.Join(' ', routeEdges)) + "\"/>");
        _writer.WriteLine("  </vehicle>");
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _writer.WriteLine("</routes>");
        _writer.Dispose();
    }
}
