namespace Sim.Core.Orca;

// The half-plane linear program at the heart of ORCA: given a set of half-plane velocity
// constraints (OrcaLine), a max-speed circle, and a preferred velocity, find the feasible velocity
// closest to preferred; on infeasibility, fall back to minimising the maximum constraint violation.
//
// This is a VERBATIM extraction of LinearProgram1/2/3 from OrcaSolver (RVO2's linearProgram1/2/3) --
// pure, allocation-light, deterministic, and SHAPE-AGNOSTIC: it operates only on OrcaLine half-planes
// and never sees agent geometry. It is shared by BOTH the disc OrcaSolver and the shaped
// (OBB/capsule) Mixed.ShapedVoSolver so the two produce identical results once their half-planes
// agree. Behaviour is unchanged from when these lived inside OrcaSolver (OrcaOpenSpaceTests guard it).
internal static class HalfPlaneLp
{
    // Guard against division by ~0 when two constraint lines are (almost) parallel.
    private const double Eps = 1e-10;

    // Optimise `result` along a single constraint line `lineNo`, subject to lines [0, lineNo) and the
    // maxSpeed circle. Returns false if the constraints are jointly infeasible for this line.
    internal static bool LinearProgram1(
        ReadOnlySpan<OrcaLine> lines, int lineNo, double radius, Vec2 optVelocity, bool directionOpt, ref Vec2 result)
    {
        var line = lines[lineNo];
        var dotProduct = Vec2.Dot(line.Point, line.Direction);
        var discriminant = dotProduct * dotProduct + radius * radius - line.Point.AbsSq;

        if (discriminant < 0.0)
        {
            // The maxSpeed circle does not intersect this line at all.
            return false;
        }

        var sqrtDiscriminant = Math.Sqrt(discriminant);
        var tLeft = -dotProduct - sqrtDiscriminant;
        var tRight = -dotProduct + sqrtDiscriminant;

        for (var i = 0; i < lineNo; i++)
        {
            var denominator = Vec2.Det(line.Direction, lines[i].Direction);
            var numerator = Vec2.Det(lines[i].Direction, line.Point - lines[i].Point);

            if (Math.Abs(denominator) <= Eps)
            {
                // (Almost) parallel. Infeasible if `line` is on the wrong side of line i.
                if (numerator < 0.0)
                {
                    return false;
                }

                continue;
            }

            var t = numerator / denominator;
            if (denominator >= 0.0)
            {
                tRight = Math.Min(tRight, t);   // line i bounds `line` on the right
            }
            else
            {
                tLeft = Math.Max(tLeft, t);     // line i bounds `line` on the left
            }

            if (tLeft > tRight)
            {
                return false;
            }
        }

        if (directionOpt)
        {
            // Optimise direction (used by the 3D LP): push to the far end along optVelocity's sense.
            result = Vec2.Dot(optVelocity, line.Direction) > 0.0
                ? line.Point + tRight * line.Direction
                : line.Point + tLeft * line.Direction;
        }
        else
        {
            // Optimise the closest point to optVelocity, clamped to [tLeft, tRight].
            var t = Vec2.Dot(line.Direction, optVelocity - line.Point);
            if (t < tLeft)
            {
                result = line.Point + tLeft * line.Direction;
            }
            else if (t > tRight)
            {
                result = line.Point + tRight * line.Direction;
            }
            else
            {
                result = line.Point + t * line.Direction;
            }
        }

        return true;
    }

    // 2D LP: velocity closest to optVelocity satisfying every half-plane, inside the maxSpeed circle.
    // Returns lines.Length on success, or the index of the first line that made it infeasible.
    internal static int LinearProgram2(
        ReadOnlySpan<OrcaLine> lines, double radius, Vec2 optVelocity, bool directionOpt, ref Vec2 result)
    {
        if (directionOpt)
        {
            // optVelocity is assumed to be a unit direction here; scale to the speed circle.
            result = optVelocity * radius;
        }
        else if (optVelocity.AbsSq > radius * radius)
        {
            result = optVelocity.Normalized() * radius;
        }
        else
        {
            result = optVelocity;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (Vec2.Det(lines[i].Direction, lines[i].Point - result) > 0.0)
            {
                // `result` violates constraint i; re-optimise along line i.
                var tempResult = result;
                if (!LinearProgram1(lines, i, radius, optVelocity, directionOpt, ref result))
                {
                    result = tempResult;
                    return i;
                }
            }
        }

        return lines.Length;
    }

    // 3D LP fallback for the infeasible case: from `beginLine` on, minimise the maximum constraint
    // violation. Static-obstacle lines occupy lines[0..numObstLines) and are already absolute
    // half-planes (not derived relative to any other line), so the projected LP for line i starts as
    // a direct COPY of them, and only the agent lines [numObstLines, i) get pairwise-intersected
    // against line i (mirrors RVO2's linearProgram3 exactly).
    internal static void LinearProgram3(
        ReadOnlySpan<OrcaLine> lines, int numObstLines, int beginLine, double radius, ref Vec2 result)
    {
        var distance = 0.0;
        // At most (lines.Length - 1) projected constraints for any single i.
        Span<OrcaLine> projLines = lines.Length <= 64
            ? stackalloc OrcaLine[lines.Length]
            : new OrcaLine[lines.Length];

        for (var i = beginLine; i < lines.Length; i++)
        {
            if (Vec2.Det(lines[i].Direction, lines[i].Point - result) > distance)
            {
                // Seed with the obstacle lines verbatim (they need no intersection with line i: they
                // are fixed constraints already expressed in absolute (point, direction) form).
                lines[..numObstLines].CopyTo(projLines);
                var projCount = numObstLines;

                for (var j = numObstLines; j < i; j++)
                {
                    OrcaLine projLine;
                    var determinant = Vec2.Det(lines[i].Direction, lines[j].Direction);

                    if (Math.Abs(determinant) <= Eps)
                    {
                        // i and j parallel.
                        if (Vec2.Dot(lines[i].Direction, lines[j].Direction) > 0.0)
                        {
                            continue;   // same direction -> j imposes nothing new
                        }

                        // Opposite directions: split the difference.
                        projLine = new OrcaLine(
                            0.5 * (lines[i].Point + lines[j].Point),
                            (lines[j].Direction - lines[i].Direction).Normalized());
                    }
                    else
                    {
                        var point = lines[i].Point
                            + (Vec2.Det(lines[j].Direction, lines[i].Point - lines[j].Point) / determinant)
                              * lines[i].Direction;
                        projLine = new OrcaLine(point, (lines[j].Direction - lines[i].Direction).Normalized());
                    }

                    projLines[projCount++] = projLine;
                }

                var tempResult = result;
                var dirOpt = new Vec2(-lines[i].Direction.Y, lines[i].Direction.X);
                if (LinearProgram2(projLines[..projCount], radius, dirOpt, true, ref result) < projCount)
                {
                    // Should not happen in principle; keep the safe (feasible) value.
                    result = tempResult;
                }

                distance = Vec2.Det(lines[i].Direction, lines[i].Point - result);
            }
        }
    }
}
