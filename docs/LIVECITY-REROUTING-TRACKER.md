# LIVECITY-REROUTING-TRACKER — checklist

Design: `LIVECITY-REROUTING-DESIGN.md` · Tasks: `LIVECITY-REROUTING-TASKS.md`

- [ ] **OWNER SIGN-OFF of the design doc** (gates everything below; the design's one open
      decision is the rollout posture, DESIGN §2.4 — recommendation: opt-in first)
- [ ] T1 config pass-through + env gates (LiveCityConfig fields, XML splice, `LIVECITY_REROUTE*`
      vars, ENV-GATES rows; success conditions T1.1–T1.4)
- [ ] T2 determinism with the device ON (new LiveCity.Tests case, reroute-count guard;
      T2.1–T2.3)
- [ ] T3 behavioural A/B (witness line, smoke OFF vs ON, hour-horizon ON; journal entry with
      BEFORE-predictions; T3.1–T3.4)
- [ ] T4 owner 3D validation on Geneva + default-flip decision (T4)
