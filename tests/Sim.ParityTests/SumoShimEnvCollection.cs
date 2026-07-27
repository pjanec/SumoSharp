using Xunit;

namespace Sim.ParityTests;

// The xUnit collection that SERIALIZES every test class driving Sim.Sumo.SumoShim.Run.
//
// WHY THIS EXISTS. SumoShim reads the PROCESS-WIDE environment variable SUMOSHARP_CONTTURNFIX to set
// Engine.ContTurnInsideJunctionGate (src/Sim.Sumo/SumoShim.cs:250). IgnoreJunctionBlockerTests SETS
// that variable around its own shim runs (it is the only way to A/B a non-SUMO engine flag through the
// SUMO-compatible CLI surface). Under xUnit's DEFAULT behaviour, distinct test COLLECTIONS run in
// PARALLEL -- and since a class with no [Collection] attribute forms its own collection, every shim
// test was previously eligible to run concurrently with that mutation. An environment variable is
// process-global, so a concurrent shim test could silently simulate with the cont-turn gate ON when it
// meant OFF.
//
// THIS WAS OBSERVED, NOT THEORISED. LowDensityTeleportTests failed 1 of 3 full-suite runs reporting
// exactly 5 teleports against its `<= 2` ceiling, while passing every standalone run. The mechanism was
// then reproduced deterministically:
//
//     SUMOSHARP_CONTTURNFIX=1 dotnet test tests/Sim.ParityTests -c Release \
//         --filter "FullyQualifiedName~LowDensityTeleportTests"
//
// fails with that same "fired 5 teleports (jam=0, yield=5)" message, while the same command with the
// variable unset passes. 5 is precisely the cont-turn-gate-ON teleport count for that scenario.
//
// WHY IT MATTERS MORE THAN AN ORDINARY FLAKE. LowDensityTeleportTests and DenseFlowDeadLaneDrainTests
// are two of the five gridlock diagnostics that are this repo's regression net for junction changes
// (docs/F3-SESSION-LOG.md §2 / §7 Lesson 1 -- goldens alone are NOT sufficient evidence for a junction
// change). A diagnostic that can go red for reasons unrelated to the change under test is worse than
// no diagnostic: it costs a session chasing a regression that does not exist, and it teaches the reader
// to discount a real failure.
//
// CONTRACT FOR NEW TESTS: if your test class calls SumoShim.Run, annotate it
// `[Collection(SumoShimEnvCollection.Name)]`. Tests within one collection run sequentially, so the
// mutation can no longer overlap a reader.
//
// This serialization fixes the RACE. It does not remove the process-global read itself, which stays
// a latent hazard for any future in-process concurrent consumer (a parallel harness, a benchmark
// driving the shim). That deeper fix is docs/NEED-sumoshim-process-global-contturn-env.md.
[CollectionDefinition(Name)]
public sealed class SumoShimEnvCollection
{
    public const string Name = "SumoShim process-global env (SUMOSHARP_CONTTURNFIX)";
}
