using Sim.Core.Orca;

namespace Sim.Core.Mixed;

// ANISOTROPIC (shape-aware) reciprocal collision avoidance for the believable mixed-traffic module
// (docs/INDIA-TRAFFIC.md, section 4). This is the polygonal generalization of RVO2's disc velocity
// obstacle: each vehicle is a centered convex polygon (ConvexShape) oriented to its heading, so the
// avoidance a long bus imposes broadside differs from what it imposes end-on, and a small motorcycle
// threads gaps a bus cannot. The resulting ORCA half-planes are solved by the SAME, already-validated
// HalfPlaneLp (LinearProgram1/2/3) the disc OrcaSolver uses -- only the per-neighbour half-plane
// construction changes.
//
// Derivation (relative-position / relative-velocity space, RVO2 conventions):
//   rp = other.Position - self.Position   (relative position)
//   rv = self.Velocity - other.Velocity   (relative velocity; relative position closes at rate -rv)
//   P  = self.Shape (+) (-other.Shape)     (Minkowski collision set: the two footprints overlap iff
//                                           rp in P; P is centered and, for centrally-symmetric
//                                           shapes, symmetric under negation)
//   collision within (0, tau]  <=>  exists t in (0, tau]: rp - rv*t in P
//   => the velocity obstacle is VO = U_{t in (0,tau]} Q/t, where Q = P translated to rp (= rp (+) P).
// VO is the truncated cone of the polygon Q from the origin: its boundary is the near cap -- the
// chain of edges of (1/tau)*Q that face the origin -- plus the two tangent ("leg") rays from the
// origin to Q. The disc solver is exactly this with Q a circle (cap -> cutoff arc, legs -> tangents);
// the many-gon-approximates-a-circle limit reproduces the disc half-plane (asserted in tests).
//
// For the nearest boundary point b to rv we take the outward unit normal n purely from the boundary
// geometry (never from the sign of b-rv), so the SAME formula handles rv inside VO (must change) and
// rv outside VO (already safe): u = b - rv; the ORCA half-plane is Point = self.Velocity +
// responsibility*u, Direction = perpCW(n) -- feasible side is +n (outside VO). Responsibility is the
// asymmetric-priority dial (0.5 reciprocal; ->1 self yields fully to a committed/heavy neighbour;
// ->0 self holds and others flow around). Purely functional, allocation-light, deterministic.
public static class ShapedVoSolver
{
    // One vehicle's frozen state for the solve. Shape is the footprint ALREADY rotated to the
    // vehicle's heading and centered on its reference point (the crowd owns identity/goals/heading).
    public readonly struct ShapedAgent
    {
        public readonly Vec2 Position;
        public readonly Vec2 Velocity;
        public readonly ConvexShape Shape;
        // Fraction of the avoidance correction SELF takes for THIS neighbour (see class comment).
        public readonly double Responsibility;

        public ShapedAgent(Vec2 position, Vec2 velocity, ConvexShape shape, double responsibility = 0.5)
        {
            Position = position;
            Velocity = velocity;
            Shape = shape;
            Responsibility = responsibility;
        }
    }

    // Compute the collision-avoiding new velocity for `self` given its shaped `neighbours` (already
    // range-filtered; static walls are passed as zero-velocity neighbours with responsibility 1.0).
    // `lineScratch` must have length >= neighbours.Length. numObstLines is 0 here (walls are modelled
    // as agents, so every line is agent-derived / pairwise-intersectable in the LP3 fallback).
    public static Vec2 ComputeNewVelocity(
        in ShapedAgent self,
        ReadOnlySpan<ShapedAgent> neighbours,
        Vec2 prefVelocity,
        double maxSpeed,
        double timeHorizon,
        double timeStep,
        Span<OrcaLine> lineScratch)
    {
        var lineCount = 0;
        for (var i = 0; i < neighbours.Length; i++)
        {
            lineScratch[lineCount++] = BuildAgentLine(self, neighbours[i], timeHorizon, timeStep);
        }

        var lines = lineScratch[..lineCount];
        var result = Vec2.Zero;
        var lineFail = HalfPlaneLp.LinearProgram2(lines, maxSpeed, prefVelocity, false, ref result);
        if (lineFail < lines.Length)
        {
            HalfPlaneLp.LinearProgram3(lines, 0, lineFail, maxSpeed, ref result);
        }

        return result;
    }

