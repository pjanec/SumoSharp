using System.Collections.Generic;
using CityLib;
using Xunit;

namespace CityLib.Tests;

// docs/LIVE-CITY-VIEWERS-TASKS.md D4 success condition: "a scripted pick ... returns the expected
// VehicleHandle/id" -- exercised here via the pure PickNearestScreen helper (the part of the pick path that
// doesn't need a live Godot window/camera), mirroring src/Sim.Viewer.Tests' own LiveCityOverlay.PickNearest
// coverage one dimension over (screen-space instead of world-space).
public class VehiclePickerTests
{
    [Fact]
    public void PickNearestScreen_ChoosesNearestCandidateWithinRadius()
    {
        var positions = new List<(float X, float Y)> { (100f, 100f), (150f, 100f), (500f, 500f) };

        var idx = VehiclePicker.PickNearestScreen(positions, mouseX: 110f, mouseY: 100f, maxPixelDist: 24f);

        Assert.Equal(0, idx);
    }

    [Fact]
    public void PickNearestScreen_PicksTheCloserOfTwoWithinRadius()
    {
        var positions = new List<(float X, float Y)> { (100f, 100f), (118f, 100f) };

        var idx = VehiclePicker.PickNearestScreen(positions, mouseX: 120f, mouseY: 100f, maxPixelDist: 24f);

        Assert.Equal(1, idx);
    }

    [Fact]
    public void PickNearestScreen_ReturnsMinusOne_WhenNothingIsWithinRadius()
    {
        var positions = new List<(float X, float Y)> { (100f, 100f) };

        var idx = VehiclePicker.PickNearestScreen(positions, mouseX: 400f, mouseY: 400f, maxPixelDist: 24f);

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void PickNearestScreen_ReturnsMinusOne_ForEmptyList()
    {
        var idx = VehiclePicker.PickNearestScreen(
            new List<(float, float)>(), mouseX: 0f, mouseY: 0f, maxPixelDist: 24f);

        Assert.Equal(-1, idx);
    }

    [Fact]
    public void PickNearestScreen_ExactlyAtRadiusBoundary_IsIncluded()
    {
        var positions = new List<(float X, float Y)> { (124f, 100f) }; // exactly 24px away

        var idx = VehiclePicker.PickNearestScreen(positions, mouseX: 100f, mouseY: 100f, maxPixelDist: 24f);

        Assert.Equal(0, idx);
    }

    [Fact]
    public void PickNearestScreen_TieKeepsFirstCandidateFound()
    {
        var positions = new List<(float X, float Y)> { (100f, 100f), (100f, 100f) };

        var idx = VehiclePicker.PickNearestScreen(positions, mouseX: 100f, mouseY: 100f, maxPixelDist: 24f);

        Assert.Equal(0, idx);
    }
}
