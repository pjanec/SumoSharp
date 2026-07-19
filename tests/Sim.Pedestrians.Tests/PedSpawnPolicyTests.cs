using System;
using Sim.Pedestrians;
using Xunit;

namespace Sim.Pedestrians.Tests;

// P8-2 (docs/PEDESTRIAN-P8-2-APPEARANCE-LEGITIMACY-DESIGN.md; docs/COORDINATION-pedestrian-x-subarea.md §2):
// the appearance-legitimacy gate. These hold PedSpawnPolicy to its contract -- the walkable-edge analogue of
// RealismMask: MaySpawnOrDespawn(e) = isFringe(e) OR isSink(e) OR isOffCamera(e), and crucially INERT BY
// DEFAULT (empty visible set => everything permissive), so it can be added without disturbing any golden.
public class PedSpawnPolicyTests
{
    [Fact]
    public void EmptyVisibleSet_IsFullyPermissive_TheInertDefault()
    {
        var policy = new PedSpawnPolicy(fringeEdgeIds: Array.Empty<string>(), visibleWalkableEdgeIds: Array.Empty<string>());

        // No camera -> every edge is off-camera -> every appearance is legitimate.
        Assert.True(policy.MaySpawnOrDespawn("anything"));
        Assert.True(policy.MaySpawnOrDespawn("on-a-busy-sidewalk"));
        Assert.True(PedSpawnPolicy.Permissive.MaySpawnOrDespawn("whatever"));
    }

    [Fact]
    public void OnCameraNonFringeNonSink_IsForbidden()
    {
        var policy = new PedSpawnPolicy(
            fringeEdgeIds: new[] { "fringeA" },
            visibleWalkableEdgeIds: new[] { "midblock", "fringeA", "sinkEdge" },
            sinkEdgeIds: new[] { "sinkEdge" });

        // An on-camera mid-block sidewalk is the classic illegal pop -> forbidden.
        Assert.False(policy.MaySpawnOrDespawn("midblock"));
    }

    [Fact]
    public void OnCameraFringe_IsAllowed()
    {
        var policy = new PedSpawnPolicy(
            fringeEdgeIds: new[] { "fringeA" },
            visibleWalkableEdgeIds: new[] { "fringeA", "midblock" });

        // A fringe stub is a legitimate on-lane entry/exit even in full view.
        Assert.True(policy.MaySpawnOrDespawn("fringeA"));
        Assert.True(policy.IsFringe("fringeA"));
        Assert.False(policy.MaySpawnOrDespawn("midblock"));
    }

    [Fact]
    public void OnCameraSink_IsAllowed()
    {
        var policy = new PedSpawnPolicy(
            fringeEdgeIds: Array.Empty<string>(),
            visibleWalkableEdgeIds: new[] { "doorEdge", "midblock" },
            sinkEdgeIds: new[] { "doorEdge" });

        // A legitimate sink (building door / transit / parking) in view is allowed; a plain sidewalk is not.
        Assert.True(policy.MaySpawnOrDespawn("doorEdge"));
        Assert.True(policy.IsSink("doorEdge"));
        Assert.False(policy.MaySpawnOrDespawn("midblock"));
    }

    [Fact]
    public void OffCameraEdge_IsAlwaysAllowed_EvenIfNotFringeOrSink()
    {
        var policy = new PedSpawnPolicy(
            fringeEdgeIds: new[] { "fringeA" },
            visibleWalkableEdgeIds: new[] { "visibleMidblock" });

        // "offCameraMidblock" is not in the visible set -> off-camera -> the viewer can't see the pop.
        Assert.True(policy.MaySpawnOrDespawn("offCameraMidblock"));
        Assert.False(policy.IsOnCamera("offCameraMidblock"));
        // The one visible non-fringe edge is still forbidden.
        Assert.False(policy.MaySpawnOrDespawn("visibleMidblock"));
        Assert.True(policy.IsOnCamera("visibleMidblock"));
    }

    [Fact]
    public void NullEdge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PedSpawnPolicy.Permissive.MaySpawnOrDespawn(null!));
    }
}
