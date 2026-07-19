using Sim.Sumo;

// GAP-1: the `sumosharp` drop-in binary's entrypoint -- a one-line delegate over the testable
// SumoShim.Run (see SumoShim.cs for the flag contract). Kept trivial so the whole CLI contract is
// exercised in-process by the GAP-1 parity test without shelling out.
return SumoShim.Run(args, Console.Out, Console.Error);
