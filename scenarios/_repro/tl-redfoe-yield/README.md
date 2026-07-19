# tl-redfoe-yield — minimal witness: SumoSharp yields a green movement to a red-light foe

A 2-car, 1-junction, geometry-free reproduction of the **dominant** traffic-light gridlock
mechanism on the real box (the residual that survived the P2-G Bug-2 RBL fix). It isolates the
bug to a single green permissive-left movement held by a single red-light-stopped foe.

## The net

One traffic-light junction `C` with four 120 m single-lane approaches (N/S/E/W). The TL runs a
single fixed phase `rrrGGgrrrGGg` for the whole run: **E/W green, N/S red** (the E/W permissive
lefts are `g`). Two cars, both departing t=0 at max speed:

- **`e_left`** — E→S left turn = link 5, **GREEN** (permissive). No oncoming W→E through is
  present, so it has a clear path.
- **`n_left`** — N→E left turn = link 2, **RED** the whole run. It approaches and stops at the
  stop line.

## Vanilla vs SumoSharp

| | vanilla 1.20.0 (`van.fcd.xml`) | SumoSharp **baseline** | SumoSharp **+ Bug-3 fix** (`ss.fcd.xml`) |
|---|---|---|---|
| `e_left` (green) | crosses at t≈9 (`:C_5_0`→`:C_13_0`→`CtoS_0`), arrives | **frozen on `EtoC_0`**, never crosses | **crosses** (enters `:C_5_0` at t≈12), arrives |
| `n_left` (red) | waits at the stop line | waits at the stop line | waits at the stop line |

`e_left` run **alone** (delete `n_left`) crosses cleanly in SumoSharp even at baseline — so the
baseline freeze was caused *solely* by the presence of the red-light foe.

**Status: FIXED (freeze) by P2-G Bug-3.** With the crossing gate made TL-aware (see below),
`e_left` is no longer frozen — it crosses and arrives like vanilla. A small residual remains: it
crosses ~3 steps later than vanilla (enters the junction at t≈12 vs t≈9), because the minor-link
cautious-approach arm (`couldBrakeForMinor`) still brakes a permissive-**green** TL link toward its
stop line as if it might meet a foe, adding hesitation vanilla does not have. That residual is a
smaller, separate follow-up (call it Bug-4) — it slows a green permissive movement by a few seconds
but does not freeze it, so it is a believability/tempo gap, not gridlock. `ss.fcd.xml` is the
**+ Bug-3** run.

## Root cause (P2-G Bug-3)

`Engine.JunctionYieldConstraint` (the crossing gate, ~Engine.cs:6392) yields ego to an approaching
foe when `foe.WillPass` is true. `WillPass` means "the foe's planned vNext carries it into the
junction this step" — i.e. it is still *moving*. During t=7–9 the red-light `n_left` is still
rolling toward its stop line (it does not reach speed 0 until t≈10), so it is marked
`WillPass=true`, and the green `e_left` — which statically `respondsTo` link 2 (a minor permissive
left) — yields to it. It then never gets a gap, because `n_left` sits at the red forever.

Vanilla does **not** yield here: SUMO's foe check (`MSLink::opened` / `havePriority`) is
TL-state-aware — a foe approaching on a **red** signal has no right-of-way and does not block a
green ego. The engine's gate is TL-state-blind for the approaching foe: it reads the static
`response`/`foe` matrix and the foe's motion, but never checks the foe link's live signal state.

This is the same class of TL-blindness as Bug-2 (the RBL cycle resolver), but in the **main**
crossing gate rather than the deadlock-breaker — and it is the larger contributor: every green
movement that shares a static foe relationship with a red-waiting car is held, which cascades into
the whole-junction stalls Geneva measured (halting climbing while vanilla flows).

## Fix (implemented — P2-G Bug-3)

In the approaching-foe branch of `JunctionYieldConstraint`, a foe whose live TL link state is red
(`r`) is treated as non-blocking (`FoeApproachingOnRedSignal`, guarded so a rail_signal's Tl id —
which is not a traffic-light program — is never mis-read as red) — mirroring `MSLink::opened`,
which only yields to foes that currently hold right-of-way. Validated: all 622 committed goldens
stay byte-identical (the red-foe situation never arises in the sparse committed TL goldens, so the
check is inert there), and on the `synthetic-junction2` witness the mid-run arrival lag drops ~75%
(peak on-net halting 107→83 toward vanilla's 45; teleports 17→11).

## Reproduce

```
sumo      -c config.sumocfg --end 30 --no-step-log true --fcd-output van.fcd.xml
<sumosharp> -c config.sumocfg --end 30 --no-step-log true --fcd-output ss.fcd.xml
# diff the two: e_left crosses in van, freezes in ss.
```
