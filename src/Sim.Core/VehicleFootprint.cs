namespace Sim.Core;

// Task B-guard (docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md §3.2b): world-space geometry for the
// "a car must never pass a pedestrian at close distance AND high speed" guard.
//
// Deliberately a WORLD-SPACE primitive, not a lane-projection test (the owner's framing): it answers
// "how far is this disc from that car's actual body, in metres" for any heading, so it means the same
// thing on a curved lane, an internal junction lane, or a laneless open-space regime. Pure function --
// no engine state, no allocation -- so both the engine constraint and its tests can call it.
public static class VehicleFootprint
{
    // Shortest distance (metres) between a vehicle's body rectangle and a disc, 0 when they touch and
    // NEGATIVE when the disc overlaps the body (penetration depth along the separating axes).
    //
    // Convention matches the engine's: (frontX, frontY) is the vehicle's FRONT BUMPER centre (SUMO's
    // `Pos` is the front, not the centre) and `angleDeg` is the naviDegree heading LaneGeometry.
    // PositionAtOffset returns -- 0 = north, increasing CLOCKWISE. The body therefore occupies
    // [-length, 0] along the forward axis and [-width/2, +width/2] across it.
    public static double ClearanceToDisc(
        double frontX,
        double frontY,
        double angleDeg,
        double length,
        double width,
        double discX,
        double discY,
        double discRadius)
    {
        var (along, lateral) = ToBodyFrame(frontX, frontY, angleDeg, discX, discY);
        return ClearanceFromBodyFrame(along, lateral, length, width, discRadius);
    }

    // The same distance, for a disc centre already expressed in the body frame (see ToBodyFrame). Split
    // out so a caller that has to PREDICT the disc's body-frame position (advance ego along its heading,
    // carry the disc along its own velocity) can reuse the rectangle maths without inventing a fake
    // world position to feed back through ToBodyFrame.
    public static double ClearanceFromBodyFrame(double along, double lateral, double length, double width, double discRadius)
    {
        var halfWidth = width * 0.5;

        // Distance from the disc centre to the rectangle, per axis (0 while inside that axis' span).
        var dAlong = Math.Max(Math.Max(along, -length - along), 0.0);
        var dLateral = Math.Max(Math.Abs(lateral) - halfWidth, 0.0);
        return Math.Sqrt((dAlong * dAlong) + (dLateral * dLateral)) - discRadius;
    }

    // A world VECTOR (a velocity) rotated into the vehicle's body frame -- the direction-only sibling of
    // ToBodyFrame, with no translation.
    public static (double Along, double Lateral) VectorToBodyFrame(double angleDeg, double vx, double vy)
    {
        var rad = angleDeg * Math.PI / 180.0;
        var fwdX = Math.Sin(rad);
        var fwdY = Math.Cos(rad);
        return ((vx * fwdX) + (vy * fwdY), (vx * -fwdY) + (vy * fwdX));
    }

    // A point in the vehicle's body frame: `Along` is metres ahead of the FRONT bumper (so the body is
    // Along in [-length, 0]) and `Lateral` is metres to the LEFT of travel -- the same +90 deg CCW
    // normal Kinematics.LatOffset and LaneGeometry.PositionAtOffset's latOffset use.
    public static (double Along, double Lateral) ToBodyFrame(
        double frontX,
        double frontY,
        double angleDeg,
        double pointX,
        double pointY)
    {
        // naviDegree (0 = north, clockwise) -> forward unit vector (sin, cos); left normal (-cos, sin).
        var rad = angleDeg * Math.PI / 180.0;
        var fwdX = Math.Sin(rad);
        var fwdY = Math.Cos(rad);
        var relX = pointX - frontX;
        var relY = pointY - frontY;
        return ((relX * fwdX) + (relY * fwdY), (relX * -fwdY) + (relY * fwdX));
    }
}
