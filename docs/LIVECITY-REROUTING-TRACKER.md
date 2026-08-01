# LIVECITY-REROUTING-TRACKER — checklist

Design: `LIVECITY-REROUTING-DESIGN.md` · Tasks: `LIVECITY-REROUTING-TASKS.md`

- [x] **OWNER SIGN-OFF** — Aug 1: "let's pls implemente the rerouting behind a gate" (opt-in posture)
- [x] T1 config pass-through + env gates — landed (`66684f0`); off = no splice (stream-identical OFF arm verified), env overrides live, ENV-GATES rows + doc test green
- [x] T2 — `LiveCityReroutingTests`: off ⇒ 0 installs; on ⇒ two runs byte-identical + installs > 0; green
- [x] T3 — journal Entry 44: 800-car closed-loop A/B, arrivals 2069 → **2810 (+36%)**, live 691→534, stoppedFrac 0.44→0.37, ~2000 reroutes/1200 s; OFF arm stream-identical; `LIVECITY-REROUTES` witness line live. (Gated hour-horizon test variant deferred until after owner validation — noted per tasks doc.)
- [x] T4 owner validation — owner: "prolongs time to the gridlock, city seems a bit more live — definitely good direction"; 3D hour-horizon A/B +3.6%/+5.2% both gate arms, 0 long stalls (journal Entry 46; the honest realistic-topology figure is +4–5%, NOT Entry 44's box +36%)
- [x] Default-flip — owner picked prob 1.0 ("all drivers can have navigation"); **DEFAULT ON since Entry 47** (period 60 s), `LIVECITY_REROUTE=0` kill switch; full sln green with the device on; ENV-GATES updated
