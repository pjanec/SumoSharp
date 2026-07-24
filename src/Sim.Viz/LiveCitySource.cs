using System.IO;
using Sim.Core;
using Sim.Ingest;
using Sim.LiveCity;
using Sim.Pedestrians;
using Sim.Replication;

namespace Sim.Viz;

// IVizReplaySource adapter over the REAL live-city demo host (LiveCitySim + LiveCityConfig): same net,
// demand and params as the City3D/raylib demo. Cars + peds, both first-class.
internal sealed class LiveCitySource : IVizReplaySource, System.IDisposable
{
    private readonly LiveCitySim _sim;
    private readonly double _dt;
    private readonly NetworkPayload _network;
    private readonly (double, double, double, double) _view;

    internal LiveCitySource(string repoRoot)
    {
        var cfg = LiveCityConfig.ForRepoRoot(repoRoot);
        var netPath = Path.Combine(cfg.DatasetDir, "net.xml");
        var model = NetworkParser.Parse(netPath);
        var fullNet = PayloadBuilder.BuildNetwork(model);
        var pedNetwork = PedNetworkParser.Load(netPath);
        _dt = cfg.Dt;
        _view = (cfg.X0, cfg.Y0, cfg.X1, cfg.Y1);
        _sim = new LiveCitySim(cfg);
        _network = SceneGen.CropNetwork(fullNet, pedNetwork, netPath, cfg.X0, cfg.Y0, cfg.X1, cfg.Y1);
    }

    public double Dt => _dt;
    public IReplicationSource VehicleSource => _sim.VehicleSource;
    public IPedReplicationSource? PedSource => _sim.PedSource;
    public ILaneShapeSource Lanes => _sim.LocalLanes;
    public NetworkPayload Network => _network;
    public (double X0, double Y0, double X1, double Y1) View => _view;
    public void Step() => _sim.Step();
    public void Dispose() => _sim.Dispose();
}