    // Build the single ORCA half-plane self must satisfy to avoid `other` for the horizon (the
    // polygonal-VO construction described in the class comment). Public for direct unit testing
    // against the disc solver.
    public static OrcaLine BuildAgentLine(in ShapedAgent self, in ShapedAgent other, double timeHorizon, double timeStep)
    {
        var rp = other.Position - self.Position;
        var rv = self.Velocity - other.Velocity;
        var p = self.Shape.MinkowskiSum(other.Shape.Reflected());   // collision set in rel-position space

        Vec2 boundary;
        Vec2 normal;

        if (p.ContainsPoint(rp))
        {
            // Already overlapping (rp inside the collision polygon). Separate over the SHORT time step
            // rather than the full horizon, like RVO2's overlap branch, using an anisotropic effective
            // radius (the collision polygon's extent along the push direction).
            var invTimeStep = 1.0 / timeStep;
            var w = rv - invTimeStep * rp;
            var wLen = w.Abs;
            var unitW = wLen > 1e-12 ? w / wLen : new Vec2(1.0, 0.0);
            var effRadius = p.SupportRadius(unitW);
            boundary = rv + (effRadius * invTimeStep - wLen) * unitW;   // point on the shrunk cutoff
            normal = unitW;
            return LineFrom(self, other, boundary, normal);
        }

        var invTau = 1.0 / timeHorizon;
        var q = Translate(p, rp);                 // collision polygon placed at the relative position
        var centroid = Centroid(q);
        var n = q.Verts.Length;

        var bestDistSq = double.PositiveInfinity;
        var haveBest = false;
        Vec2 bestB = Vec2.Zero;
        Vec2 bestN = Vec2.Zero;

        for (var i = 0; i < n; i++)
        {
            var a = q.Verts[i];
            var b2 = q.Verts[(i + 1) % n];
            var edge = b2 - a;
            var edgeOutward = new Vec2(edge.Y, -edge.X);   // perpCW: outward normal of a CCW edge

            var facesOrigin = Vec2.Dot(edgeOutward, -a) > 0.0;

            // Near-CAP candidate: the edge (scaled by 1/tau) is part of the VO boundary only if it
            // faces the origin. Project rv onto the scaled segment, CLAMPED to the segment -- unlike
            // the smooth disc cutoff, a polygon's nearest boundary point is frequently a VERTEX, so a
            // clamped endpoint hit is a real candidate (the shared vertex of two facing edges), not one
            // to discard. The min over all facing edges then yields the true nearest point on the cap.
            if (facesOrigin)
            {
                var sa = a * invTau;
                var sb = b2 * invTau;
                var seg = sb - sa;
                var segSq = seg.AbsSq;
                if (segSq > 1e-24)
                {
                    var t = Math.Clamp(Vec2.Dot(rv - sa, seg) / segSq, 0.0, 1.0);
                    var bpt = sa + t * seg;
                    var dsq = (rv - bpt).AbsSq;
                    if (dsq < bestDistSq)
                    {
                        bestDistSq = dsq;
                        bestB = bpt;
                        bestN = edgeOutward.Normalized();
                        haveBest = true;
                    }
                }
            }

            // LEG candidate: vertex i is a tangent vertex if its two incident edges disagree on
            // facing the origin. The leg is the ray from (1/tau)*vertex outward along unit(vertex).
            var prev = q.Verts[(i - 1 + n) % n];
            var prevEdge = a - prev;
            var prevOutward = new Vec2(prevEdge.Y, -prevEdge.X);
            var prevFaces = Vec2.Dot(prevOutward, -prev) > 0.0;
            if (prevFaces != facesOrigin)
            {
                var legDir = a.Normalized();
                if (legDir.AbsSq > 1e-24)
                {
                    var start = a * invTau;
                    var lambda = Math.Max(0.0, Vec2.Dot(rv - start, legDir));
                    var bpt = start + lambda * legDir;
                    var dsq = (rv - bpt).AbsSq;
                    if (dsq < bestDistSq)
                    {
                        // Outward normal: perpendicular to the leg, pointing away from the cone
                        // interior (away from the collision-polygon centroid).
                        var nrm = new Vec2(legDir.Y, -legDir.X);
                        if (Vec2.Dot(nrm, centroid - start) > 0.0)
                        {
                            nrm = -nrm;
                        }

                        bestDistSq = dsq;
                        bestB = bpt;
                        bestN = nrm;
                        haveBest = true;
                    }
                }
            }
        }

        if (!haveBest)
        {
            // Degenerate (e.g. rp on the polygon boundary within eps): fall back to a no-op far line
            // that never binds (allows the current velocity).
            var dir = rp.Abs > 1e-9 ? rp.Normalized() : new Vec2(1.0, 0.0);
            boundary = rv;
            normal = dir;
            return LineFrom(self, other, boundary, normal);
        }

        return LineFrom(self, other, bestB, bestN);
    }

    // Assemble the OrcaLine from the nearest boundary point and outward normal, applying the
    // responsibility split: u = boundary - rv, Point = self.Velocity + responsibility*u,
    // Direction = perpCW(normal) so the feasible half-plane is the +normal (outside-VO) side.
    private static OrcaLine LineFrom(in ShapedAgent self, in ShapedAgent other, Vec2 boundary, Vec2 normal)
    {
        var rv = self.Velocity - other.Velocity;
        var u = boundary - rv;
        var point = self.Velocity + other.Responsibility * u;
        var direction = new Vec2(normal.Y, -normal.X);   // perpCW(normal)
        return new OrcaLine(point, direction);
    }

    private static ConvexShape Translate(ConvexShape s, Vec2 by)
    {
        // Small helper: shift a shape's vertices. Kept local (ConvexShape stays immutable/centered).
        var verts = new Vec2[s.Verts.Length];
        for (var i = 0; i < verts.Length; i++)
        {
            verts[i] = s.Verts[i] + by;
        }

        return ConvexShape.FromVertsUnchecked(verts);
    }

    private static Vec2 Centroid(ConvexShape s)
    {
        var sum = Vec2.Zero;
        foreach (var v in s.Verts)
        {
            sum += v;
        }

        return sum / s.Verts.Length;
    }
}
