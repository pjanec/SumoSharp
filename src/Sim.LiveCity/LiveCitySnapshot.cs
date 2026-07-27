using System.Collections.Generic;
using Sim.Core;

namespace Sim.LiveCity;

// docs/LIVE-CITY-VIEWERS-DESIGN.md §1 "Read-back" -- the pedestrian's SIM-LOD regime, mirrored from
// SceneGen.BuildLiveCity's KindPedLowPower/KindPedHighPower/KindPedPaused disc-colour mapping (grey =
// low-power weave, orange = promoted full-ORCA, yellow = paused/dwell).
public enum PedRegime
{
    LowPowerWalking,
    HighPower,
    Paused,
}

// One car's read-back pose for a viewer frame.
public readonly struct LiveCityCar
{
    public LiveCityCar(VehicleHandle handle, double x, double y, double z, double angleDeg, double length, double width, string name)
    {
        Handle = handle;
        X = x;
        Y = y;
        Z = z;
        AngleDeg = angleDeg;
        Length = length;
        Width = width;
        Name = name;
    }

    public VehicleHandle Handle { get; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public double AngleDeg { get; }
    public double Length { get; }
    public double Width { get; }
    public string Name { get; }
}

// One pedestrian's read-back pose for a viewer frame.
//
// `Z` is the GROUND elevation under the ped, in the net's own vertical units (docs/EXTERNAL-NET-
// LOADING-DESIGN.md §4). It is exactly 0.0 on a 2-D net -- the demo, and every synthetic net whose
// lane shapes carry no third component -- and the real surface height on a 3-D one (a georeferenced
// Swiss cut sits at ~370-400 m). The pedestrian SIMULATION remains 2-D throughout; this is an
// output-side surface query filled by LiveCitySim.Sample, exactly as the vehicle side derives its own
// PosZ, and it never feeds back into pedestrian state.
public readonly struct LiveCityPed
{
    public LiveCityPed(int id, double x, double y, double z, PedRegime regime, string animTag)
    {
        Id = id;
        X = x;
        Y = y;
        Z = z;
        Regime = regime;
        AnimTag = animTag;
    }

    public int Id { get; }
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public PedRegime Regime { get; }
    public string AnimTag { get; }
}

// A single sampled frame of the coupled scene (LiveCitySim.Sample()), consumed by every viewer.
public sealed class LiveCitySnapshot
{
    public LiveCitySnapshot(IReadOnlyList<LiveCityCar> cars, IReadOnlyList<LiveCityPed> peds, int occupiedCrossings)
    {
        Cars = cars;
        Peds = peds;
        OccupiedCrossings = occupiedCrossings;
    }

    public IReadOnlyList<LiveCityCar> Cars { get; }
    public IReadOnlyList<LiveCityPed> Peds { get; }
    public int OccupiedCrossings { get; }
}
