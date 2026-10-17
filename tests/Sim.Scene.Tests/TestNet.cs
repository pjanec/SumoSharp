using Sim.Ingest;

namespace Sim.Scene.Tests;

internal static class TestNet
{
    private static NetworkModel? _box;

    // scenarios/_ped/demo_city/box/net.xml, parsed once.
    public static NetworkModel Box => _box ??= NetworkParser.Parse(Path.Combine(
        RepoRoot(), "scenarios", "_ped", "demo_city", "box", "net.xml"));

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Traffic.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (Traffic.sln not found above test assembly).");
    }
}
