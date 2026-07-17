namespace Sim.Pedestrians.Navigation.DotRecast;

// Recast build parameters for the POC (docs/PEDESTRIAN-POC-PLAN.md POC-1b). POC-0's geometry is a
// small, flat pedestrian net (walkingareas/crossings/sidewalk quads a few metres across), so a fine
// cell size resolves narrow crossings/sidewalks without an excessive voxel count.
//
// AgentMaxSlopeDeg is small (the walkable plane is flat -- everything is lifted to Recast Y=0, see
// DotRecastGeometry) rather than 0 to leave a little slack for floating-point noise in the lifted
// triangle normals; it has no real effect on a perfectly flat mesh.
//
// MinRegionArea / MergeRegionArea are world-unit m^2 (not the squared-voxel-count form some Recast
// wrappers use) -- kept small so POC-0's small walkable polygons (a crossing outline, a 1-2 m wide
// sidewalk quad) are not merged away or dropped as "small islands" by the watershed partitioner.
public sealed record DotRecastBuildConfig(
    float CellSize = 0.2f,
    float CellHeight = 0.2f,
    float AgentRadius = 0.3f,
    float AgentHeight = 1.8f,
    float AgentMaxClimb = 0.1f,
    float AgentMaxSlopeDeg = 10f,
    int VertsPerPoly = 6,
    float MinRegionArea = 0.25f,
    float MergeRegionArea = 1.0f,
    float EdgeMaxLen = 2.0f,
    float EdgeMaxError = 0.5f,
    float DetailSampleDist = 1.0f,
    float DetailSampleMaxError = 0.2f)
{
    public static readonly DotRecastBuildConfig Default = new();
}
