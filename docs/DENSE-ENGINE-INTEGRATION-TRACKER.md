# Dense-flow engine integration — TRACKER

Status for `docs/DENSE-ENGINE-INTEGRATION-TASKS.md`. **Method: full branch merge** (owner decision) —
conflict-free, carries the branch's parity-tested test updates + dedicated tests automatically.

## Done — verified first-hand on the merged tree
- [x] **Merge** `claude/dense-lane-overlap-fix-5tr4ha` into the live-city line — **conflict-free**
- [x] **Build** — whole solution (`Traffic.sln`) clean
- [x] **Parity** — `Sim.ParityTests` = **657 passed / 4 skipped** (654 → 657: +3 dense-flow tests;
      every pre-existing golden byte-identical)
- [x] **Determinism/bench** — no `System.Random`; `deterministic=True`, `parallel==single=True`;
      hash **D96213B7BB4021A7 UNCHANGED**
- [x] **#15 gridlock A/B** — @160: arrivals 38→**81**, end stoppedFrac 0.75→**0.38**, jam-and-recover;
      @70: arrivals 16→**36**, end stoppedFrac 0.82→**0.50** (meets success conditions)
- [ ] **Commit + push** the merge (fetch+rebase-merges first)

## Follow-on (already landed by the merge, no separate task)
- [x] **T7** — the branch's dedicated parity tests (`DenseFlowDeadLaneDrainTests`,
      `RungHDp0c2ParkingLotReuseTests`, `RungHDp0c3BaseDepartPosTests`) + `_repro` scenarios came in
      with the merge and pass.

## Notes / results log
- Gridlock is *improved, not fully cured*: jams still form to ~0.6–0.7 stoppedFrac but now recover
  (terminal-lock → jam-and-recover); throughput ~2× at both densities. Residual = the branch's own
  unresolved item (turn-lane segregation, partly reverted) + no teleport ported. Chase separately if
  the residual jamming is still visible/unacceptable in the GPU viewer.
