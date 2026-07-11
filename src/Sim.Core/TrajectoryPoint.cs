namespace Sim.Core;

// Lane-relative (Lane, Pos) is the source of truth per DESIGN.md; X/Y are derived.
//
// Perf (PERF-ROADMAP.md Layer 0a): a `readonly record struct`, NOT a heap `record`. The engine emits
// one of these per vehicle per step; as a struct it is stored inline in TrajectorySet's backing
// List<TrajectoryPoint> (no per-point object header, no GC object per emitted point). Field set and
// value semantics are byte-identical to the former record (record struct keeps positional
// construction + value equality), so every consumer -- TrajectoryComparator, FcdParser, Sim.Viz,
// the benches -- is unchanged.
public readonly record struct TrajectoryPoint(
    string VehicleId,
    double Time,
    string Lane,
    double Pos,
    double Speed,
    double X,
    double Y,
    double Angle,
    double? Acceleration);
