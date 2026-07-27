// Sim.LiveCity.Tests touches PROCESS-GLOBAL state -- several probes/diagnostics set LIVECITY_* environment
// variables (e.g. HeadOfQueueStallProbeTests sets LIVECITY_CARS + the gate vars, LongHorizonGridlockDiagTests
// the gates) which LiveCityConfig.ForRepoRoot reads. xUnit runs test COLLECTIONS in parallel by default, so
// those env writes race the config construction in other tests (notably the DenseFlow throughput/gridlock
// test), making results depend on scheduling. Disable collection parallelization for this assembly so every
// test sees a stable environment -- a correctness requirement for env-var-driven tests, not a perf tweak.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
