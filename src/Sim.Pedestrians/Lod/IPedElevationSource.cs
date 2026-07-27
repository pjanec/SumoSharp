using Sim.Core.Orca;

namespace Sim.Pedestrians.Lod;

// docs/EXTERNAL-NET-LOADING-DESIGN.md §4 (C2): the render-side "how high is the ground here?" seam for
// pedestrians on a 3-D net.
//
// WHY IT IS AN INTERFACE HERE RATHER THAN A CONCRETE SAMPLER. The elevation data lives on the VEHICLE
// side -- `Sim.Ingest.NetworkModel`'s `Lane.ShapeZ`, sampled by `Sim.Ingest.LaneGeometry` -- but
// Sim.Pedestrians must never reference Sim.Ingest or any parity source (this project's own csproj says
// so, and docs/PEDESTRIAN-DESIGN.md §0 Principle 6 requires it). So the dependency is INVERTED: the ped
// stack declares the shape of the question, and a project that legitimately sees both sides
// (Sim.LiveCity, via `NetLaneElevationSource`) answers it.
//
// Deliberately minimal -- one query, no lifecycle, no net-specific vocabulary -- so an embedder with a
// completely different elevation source (a terrain heightmap, a physics raycast, a game engine's own
// ground query) can satisfy it without owning a SUMO network at all.
public interface IPedElevationSource
{
    // The ground/surface elevation, in the net's own vertical units (metres, matching lane shape z), at
    // the given 2-D world position. Implementations must be pure and thread-safe for concurrent reads:
    // a reconstructor may be pumped from a render thread while the sim steps.
    //
    // Returns 0.0 when the position cannot be attributed to any known surface -- the same value the
    // pedestrian stack used before elevation existed, so a 2-D net degrades to exactly its old
    // behaviour rather than to a hole in the world.
    double ElevationAt(Vec2 pos);
}
