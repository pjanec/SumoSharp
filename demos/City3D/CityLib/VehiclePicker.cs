using System.Collections.Generic;

namespace CityLib;

// docs/LIVE-CITY-VIEWERS-DESIGN.md §5, docs/LIVE-CITY-VIEWERS-TASKS.md D4 -- the City3D (Godot, 3D)
// counterpart to src/Sim.Viewer/LiveCityOverlay.cs's `PickNearest`: SAME nearest-within-radius, first-wins-
// on-ties algorithm, just over SCREEN-space points (a camera's `UnprojectPosition` of each candidate's
// world origin) instead of world-space ones -- Main.cs has no physics colliders on the car MultiMesh to
// ray-pick against, so the click-pick works by comparing the mouse pixel to every candidate's own
// screen-projected position instead (design §5: "camera ray -> nearest instance"). Kept here, Godot-free
// and pure, so it is unit-testable without a window (mirrors PickNearest's own "trivially unit-testable
// without a window" rationale) -- Main.cs only builds the (screenX, screenY) list (filtering out
// candidates behind the camera first) and calls this.
public static class VehiclePicker
{
    // Returns the index into `screenPositions` of the nearest candidate to (mouseX, mouseY) within
    // `maxPixelDist` pixels, or -1 if the list is empty or every candidate is farther than `maxPixelDist`.
    // Ties (exactly equal squared distance) keep the FIRST candidate found -- same stable, deterministic
    // tie-break LiveCityOverlay.PickNearest uses.
    public static int PickNearestScreen(
        IReadOnlyList<(float X, float Y)> screenPositions, float mouseX, float mouseY, float maxPixelDist)
    {
        var maxD2 = maxPixelDist * maxPixelDist;
        var best = -1;
        var bestD2 = float.PositiveInfinity;

        for (var i = 0; i < screenPositions.Count; i++)
        {
            var dx = screenPositions[i].X - mouseX;
            var dy = screenPositions[i].Y - mouseY;
            var d2 = (dx * dx) + (dy * dy);
            if (d2 <= maxD2 && d2 < bestD2)
            {
                bestD2 = d2;
                best = i;
            }
        }

        return best;
    }
}
