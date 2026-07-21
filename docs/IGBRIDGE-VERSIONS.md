# IgBridge — version markers

Milestone tags for the IgBridge PoC. The git tag `igbridge-v1-usable` was created locally on the
commit below; the remote git proxy only accepts pushes to the working branch (tag/other refs 403),
so this file is the durable, pushed marker.

## `igbridge-v1-usable` — commit `c593558`
**First owner-signed-off usable version.** Owner: *"non-sharp turns now looking very real! mark this
version pls, first one usable."*

What it delivers (all render-side; parity 654 pass / 4 skip byte-identical):
- Deterministic 10 Hz SumoSharp → IG-native sample stream `[entityId, x, y, z, headingDeg, t]` with
  SUMO motion artifacts baked out.
- Kinematic no-slip rear-axle drag — rear tracks *inside* the front arc like a real car (drawn
  rear-bumper slip matches an ideal bicycle to <0.15°); substepped for high-yaw fidelity (§5.5).
- Zero-lag constant-velocity (g-h) front tracking — no post-turn "beginner-driver" overshoot (§5.6).
- Gentle lane changes via a bounded decaying-lateral-error model — SUMO's instant ~3.2 m lateral snap
  becomes a ~10–25 °/s yaw instead of a 70–130 °/s rotation jump, with no reseed spikes (§5.8).
- Clean 6×6 grid test bed (`subarea-box`), FakeIg 2-sample replay, side-by-side `Sim.Viz` render with
  click-to-identify.

Known residuals at this tag (see §5.9):
- Sharp low-radius turns: the rear off-tracks aggressively (faithful to the bicycle model, since the
  front follows the lane centerline — a real driver would take a wider turn-in line). Refinement:
  anticipatory turn-in.
- Fixed just after the tag (T2.0f): stationary vehicles drifted the center ~6 cm ("dancing on the
  spot" / "backward movement") — the g-h velocity overshooting a hard decel. Resolved by clamping the
  tracked front velocity to the known vehicle speed.
