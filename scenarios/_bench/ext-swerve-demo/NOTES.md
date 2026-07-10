# ext-swerve-demo — external-agent lateral swerve (B6)

Demonstrates the B6 lateral-evasion behaviour: a SUMO car **swerves within its lane** around an
external pedestrian that jumps in too close to stop, then recenters. Driven by `Sim.ExtDemo` +
the engine's `AddObstacle(..., latPos, width, latSpeed)` lateral API (EXTERNAL-AGENTS-VIZ.md).

## Behaviour (verified from the combined FCD)
Car cruises at 13.89 m/s, lane centre y=-3.00. At t=10 the pedestrian jumps in 8 m ahead (pos 146.9)
and lunges LEFT at 2.5 m/s (`latSpeed`, faster than the car's 2 m/s swerve). The car cannot stop in
time, so it **brakes to 4.89 m/s AND swerves 0.65 m to the RIGHT at t=11** (predictively dodging the
side the pedestrian is vacating), clears it with no collision, then recenters and re-accelerates to
free flow by t=15. Max lateral deviation 0.65 m.

## How it's wired
- `external-agents.json` carries the real B6 lateral fields (`latPos`, `width`, `latSpeed`) which
  `Sim.ExtDemo/Program.cs` passes straight to `AddObstacle`. width>0 gives a partial footprint the
  engine swerves around (width 0 = full-lane block → car stops, see ext-agents-demo).
- The car renders its own swerve (its emitted x/y includes `Kinematics.LatOffset`). The pedestrian
  is drawn by `CombinedFcdObserver` via `LaneGeometry.PositionAtOffset(shape, frontPos-length/2,
  latPos)` — the SAME lane->world transform the engine's collision math uses, so the drawn agent and
  the footprint the car reacts to line up exactly.
- Phone-viewable via hosted artifact; `combined.fcd.xml`/`replay.html` are regenerated, gitignored.
  Slow the replay to 0.25x to watch the ~1 s dodge.
