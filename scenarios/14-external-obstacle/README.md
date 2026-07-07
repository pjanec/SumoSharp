# 14-external-obstacle (B1 behavioral fixture)

No SUMO golden: the external obstacle is a live input not present in any offline SUMO run
(DESIGN.md "Two futures" / Group B). Validation is behavioral/property tests
(`RungB1ExternalObstacleTests`), not golden-FCD parity. The steady gap the follower holds behind
an injected obstacle is cross-checked against `scenarios/13-stopped-leader`'s SUMO golden
(follower front 242.499 = obstacle back 245 - minGap 2.5 - NUMERICAL_EPS).
