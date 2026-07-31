namespace Sim.ParityTests;

/// <summary>
/// Pins the three junction gates <c>Sim.Sumo.SumoShim</c> reads from the environment to the values the
/// <c>Engine</c> itself ships, and restores whatever was there before.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. SumoShim reads <c>SUMOSHARP_CONTTURNFIX</c>, <c>SUMOSHARP_ISLEADERFIX</c> and
/// <c>SUMOSHARP_INTERNALJUNCTIONFIX</c> with the unsafe two-state form
/// (<c>GetEnvironmentVariable(name) == "1"</c>), while all three <c>Engine</c> properties now default to
/// <c>true</c>. An UNSET variable therefore forces the gate OFF, and every shim invocation that does not
/// set them runs with three junction gates disabled that the engine, the goldens and the LiveCity host
/// all have enabled. That is the open bug documented in <c>docs/ENV-GATES.md</c>.
/// </para>
/// <para>
/// The consequence for tests is worse than for measurements: a shim-driven test that leaves them unset is
/// silently calibrated against a configuration THE ENGINE DOES NOT SHIP, and its constants then look like
/// regressions the moment anything moves. Measured when this helper was introduced, on
/// <c>scenarios/_repro/synthetic-junction2</c>: the low-density scenario reads 5 teleports unpinned and
/// 2 pinned, and the dense scenario 290 arrivals unpinned against 289 pinned -- so
/// <c>DenseFlowDeadLaneDrainTests</c>' historic <c>&gt;= 290</c> floor was never reachable in the shipped
/// configuration at all.
/// </para>
/// <para>
/// This is CLAUDE.md measurement-discipline #10 ("set every gate you care about EXPLICITLY, in BOTH arms")
/// applied to the test suite. Pinning does NOT paper over the shim bug -- it makes these tests measure the
/// engine we ship, which is what they were always meant to assert. Fixing SumoShim to use the safe
/// <c>EnvGate</c> form is tracked separately; when it lands, these calls become redundant rather than wrong.
/// </para>
/// <para>
/// These are PROCESS-GLOBAL variables, so every caller must already be in <see cref="SumoShimEnvCollection"/>
/// (xUnit runs it sequentially) or a concurrent test will observe the pinned value. Always restore in a
/// <c>finally</c>.
/// </para>
/// </remarks>
public static class JunctionGateEnv
{
    /// <summary>The gates SumoShim reads, paired with the Engine default each one must be pinned to.</summary>
    private static readonly (string Name, string EngineDefault)[] Gates =
    {
        ("SUMOSHARP_CONTTURNFIX", "1"),          // Engine.ContTurnInsideJunctionGate      = true
        ("SUMOSHARP_ISLEADERFIX", "1"),          // Engine.JunctionIsLeaderGate            = true
        ("SUMOSHARP_INTERNALJUNCTIONFIX", "1"),  // Engine.InternalJunctionAdmissionGate   = true
    };

    /// <summary>Sets all three gates to the Engine defaults; returns the previous values for <see cref="Restore"/>.</summary>
    public static string?[] PinToEngineDefaults()
    {
        var previous = new string?[Gates.Length];
        for (var i = 0; i < Gates.Length; i++)
        {
            previous[i] = Environment.GetEnvironmentVariable(Gates[i].Name);
            Environment.SetEnvironmentVariable(Gates[i].Name, Gates[i].EngineDefault);
        }

        return previous;
    }

    /// <summary>Restores the values captured by <see cref="PinToEngineDefaults"/>.</summary>
    public static void Restore(string?[] previous)
    {
        for (var i = 0; i < Gates.Length && i < previous.Length; i++)
        {
            Environment.SetEnvironmentVariable(Gates[i].Name, previous[i]);
        }
    }
}
