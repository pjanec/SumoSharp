using Sim.Core;
using Sim.Replication;

namespace Sim.Viz;

// The abstraction VizReplayBuilder drives: any deterministic source of vehicle (and optional ped) poses
// that can be stepped and published onto the replication wire the DR reconstruction reads. Adapters:
// LiveCitySource (cars+peds live-city) and (T3) EngineScenarioSource (a plain Engine on a scenario dir).
internal interface IVizReplaySource
{
    void Step();                              // advance one deterministic sim step (= Dt seconds)
    double Dt { get; }                        // sim step seconds
    IReplicationSource VehicleSource { get; } // the vehicle wire (History carries UpcomingLanes -> look-ahead)
    IPedReplicationSource? PedSource { get; } // the ped wire, or null (vehicle-only scenario)
    ILaneShapeSource Lanes { get; }           // z-aware lane geometry for the reconstruction
    NetworkPayload Network { get; }           // road/junction/crossing polylines for the background (already cropped)
    (double X0, double Y0, double X1, double Y1) View { get; } // crop bbox
}
