using System.IO;
using System.Linq;

namespace Sim.ParityTests;

// Packaging-layout guard for the SumoSharp NuGet story (docs/SUMOSHARP-PACKAGING-DESIGN.md §V2,
// decisions E1-E6). Like Rung B13, these are hermetic, source-only assertions: they read committed
// csproj/source files, touch no network, build no native libs, and run no simulation. They fail loudly
// if a future edit regresses the adoption-first packaging design -- e.g. an internal engine project
// re-acquiring its own PackageId, the portable bundle picking up a native dependency, or the DDS binding
// bundling the engine instead of depending on it.
//
// §V2 shape: the SHIPPED library surface is exactly TWO packages --
//   * SumoSharp      (packaging/SumoSharp)        -- the one portable engine package (Core..LiveCity
//                                                    bundled), net8.0 + netstandard2.1, no native dep.
//   * SumoSharp.Dds  (src/Sim.Replication.Dds)    -- the optional CycloneDDS binding, net8.0, native,
//                                                    depends on the SumoSharp package.
// Every other src/packaging project is internal/sample and must NOT be packable.
public class PackagingLayoutTests
{
    private const string BundleCsproj = "packaging/SumoSharp/SumoSharp.csproj";
    private const string DdsCsproj = "src/Sim.Replication.Dds/Sim.Replication.Dds.csproj";

    [Fact]
    public void ExactlyTwoPackagesArePackable_TheBundleAndTheDdsBinding()
    {
        // Scan every csproj under src/ and packaging/ and collect the ones marked IsPackable=true. The
        // adoption-first design (E1/E6) ships exactly two: the SumoSharp bundle and SumoSharp.Dds. A new
        // packable project (or an internal engine project re-acquiring IsPackable) trips this.
        var root = RepoRoot();
        var packable = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "packaging"), "*.csproj", SearchOption.AllDirectories))
            .Where(p => File.ReadAllText(p).Contains("<IsPackable>true</IsPackable>"))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, System.StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { BundleCsproj, DdsCsproj }.OrderBy(p => p, System.StringComparer.Ordinal).ToArray(),
            packable);
    }

    [Fact]
    public void Bundle_MultiTargets_IsPortable_AndPicksUpNoNativeDependency()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), BundleCsproj));

        // The one package a game engine installs (E1): multi-targeted so Unity (Mono/IL2CPP) and Godot
        // can consume it, packed under the top-level `SumoSharp` id.
        Assert.Contains("<TargetFrameworks>", csproj);
        Assert.Contains("net8.0", csproj);
        Assert.Contains("netstandard2.1", csproj);
        Assert.Contains("<IsPackable>true</IsPackable>", csproj);
        Assert.Contains("<PackageId>SumoSharp</PackageId>", csproj);

        // It bundles the portable engine assemblies (E2: bundle, don't merge). Match reference paths.
        foreach (var proj in new[]
                 {
                     "Sim.Core", "Sim.Ingest", "Sim.Replication", "Sim.Viewer.Motion",
                     "Sim.Host", "Sim.Pedestrians", "Sim.LiveCity", "Sim.Evac",
                 })
        {
            Assert.Contains($"{proj}.csproj", csproj);
        }

        // It must stay native-free: the portable package cannot drag CycloneDDS or the raylib/rlImgui
        // desktop stack (V2.1 -- native binaries are the ONLY thing that forces a separate package).
        Assert.DoesNotContain("CycloneDDS", csproj);
        Assert.DoesNotContain("Raylib", csproj);
        Assert.DoesNotContain("rlImgui", csproj);
        Assert.DoesNotContain("Sim.Replication.Dds.csproj", csproj);
        Assert.DoesNotContain("Sim.Viewer.Raylib.csproj", csproj);
    }

    [Fact]
    public void DdsBinding_IsNative_Net8Only_AndDependsOnTheBundle()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), DdsCsproj));

        // The optional native transport (E1/V1.3): net8.0 only (CycloneDDS.NET is net8.0), NOT
        // multi-targeted like the portable bundle.
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", csproj);
        Assert.DoesNotContain("<TargetFrameworks>", csproj);
        Assert.Contains("<IsPackable>true</IsPackable>", csproj);
        Assert.Contains("<PackageId>SumoSharp.Dds</PackageId>", csproj);
        Assert.Contains("CycloneDDS.NET", csproj);

        // It must DEPEND ON the SumoSharp engine package, not bundle the engine DLLs (which would
        // duplicate them for a consumer installing both). Referencing the packable bundle project makes
        // `dotnet pack` emit that dependency. Normalize slashes so the check is OS-agnostic.
        Assert.Contains("packaging\\SumoSharp\\SumoSharp.csproj", csproj.Replace('/', '\\'));
    }

    [Fact]
    public void TransportContract_IsDefinedInReplication_NotInDds()
    {
        // The transport-neutral replication contract (IReplicationSink/IReplicationSource) lives in
        // Sim.Replication -- shipped inside the portable SumoSharp package -- so a consumer coded against
        // these interfaces never needs to know CycloneDDS exists. If they were declared in (or duplicated
        // into) the DDS binding, that transport-neutrality guarantee would break.
        var replicationSource = File.ReadAllText(Path.Combine(RepoRoot(), "src/Sim.Replication/IReplication.cs"));
        Assert.Contains("interface IReplicationSink", replicationSource);
        Assert.Contains("interface IReplicationSource", replicationSource);

        var ddsDir = Path.Combine(RepoRoot(), "src/Sim.Replication.Dds");
        foreach (var file in Directory.GetFiles(ddsDir, "*.cs"))
        {
            var contents = File.ReadAllText(file);
            Assert.DoesNotContain("interface IReplicationSink", contents);
            Assert.DoesNotContain("interface IReplicationSource", contents);
        }
    }

    [Fact]
    public void PortableEngineProjects_MultiTarget_AndCarryNoNativeDependency()
    {
        // Every project bundled into the portable SumoSharp package must itself be portable: multi-target
        // net8.0 + netstandard2.1, and never reference a native/transport dependency. If one of them
        // acquired e.g. a raylib or CycloneDDS reference, the bundle would drag native binaries into the
        // package a Unity/Godot consumer installs (V2.1).
        foreach (var proj in new[]
                 {
                     "Sim.Core", "Sim.Ingest", "Sim.Replication", "Sim.Viewer.Motion",
                     "Sim.Host", "Sim.Pedestrians", "Sim.LiveCity", "Sim.Evac",
                 })
        {
            var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", proj, $"{proj}.csproj"));
            Assert.True(
                csproj.Contains("<TargetFrameworks>") && csproj.Contains("net8.0") && csproj.Contains("netstandard2.1"),
                $"{proj} must multi-target net8.0;netstandard2.1 to fold into the portable SumoSharp package.");

            // Match reference INCLUDEs, not bare substrings: the native package/project names legitimately
            // appear in explanatory comments (e.g. Sim.Replication's comment mentions CycloneDDS.NET,
            // Sim.LiveCity's mentions Raylib), so assert the actual PackageReference/ProjectReference forms
            // are absent rather than the words.
            Assert.DoesNotContain("Include=\"CycloneDDS", csproj);
            Assert.DoesNotContain("Include=\"Raylib", csproj);
            Assert.DoesNotContain("Include=\"rlImgui", csproj);
            Assert.DoesNotContain("Sim.Replication.Dds.csproj", csproj);
            Assert.DoesNotContain("Sim.Viewer.Raylib.csproj", csproj);
        }
    }

    // Walk up from the test assembly to the repo root (Traffic.sln), matching Rung B13's convention
    // -- no dependency on git at test time.
    private static string RepoRoot()
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
