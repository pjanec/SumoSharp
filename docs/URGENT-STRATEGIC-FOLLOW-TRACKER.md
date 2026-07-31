# URGENT-STRATEGIC-FOLLOW — tracker

Tasks: `URGENT-STRATEGIC-FOLLOW-TASKS.md` · Design: `URGENT-STRATEGIC-FOLLOW-DESIGN.md`

- [x] T0.1 probe constraint (committed default-OFF; goldens inert both states; SUMO's move reproduced to two decimals; L2 collapse measured)
- [x] OWNER SIGN-OFF on the design ("go", session log)
- [x] T1.1 brake-without-change attribution instrument (LcStrategicOutcome × binder 18; Entry 26)
- [x] T1.2 verdict: safety gaps, not extra vetoes; missing informFollower; then the REAL cause — the stop-pin defeats our reroute exit (Entry 26)
- [x] T2 the scoped fix: follower half ported (binder 19) + pair scoped to the MOVING-merge regime (Entry 26)
- [x] T2.5 attribution (Entry 27): coupling EXONERATED — 0 of the ON-arm pairs involve a recent moving change; both arms share ONE pileup-episode defect class (distinct pairs 77 vs 66); 21-vs-9 was peak-metric episode size
- [x] T2.6 fix the internal-lane pileup (Entry 29): third `CrossJunctionLeaderConstraint` walk over the ACTUAL lane's connection path when ego is off-pool (`BuildActualDownstreamSpan`). Goldens inert; L2 overlaps OFF 9→1, ON 21→3; arrived up both arms; battery clean (four flagged rows proven pre-existing by stash A/B). **All §5 gates green with the coupling ON**
- [x] T3.0 (unplanned, Entry 30): Entry 29's numbers were racy -- latent parallel-plan race in the approach arm's pre-pass `foe.WillPass` read; guarded via `WillPassPrev` (two variants refuted by measurement first); parallel==serial byte-identical
- [x] T3.1 full measurement sweep on the deterministic engine (Entry 31 table -- every §5 gate green)
- [x] T3.2 default flip: `UrgentStrategicLeaderFollow = true`; shim reads the gate in EnvGate form; ENV-GATES + MustUseSafeForm updated; battery reports committed (`net-regression-urgentfollow-{on,off}.txt`)
- [x] T3.3 `UrgentStrategicFollowBehaviourTests` -- pins the L2-light at-speed change, forced-OFF vacuity guard
