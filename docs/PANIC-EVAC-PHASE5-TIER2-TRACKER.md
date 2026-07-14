# PANIC-EVAC-PHASE5-TIER2-TRACKER.md — 10k-city optimization checklist

Status for Phase-5 Tier 2 (`PANIC-EVAC-PHASE5-TIER2-DESIGN.md` / `-TASKS.md`). Priority is set by the
**measured** Tier-1 profile: the two O(m²) crowd solvers (pusher 40 % + pedestrian 34 %) come first, then
the city-size items (10k scenario, viz payload, auto-track scan). Both new hashes are opt-in + proven
bit-identical; parity hash `909605E965BFFE59` stays unmoved throughout.

> **Status:** design-first docs drafted; **awaiting owner go for Tier2-B1.**

## STAGE T2-S1 — spatial-hash the two crowd solvers
- [ ] **T2.1** `MixedTrafficCrowd` uniform-grid neighbour query (opt-in, `Array.Sort` candidate gather; exact Position+Heading equality test incl. MaxNeighbours + non-holonomic path) — #1 hotspot
- [ ] **T2.2** enable `OrcaCrowd.UseSpatialHash` for the evac ped crowd (`EvacConfig` flag; demo-level grid==brute signature test) — #2 hotspot
- [ ] **T2.3** heavy-load micro-benchmark (N≈250/1000/2000, brute vs grid, both solvers) — proves the reason for the work

## STAGE T2-S2 — the 10k demo + payload/scan handling
- [ ] **T2.4** 10k host scenario (spike: 2-lane `--rand` @10k → adopt; else fall back to committed `city-15000`; decision logged)
- [ ] **T2.5** `EvacCityScenario` (auto-track; large central incident sized to trap low-thousands; hashes on; deterministic; locality holds)
- [ ] **T2.6** viz payload management (region-crop / decimation / cap, every drop logged; Opus renders to confirm)
- [ ] **T2.7** before/after 10k cost profile (hashes off vs on) + auto-track scan measurement + verdict

## Deferred (measurement-gated)
- [ ] spatial `QueryNear` / disc feeds — only if T2.7 shows disc feeds material at 10k (Tier-1 < 0.5 %)
- [ ] FearField grid — only if fear update material (Tier-1 ~1 %)
- [ ] auto-track scan optimization (→ becomes T2.8) — only if T2.7 shows it material

---

### Proposed batches
- **Tier2-B1:** T2.1 + T2.2 + T2.3 — core algorithmic win (crowd-solver hashes), self-contained.
- **Tier2-B2:** T2.4 + T2.5 + T2.6 + T2.7 — 10k demo + closing before/after profile.
