using System;
using System.Collections.Generic;
using Sim.Core.Orca;
using Sim.Pedestrians.Demand;

namespace Sim.Pedestrians;

// P8-4a (docs/PEDESTRIAN-P8-4-DENSITY-DESIGN.md; COORDINATION-pedestrian-x-subarea.md §3 row 5): the
// dialable ped-density knob. Maps a dial in [0,1] (0 = empty, 1 = the SAFE MAXIMUM) to a
// (PopulationCap, SpawnRatePerSecond) pair for a PedDemandConfig, anchored -- like the SumoData vehicle
// calibration (knee_veh_lkm at lane_km) -- on a PER-LENGTH density: pedestrians per walkable-km. Length,
// not area, because walkable area double-counts overlapping junction polygons (inflating a cap in the
// UNSAFE direction), whereas summed sidewalk length is overlap-free, monotone, and conservative.
//
// Pure & deterministic: arithmetic on committed inputs (sidewalk lengths + dial), no RNG, no engine, no
// SUMO. Additive/inert: it only produces numbers a caller chooses to feed into PedDemandConfig -- the
// default demand path and every committed golden are untouched.
public static class PedDensityKnob
{
    // Safe reference: peds per walkable-km at dial=1. ~250/km at ~2 m effective sidewalk width is ~0.5
    // ped/m^2 -- LoS C (free/steady flow), well below the ~1.5-2 ped/m^2 stop-and-go jam onset, so a
    // full-dial crowd flows rather than locking a crossing. This is the STATIC half of the
    // crossing-throughput guard (design §3): the always-on conservative ceiling.
    public const double SafePedsPerWalkableKmDefault = 250.0;

    // Mean trip duration used by Little's law (lambda = N / T) to turn a sustained population into an
    // arrival rate. A caller with a measured mean trip time for its box passes its own value.
    public const double MeanTripSecondsDefault = 90.0;

    public readonly record struct Result(int PopulationCap, double SpawnRatePerSecond, double WalkableLengthKm, double DensityFraction);

    // Total walkable length (km) = sum of the sidewalk-lane polyline lengths / 1000. Overlap-free (unlike
    // baked-polygon area), so it is a conservative, monotone anchor for the density model.
    public static double WalkableLengthKm(PedNetwork network)
    {
        if (network is null)
        {
            throw new ArgumentNullException(nameof(network));
        }

        var metres = 0.0;
        foreach (var lane in network.Sidewalks)
        {
            metres += PolylineLength(lane.Shape);
        }

        return metres / 1000.0;
    }

    // Compute the (cap, rate) pair for a dial over a network. `dial` is clamped to [0,1] -- the safe-range
    // enforcement: the emitted cap can never exceed the dial=1 safe ceiling.
    public static Result ForNetwork(
        PedNetwork network,
        double dial,
        double safePedsPerWalkableKm = SafePedsPerWalkableKmDefault,
        double meanTripSeconds = MeanTripSecondsDefault)
    {
        if (safePedsPerWalkableKm < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(safePedsPerWalkableKm), "safe density must be >= 0.");
        }

        if (meanTripSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(meanTripSeconds), "mean trip time must be > 0.");
        }

        var d = Math.Clamp(dial, 0.0, 1.0);
        var lengthKm = WalkableLengthKm(network);

        // Round-half-away-from-zero on a non-negative value == round-half-up; floor at 0 defensively.
        var cap = (int)Math.Max(0.0, Math.Round(d * safePedsPerWalkableKm * lengthKm, MidpointRounding.AwayFromZero));
        var rate = cap / meanTripSeconds; // Little's law: arrival rate to sustain `cap` live peds

        return new Result(cap, rate, lengthKm, d);
    }

    // Returns a copy of `config` with PopulationCap / SpawnRatePerSecond overwritten from the knob. Every
    // other field (Seed, WeightedEndpoints, Liveliness, radii, ...) is carried through unchanged, so the
    // knob composes with the P8-3 weighted sub-area demand: dial the density, keep the legitimate O/D.
    public static PedDemandConfig Apply(
        PedDemandConfig config,
        PedNetwork network,
        double dial,
        double safePedsPerWalkableKm = SafePedsPerWalkableKmDefault,
        double meanTripSeconds = MeanTripSecondsDefault)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        var r = ForNetwork(network, dial, safePedsPerWalkableKm, meanTripSeconds);
        return new PedDemandConfig
        {
            Origins = config.Origins,
            Destinations = config.Destinations,
            SpawnRatePerSecond = r.SpawnRatePerSecond,
            PopulationCap = r.PopulationCap,
            Seed = config.Seed,
            MaxSpeed = config.MaxSpeed,
            Radius = config.Radius,
            ArrivalRadius = config.ArrivalRadius,
            Liveliness = config.Liveliness,
            WeightedEndpoints = config.WeightedEndpoints,
        };
    }

    private static double PolylineLength(IReadOnlyList<Vec2> shape)
    {
        if (shape is null || shape.Count < 2)
        {
            return 0.0;
        }

        var total = 0.0;
        for (var i = 1; i < shape.Count; i++)
        {
            total += (shape[i] - shape[i - 1]).Abs;
        }

        return total;
    }
}
