using DotRecast.Detour;
using DotRecast.Recast;
using Sim.Pedestrians.Navigation.Bake;

namespace Sim.Pedestrians.Navigation.DotRecast;

// Builds a DtNavMesh from a WalkablePolygonBaker.Bake() polygon set via DotRecast's standard
// solo-tile Recast pipeline (docs/PEDESTRIAN-POC-PLAN.md POC-1b task step 2): rasterize ->
// compact-heightfield -> erode by agent radius -> watershed regions -> contours -> poly mesh ->
// poly mesh detail -> DtNavMesh, matching DotRecast's own reference builder
// (DotRecast.Recast.Toolset.Builder.SoloNavMeshBuilder) without depending on the Toolset package.
//
// DETERMINISTIC: fixed input geometry (DotRecastGeometry.Triangulate is a pure function of the
// baked polygon list) + fixed config + a single-threaded, unseeded Recast build (RcBuilder.Build,
// no RNG anywhere in the pipeline) -> the same polygons + config always produce the same DtNavMesh.
public static class DotRecastNavMeshBuilder
{
    public static DtNavMesh Build(IReadOnlyList<BakedPolygon> polygons, DotRecastBuildConfig config)
    {
        var geom = DotRecastGeometry.Triangulate(polygons);

        var cfg = new RcConfig(
            useTiles: false, tileSizeX: 0, tileSizeZ: 0, borderSize: 0,
            partition: RcPartition.WATERSHED,
            cellSize: config.CellSize, cellHeight: config.CellHeight,
            agentMaxSlope: config.AgentMaxSlopeDeg, agentHeight: config.AgentHeight,
            agentRadius: config.AgentRadius, agentMaxClimb: config.AgentMaxClimb,
            minRegionArea: config.MinRegionArea, mergeRegionArea: config.MergeRegionArea,
            edgeMaxLen: config.EdgeMaxLen, edgeMaxError: config.EdgeMaxError, vertsPerPoly: config.VertsPerPoly,
            detailSampleDist: config.DetailSampleDist, detailSampleMaxError: config.DetailSampleMaxError,
            filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(RcRecast.RC_WALKABLE_AREA), buildMeshDetail: true);

        var builderConfig = new RcBuilderConfig(cfg, geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax());
        var builder = new RcBuilder();
        var result = builder.Build(geom, builderConfig, keepInterResults: false);

        var polyMesh = result.Mesh;
        if (polyMesh == null || polyMesh.npolys == 0)
        {
            throw new InvalidOperationException(
                "DotRecast produced an empty poly mesh from the baked walkable polygons -- either the " +
                "input geometry is empty or the build config (cell size / min region area / agent " +
                "radius) is too coarse for it.");
        }

        // Single walkable poly type for the POC: every generated polygon is flagged 1 (matches
        // DtQueryDefaultFilter's default includeFlags=0xffff, so every poly passes the query filter).
        for (var i = 0; i < polyMesh.npolys; i++)
        {
            polyMesh.flags[i] = 1;
        }

        var polyMeshDetail = result.MeshDetail;

        var createParams = new DtNavMeshCreateParams
        {
            verts = polyMesh.verts,
            vertCount = polyMesh.nverts,
            polys = polyMesh.polys,
            polyAreas = polyMesh.areas,
            polyFlags = polyMesh.flags,
            polyCount = polyMesh.npolys,
            nvp = polyMesh.nvp,
            walkableHeight = config.AgentHeight,
            walkableRadius = config.AgentRadius,
            walkableClimb = config.AgentMaxClimb,
            bmin = polyMesh.bmin,
            bmax = polyMesh.bmax,
            cs = config.CellSize,
            ch = config.CellHeight,
            buildBvTree = true,
        };

        if (polyMeshDetail != null)
        {
            createParams.detailMeshes = polyMeshDetail.meshes;
            createParams.detailVerts = polyMeshDetail.verts;
            createParams.detailVertsCount = polyMeshDetail.nverts;
            createParams.detailTris = polyMeshDetail.tris;
            createParams.detailTriCount = polyMeshDetail.ntris;
        }

        var meshData = DtNavMeshBuilder.CreateNavMeshData(createParams)
            ?? throw new InvalidOperationException("DtNavMeshBuilder.CreateNavMeshData rejected the poly mesh built from the baked walkable polygons.");

        var navMesh = new DtNavMesh();
        var status = navMesh.Init(meshData, config.VertsPerPoly, 0);
        if (status.Failed())
        {
            throw new InvalidOperationException($"DtNavMesh.Init failed: {status}");
        }

        return navMesh;
    }
}
