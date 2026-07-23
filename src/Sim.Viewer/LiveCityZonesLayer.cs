using System.Numerics;
using Raylib_cs;
using Sim.LiveCity;
using Sim.Viewer.Raylib;

namespace Sim.Viewer;

// docs/LIVE-CITY-VISUALS-NOTES.md deliverable 2 ("Zones layer") / docs/reference/live-city-viz/DESIGN-
// live-city-2d-viz.md §2 row 4: translucent district ground tints, drawn BEFORE the road pass (a "where am
// I" wash under the streets, never obscuring them) -- mirrors LiveCityOverlay.cs's shape ("the ONLY place
// in the demo tool that references Sim.LiveCity" for a given concern; Sim.Viewer.Raylib stays domain-
// agnostic, so this static helper -- not a Renderer method -- is where Sim.LiveCity.SceneZone gets turned
// into raylib draw calls).
public static class LiveCityZonesLayer
{
    // docs/reference/live-city-viz/renderer/templates/template.js:93-105 (ZONE_FILL) by hue: downtown
    // neutral grey, retail amber, dining pink, residential blue, park green, arterial faint grey. Alpha is
    // raised from the reference's raw canvas values (0.05-0.14) into the ~0.18-0.25 band the task calls
    // for (a flat 2D fill needs more opacity than a soft browser canvas blend to read clearly against the
    // dark road/background palette) -- see Main.cs's ZoneFillPalette for the identical reasoning on the 3D
    // side; the two viewers' alphas are picked independently per medium but preserve the SAME relative
    // ordering (arterial faintest, unknown types fall back to ZoneFillDefault).
    private static readonly Dictionary<string, Color> ZoneFillPalette = new(StringComparer.Ordinal)
    {
        ["downtown"] = new Color(148, 163, 184, 51),     // ~0.20 alpha
        ["retail"] = new Color(245, 158, 11, 56),        // ~0.22 alpha
        ["dining"] = new Color(244, 114, 182, 56),       // ~0.22 alpha
        ["residential"] = new Color(96, 165, 250, 51),   // ~0.20 alpha
        ["park"] = new Color(34, 197, 94, 56),           // ~0.22 alpha
        ["arterial"] = new Color(148, 163, 184, 26),     // ~0.10 alpha (faint)
    };

    private static readonly Color ZoneFillDefault = new(156, 163, 175, 38); // ~0.15 alpha

    // Draws every zone as a filled translucent polygon, LARGEST-AREA-FIRST (so a big district's tint is
    // painted, and therefore covered, before a smaller nested/adjacent zone's -- the task's explicit "so a
    // big zone doesn't cover a small one"). Caller draws this BEFORE Renderer.DrawWorldDds/DrawStaticWorld
    // so roads paint on top of the tint, never the reverse. World -> screen goes through the SAME
    // Renderer.Flip (negate Y) every other world-space draw call in this viewer uses, so zones line up with
    // the roads/vehicles/peds pixel-for-pixel.
    public static void Draw(Camera2D camera, IReadOnlyList<SceneZone> zones)
    {
        if (zones.Count == 0)
        {
            return;
        }

        var ordered = new List<(SceneZone Zone, double Area)>(zones.Count);
        foreach (var z in zones)
        {
            ordered.Add((z, PlanarArea(z.Polygon)));
        }

        ordered.Sort((a, b) => b.Area.CompareTo(a.Area));

        global::Raylib_cs.Raylib.BeginMode2D(camera);

        foreach (var (zone, _) in ordered)
        {
            if (zone.Polygon.Count < 3)
            {
                continue; // degenerate polygon -- nothing to fill.
            }

            var color = ZoneFillPalette.TryGetValue(zone.Type, out var c) ? c : ZoneFillDefault;

            // Raylib.DrawTriangleFan fans around points[0] -- exact for the convex (or near-convex, e.g.
            // the arterial ring's collinear mid-edge points) district polygons the demo_city/box dataset
            // ships, same fan-triangulation choice CityLib.ZoneGroundBuilder makes on the 3D side.
            var pts = new Vector2[zone.Polygon.Count];
            for (var i = 0; i < zone.Polygon.Count; i++)
            {
                var (x, y) = zone.Polygon[i];
                pts[i] = Renderer.Flip(x, y);
            }

            global::Raylib_cs.Raylib.DrawTriangleFan(pts, pts.Length, color);
        }

        global::Raylib_cs.Raylib.EndMode2D();
    }

    // Shoelace formula over SUMO (x,y) -- same technique CityLib.ZoneGroundBuilder.PlanarArea uses on the
    // 3D side, kept independent (this file must not depend on CityLib) so both viewers compute the SAME
    // largest-area-first ordering from the SAME underlying LiveCityScene data without sharing code across
    // the Godot/raylib boundary.
    private static double PlanarArea(IReadOnlyList<(double X, double Y)> polygon)
    {
        var n = polygon.Count;
        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var (x1, y1) = polygon[i];
            var (x2, y2) = polygon[(i + 1) % n];
            sum += (x1 * y2) - (x2 * y1);
        }

        return Math.Abs(sum) * 0.5;
    }
}
