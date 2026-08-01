# JUNCTION-FOE-LANE — tracker

Design: `JUNCTION-FOE-LANE-DESIGN.md` · Tasks: `JUNCTION-FOE-LANE-TASKS.md` · Evidence: journal
Entry 35. **Operational hand-off page: `JUNCTION-FOE-LANE-RESUME.md` — a cold start begins THERE.** A box is ticked only when the reviewer has confirmed the task's success conditions
first-hand (CLAUDE.md orchestration loop).

- [x] **OWNER SIGN-OFF on the design** — "go autonomously", July 31 (owner session)
- [ ] F0.1 foes-vs-response source verification (+ TraCI probe) — **source half DONE, answers in design §2** (foes bitstring, not response; cited); TraCI probe pending
- [ ] F1.1 conflict geometry at ingest (parity-inert, all-nets sweep)
- [~] F2.1 foe-lane occupancy arm — the F3 workstream had ALREADY built it (`JunctionPhysicalOccupancyGate`); re-plumbed as `SUMOSHARP_PHYSOCCUPANCY` (both drivers + ENV-GATES). Crossing half live under the gate; bay half partial (Entry 35b): `BayConflict` ingest geometry ✓, `_physOnLane*` physical index ✓, bay arm ✓ but hold-timing unresolved (early → gridlock, late → misses)
- [ ] **F2.1c (NEW, next session): degenerate-bay WAIT-POINT relocation** — when a bay's waiting position lies inside a BayConflict interval, `InternalJunctionAdmissionConstraint` holds the turner at the junction ENTRY instead of in the bay; plus one episode trace of the recurring dwell-634 gridlock site before any threshold tuning
- [x] F2.2 same-target merge half — foes-based reachability + `IsLeaderByEntryOrder` PHASE-1 tie-break (Entry 35b): landing onsets 12→5 deadlock-free, gate-off byte-identical, suite 779/5/0. Full ≤2 target re-checked at the F3.1 gate ladder
- [ ] F3.1 full gate ladder, both states, predictions-first
- [ ] F3.2 default flip + docs
