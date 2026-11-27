using System.Text.Json.Nodes;

namespace SlimDep.UnitTests.Analysis;

/// <summary>
/// End-to-end analyzer tests against a synthetic project: a .csproj, a hand-written
/// obj/project.assets.json and a fake NuGet cache holding real DLLs.
/// </summary>
public sealed class ProjectAnalyzerTests : IDisposable
{
    // Real assemblies used as fake package DLLs. Their public namespaces:
    //   SlimDep.Core.dll      → SlimDep.Core.Models, SlimDep.Core.Exceptions, ...
    //   FluentAssertions.dll  → FluentAssertions, FluentAssertions.*
    //   System.Text.Json.dll  → System.Text.Json (also provided by the shared framework)
    private static readonly string CoreDll = typeof(ScanResult).Assembly.Location;
    private static readonly string FaDll = typeof(FluentAssertions.AssertionExtensions).Assembly.Location;
    private static readonly string StjDll = typeof(System.Text.Json.JsonSerializer).Assembly.Location;

    private readonly string _root;
    private readonly string _projectDir;
    private readonly string _cacheDir;
    private readonly string _csproj;
    // Unique per test: AssetsFileParser caches namespaces by package id/version
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public ProjectAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"slimdep-pa-{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_root, "App");
        _cacheDir = Path.Combine(_root, "nuget");
        Directory.CreateDirectory(Path.Combine(_projectDir, "obj"));
        Directory.CreateDirectory(_cacheDir);
        _csproj = Path.Combine(_projectDir, "App.csproj");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Core => $"Pkg.Core{_suffix}";
    private string Fa => $"Pkg.Fa{_suffix}";

    // ── Multi-targeting ───────────────────────────────────────────────────────

    [Fact]
    public async Task MultiTarget_PackageOnlyInSecondTfm_IsAnalysed()
    {
        WriteCsprojWith(Fa);
        WriteAssets(
            direct: [Fa],
            targets: new()
            {
                ["net8.0"] = [],
                ["net48"] = [Pkg(Fa, FaDll)],
            });
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        result.TotalDirectPackages.Should().Be(1);
        result.UnusedPackages.Should().ContainSingle(p => p.Name == Fa);
    }

    [Fact]
    public async Task MultiTarget_UsingInsideIfBlock_CountsAsUsed()
    {
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: new() { ["net8.0"] = [], ["net48"] = [Pkg(Fa, FaDll)] });
        WriteSource("Program.cs", "#if NET48\nusing FluentAssertions;\n#endif\nclass P { }");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
    }

    // ── --include-transitive ──────────────────────────────────────────────────

    [Fact]
    public async Task Transitive_Off_TransitivePackagesNotReported()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(Pkg(Core, CoreDll, deps: Dep(Fa)), Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze(includeTransitive: false);

        result.UnusedPackages.Should().BeEmpty();
        result.TotalDirectPackages.Should().Be(1);
    }

    [Fact]
    public async Task Transitive_On_UnusedTransitivePackageReportedWithParent()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(Pkg(Core, CoreDll, deps: Dep(Fa)), Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze(includeTransitive: true);

        var pkg = result.UnusedPackages.Should().ContainSingle().Subject;
        pkg.Name.Should().Be(Fa);
        pkg.IsTransitive.Should().BeTrue();
        pkg.LineNumber.Should().BeNull();
        pkg.Reason.Should().Contain(Core);
        result.TotalDirectPackages.Should().Be(1, "transitive packages are not direct references");
    }

    // ── Project-file usings ───────────────────────────────────────────────────

    [Fact]
    public async Task UsingItemInCsproj_CountsAsUsed()
    {
        WriteCsproj($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="{Fa}" Version="1.0.0" />
                <Using Include="FluentAssertions" />
              </ItemGroup>
            </Project>
            """);
        WriteAssets(direct: [Fa], targets: Net8(Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task GeneratedGlobalUsingsFile_CountsAsUsed()
    {
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: Net8(Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "class P { }");
        string genDir = Path.Combine(_projectDir, "obj", "Debug", "net8.0");
        Directory.CreateDirectory(genDir);
        File.WriteAllText(Path.Combine(genDir, "App.GlobalUsings.g.cs"), "global using global::FluentAssertions;");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task UnusedDirectPackage_StillReported_WithLineNumber()
    {
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: Net8(Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        var pkg = result.UnusedPackages.Should().ContainSingle().Subject;
        pkg.IsTransitive.Should().BeFalse();
        pkg.LineNumber.Should().Be(3);
    }

    // ── SLIMDEP002: transitive package used directly ──────────────────────────

    [Fact]
    public async Task Undeclared_TransitivePackageUsedDirectly_IsReported()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(Pkg(Core, CoreDll, deps: Dep(Fa)), Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing FluentAssertions;");

        var result = await Analyze();

        var pkg = result.UndeclaredPackages.Should().ContainSingle().Subject;
        pkg.Name.Should().Be(Fa);
        pkg.IntroducedBy.Should().Equal(Core);
        pkg.UsedNamespaces.Should().Equal("FluentAssertions");
        pkg.Reason.Should().Contain("<PackageReference>");
        result.UnusedPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task Undeclared_FullyQualifiedUsage_IsReported()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(Pkg(Core, CoreDll, deps: Dep(Fa)), Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nclass P { FluentAssertions.Execution.AssertionScope S; }");

        var result = await Analyze();

        result.UndeclaredPackages.Should().ContainSingle(p => p.Name == Fa);
    }

    [Fact]
    public async Task Undeclared_ViaDirectMetapackage_NotReported()
    {
        string meta = $"Pkg.Meta{_suffix}";
        WriteCsprojWith(meta);
        WriteAssets(direct: [meta], targets: Net8(Pkg(meta, dll: null, deps: Dep(Fa)), Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using FluentAssertions;");

        var result = await Analyze();

        result.UndeclaredPackages.Should().BeEmpty("the metapackage is the declaration (like xunit → xunit.assert)");
    }

    [Fact]
    public async Task Undeclared_OnlyViaProjectReference_NotReported()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(
            Pkg(Core, CoreDll),
            Project("Lib", deps: Dep(Fa)),
            Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing FluentAssertions;");

        var result = await Analyze();

        result.UndeclaredPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task Undeclared_NamespaceAlsoExportedByDirectPackage_NotReported()
    {
        string other = $"Pkg.Other{_suffix}";
        WriteCsprojWith(other);
        // The direct package ships the same namespaces, so the usage can't be attributed
        WriteAssets(direct: [other], targets: Net8(Pkg(other, FaDll, deps: Dep(Fa)), Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using FluentAssertions;");

        var result = await Analyze();

        result.UndeclaredPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task Undeclared_ParentNamespaceOnly_NotReported()
    {
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: Net8(Pkg(Fa, FaDll, deps: Dep(Core)), Pkg(Core, CoreDll)));
        // Imports a child namespace that doesn't exist in Core; SlimDep.Core.Models is only a parent
        WriteSource("Program.cs", "using FluentAssertions;\nusing SlimDep.Core.Models.Generated;");

        var result = await Analyze();

        result.UndeclaredPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task Undeclared_FrameworkNamespace_NotReported()
    {
        string stj = $"Pkg.Stj{_suffix}";
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(Pkg(Core, CoreDll, deps: Dep(stj)), Pkg(stj, StjDll)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing System.Text.Json;");

        var result = await Analyze();

        result.UndeclaredPackages.Should().BeEmpty("System.Text.Json is provided by the shared framework");
    }

    [Fact]
    public async Task Undeclared_IgnoredPackage_NotReported()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(Pkg(Core, CoreDll, deps: Dep(Fa)), Pkg(Fa, FaDll)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing FluentAssertions;");

        var result = await Analyze(ignore: [Fa]);

        result.UndeclaredPackages.Should().BeEmpty();
    }

    // ── Version pins ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Pin_UnusedDirectReferenceRaisingTransitiveVersion_IsKeptNotUnused()
    {
        WriteCsprojWith(Core, Fa);
        WriteAssets(direct: [Core, Fa], targets: Net8(
            Pkg(Core, CoreDll, deps: Dep(Fa, "1.2.0")),
            Pkg(Fa, FaDll, version: "1.5.3")));
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
        var pin = result.PinnedPackages.Should().ContainSingle().Subject;
        pin.Name.Should().Be(Fa);
        pin.Version.Should().Be("1.5.3");
        pin.TransitiveVersion.Should().Be("1.2.0");
        pin.RequiredBy.Should().Equal(Core);
    }

    [Fact]
    public async Task Pin_TransitiveAsksForSameVersion_IsNotAPin_ReportedUnused()
    {
        WriteCsprojWith(Core, Fa);
        WriteAssets(direct: [Core, Fa], targets: Net8(
            Pkg(Core, CoreDll, deps: Dep(Fa, "1.5.3")),
            Pkg(Fa, FaDll, version: "1.5.3")));
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze();

        result.PinnedPackages.Should().BeEmpty();
        result.UnusedPackages.Should().ContainSingle(p => p.Name == Fa);
    }

    [Fact]
    public async Task Pin_RequiredByProjectReference_IsLabelled()
    {
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: Net8(
            Project("Lib", deps: Dep(Fa, "[1.0.0, )")),
            Pkg(Fa, FaDll, version: "2.0.0")));
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        result.PinnedPackages.Should().ContainSingle()
            .Which.RequiredBy.Should().Equal("project Lib");
    }

    [Fact]
    public async Task Pin_HighestLowerBoundIsTheFallbackVersion()
    {
        string other = $"Pkg.Other{_suffix}";
        WriteCsprojWith(Core, other, Fa);
        WriteAssets(direct: [Core, other, Fa], targets: Net8(
            Pkg(Core, CoreDll, deps: Dep(Fa, "1.0.0")),
            Pkg(other, StjDll, deps: Dep(Fa, "1.4.0")),
            Pkg(Fa, FaDll, version: "1.5.0")));
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing System.Text.Json;");

        var result = await Analyze();

        result.PinnedPackages.Should().ContainSingle().Which.TransitiveVersion.Should().Be("1.4.0");
    }

    [Fact]
    public async Task Pin_OnlyOnOneTargetFramework_IsKept_AndNamesThatTarget()
    {
        // net8.0: Core asks for the same version → redundant there.
        // net48:  Core asks for 1.0.0 → removing the reference would downgrade net48.
        WriteCsprojWith(Core, Fa);
        WriteAssets(direct: [Core, Fa], targets: new()
        {
            ["net8.0"] = [Pkg(Core, CoreDll, deps: Dep(Fa, "1.5.3")), Pkg(Fa, FaDll, version: "1.5.3")],
            ["net48"] = [Pkg(Core, CoreDll, deps: Dep(Fa, "1.0.0")), Pkg(Fa, FaDll, version: "1.5.3")],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty("removing it would downgrade the net48 build");
        var pin = result.PinnedPackages.Should().ContainSingle().Subject;
        pin.TransitiveVersion.Should().Be("1.0.0");
        pin.TargetFrameworks.Should().Equal("net48");
        pin.Reason.Should().Contain("on net48");
    }

    [Fact]
    public async Task Pin_OnEveryTarget_DoesNotListTargets()
    {
        WriteCsprojWith(Core, Fa);
        WriteAssets(direct: [Core, Fa], targets: new()
        {
            ["net8.0"] = [Pkg(Core, CoreDll, deps: Dep(Fa, "1.0.0")), Pkg(Fa, FaDll, version: "1.5.3")],
            ["net48"] = [Pkg(Core, CoreDll, deps: Dep(Fa, "1.0.0")), Pkg(Fa, FaDll, version: "1.5.3")],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze();

        result.PinnedPackages.Should().ContainSingle().Which.TargetFrameworks.Should().BeNull();
    }

    // ── SDK implicit packages ─────────────────────────────────────────────────

    [Fact]
    public async Task AutoReferencedSdkPackage_AndWhatItBringsIn_AreNeverReported()
    {
        // NETStandard.Library is marked autoReferenced by WriteAssets; give it assemblies and a dependency
        string platform = $"Pkg.Platform{_suffix}";
        string sdkVersion = $"2.0.{Random.Shared.Next(100_000)}"; // unique cache key
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: Net8(
            Pkg(Fa, FaDll),
            Pkg("NETStandard.Library", StjDll, version: sdkVersion, deps: Dep(platform)),
            Pkg(platform, CoreDll)));
        WriteSource("Program.cs", "using FluentAssertions;\nusing SlimDep.Core.Models;");

        var result = await Analyze(includeTransitive: true);

        result.UnusedPackages.Should().BeEmpty();
        result.UndeclaredPackages.Should().BeEmpty();
    }

    // ── Framework namespaces depend on the target framework ───────────────────

    [Fact]
    public async Task Undeclared_FrameworkNamespaceOnNetFramework_IsReported()
    {
        // System.Text.Json ships with .NET 8 but not with .NET Framework 4.8
        string stj = $"Pkg.Stj{_suffix}";
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: new()
        {
            ["net48"] = [Pkg(Core, CoreDll, deps: Dep(stj)), Pkg(stj, StjDll)],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing System.Text.Json;");

        var result = await Analyze();

        result.UndeclaredPackages.Should().ContainSingle(p => p.Name == stj);
    }

    [Fact]
    public async Task Undeclared_MultiTarget_NamespaceMissingFromOneFramework_IsReported()
    {
        string stj = $"Pkg.Stj{_suffix}";
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: new()
        {
            ["net8.0"] = [Pkg(Core, CoreDll, deps: Dep(stj)), Pkg(stj, StjDll)],
            ["net48"] = [Pkg(Core, CoreDll, deps: Dep(stj)), Pkg(stj, StjDll)],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing System.Text.Json;");

        var result = await Analyze();

        result.UndeclaredPackages.Should().ContainSingle(p => p.Name == stj,
            "the net48 build needs the package even though net8.0 has the namespace built in");
    }

    [Fact]
    public async Task Undeclared_PackageOnlyOnModernTarget_FrameworkExemptionStillApplies()
    {
        // The System.Text.Json package exists only in the net8.0 graph, where the framework provides
        // the namespace; net48 must not cancel that exemption.
        string stj = $"Pkg.Stj{_suffix}";
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: new()
        {
            ["net8.0"] = [Pkg(Core, CoreDll, deps: Dep(stj)), Pkg(stj, StjDll)],
            ["net48"] = [Pkg(Core, CoreDll)],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;\n#if NET8_0\nusing System.Text.Json;\n#endif");

        var result = await Analyze();

        result.UndeclaredPackages.Should().BeEmpty();
    }

    // ── Default safe list ─────────────────────────────────────────────────────

    [Fact]
    public async Task SafeList_AppliesToUnusedTransitivePackages()
    {
        const string bundle = "SQLitePCLRaw.bundle_green";
        string version = $"2.1.{Random.Shared.Next(100_000)}";
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: Net8(
            Pkg(Core, CoreDll, deps: Dep(bundle, version)),
            Pkg(bundle, FaDll, version: version)));
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze(includeTransitive: true);

        result.UnusedPackages.Should().BeEmpty();
        result.SkippedPackages.Should().ContainSingle(p => p.Name == bundle);

        var noIgnores = await Analyze(includeTransitive: true, useDefaultIgnores: false);
        noIgnores.UnusedPackages.Should().ContainSingle(p => p.Name == bundle && p.IsTransitive);
    }

    [Fact]
    public async Task SafeList_UnusedTestAdapter_IsSkippedNotUnused()
    {
        const string coverlet = "coverlet.collector";
        string version = $"6.0.{Random.Shared.Next(100_000)}"; // unique cache key
        WriteCsprojWith(coverlet);
        WriteAssets(direct: [coverlet], targets: Net8(Pkg(coverlet, FaDll, version: version)));
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
        result.SkippedPackages.Should().ContainSingle()
            .Which.Reason.Should().Contain("coverlet.*");
    }

    [Fact]
    public async Task SafeList_NoDefaultIgnores_ReportsUnused()
    {
        const string coverlet = "coverlet.collector";
        string version = $"6.0.{Random.Shared.Next(100_000)}";
        WriteCsprojWith(coverlet);
        WriteAssets(direct: [coverlet], targets: Net8(Pkg(coverlet, FaDll, version: version)));
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze(useDefaultIgnores: false);

        result.SkippedPackages.Should().BeEmpty();
        result.UnusedPackages.Should().ContainSingle(p => p.Name == coverlet);
    }

    [Fact]
    public async Task SafeList_PrivateAssetsAll_IsSkipped()
    {
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: Net8(Pkg(Fa, FaDll)), privateAssetsAll: [Fa]);
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
        result.SkippedPackages.Should().ContainSingle()
            .Which.Reason.Should().Contain("PrivateAssets");
    }

    [Fact]
    public async Task SafeList_UsedPackage_NotListedAsSkipped()
    {
        const string coverlet = "coverlet.collector";
        string version = $"6.0.{Random.Shared.Next(100_000)}";
        WriteCsprojWith(coverlet);
        WriteAssets(direct: [coverlet], targets: Net8(Pkg(coverlet, FaDll, version: version)));
        WriteSource("Program.cs", "using FluentAssertions;");

        var result = await Analyze();

        result.SkippedPackages.Should().BeEmpty("only packages that would have been reported are listed");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<ScanResult> Analyze(bool includeTransitive = false, bool useDefaultIgnores = true, string[]? ignore = null) =>
        new ProjectAnalyzer().AnalyzeAsync(new ScanOptions
        {
            ProjectPath = _csproj,
            IncludeTransitive = includeTransitive,
            UseDefaultIgnores = useDefaultIgnores,
            InlineIgnoredPackages = ignore,
        });

    private void WriteCsproj(string xml) => File.WriteAllText(_csproj, xml);

    private void WriteCsprojWith(params string[] packages) => WriteCsproj(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n"
        + string.Concat(packages.Select(p => $"    <PackageReference Include=\"{p}\" Version=\"1.0.0\" />\n"))
        + "  </ItemGroup>\n</Project>\n");

    private void WriteSource(string name, string content) =>
        File.WriteAllText(Path.Combine(_projectDir, name), content);

    private sealed record FakePackage(string Name, string? DllPath, string Version, Dictionary<string, string>? Deps, bool IsProject);

    private static FakePackage Pkg(string name, string? dll, string version = "1.0.0", Dictionary<string, string>? deps = null) =>
        new(name, dll, version, deps, IsProject: false);

    private static FakePackage Project(string name, Dictionary<string, string>? deps = null) =>
        new(name, null, "1.0.0", deps, IsProject: true);

    private static Dictionary<string, string> Dep(string name, string minVersion = "1.0.0") => new() { [name] = minVersion };

    private static Dictionary<string, FakePackage[]> Net8(params FakePackage[] packages) => new() { ["net8.0"] = packages };

    private void WriteAssets(string[] direct, Dictionary<string, FakePackage[]> targets, string[]? privateAssetsAll = null)
    {
        var libraries = new JsonObject();
        var targetsNode = new JsonObject();

        foreach (var (tfm, packages) in targets)
        {
            var tfmNode = new JsonObject();
            foreach (var pkg in packages)
            {
                string key = $"{pkg.Name}/{pkg.Version}";
                string relDir = $"{pkg.Name.ToLowerInvariant()}/{pkg.Version}";
                var entry = new JsonObject { ["type"] = pkg.IsProject ? "project" : "package" };

                if (pkg.DllPath is not null)
                {
                    string dllName = Path.GetFileName(pkg.DllPath);
                    string dest = Path.Combine(_cacheDir, relDir, "lib", "net8.0", dllName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(pkg.DllPath, dest, overwrite: true);
                    entry["compile"] = new JsonObject { [$"lib/net8.0/{dllName}"] = new JsonObject() };
                }
                else if (!pkg.IsProject)
                {
                    // Metapackage: no assemblies
                    entry["compile"] = new JsonObject { ["lib/netstandard1.0/_._"] = new JsonObject() };
                }

                if (!pkg.IsProject)
                    libraries[key] = new JsonObject { ["type"] = "package", ["path"] = relDir };

                if (pkg.Deps is not null)
                {
                    var deps = new JsonObject();
                    foreach (var (n, v) in pkg.Deps) deps[n] = v;
                    entry["dependencies"] = deps;
                }
                tfmNode[key] = entry;
            }
            targetsNode[tfm] = tfmNode;
        }

        var frameworks = new JsonObject();
        foreach (string tfm in targets.Keys)
        {
            var deps = new JsonObject();
            foreach (string d in direct)
            {
                var dep = new JsonObject { ["target"] = "Package", ["version"] = "[1.0.0, )" };
                if (privateAssetsAll?.Contains(d) == true) dep["suppressParent"] = "All";
                deps[d] = dep;
            }
            // Implicit SDK reference — must never be reported
            deps["NETStandard.Library"] = new JsonObject { ["target"] = "Package", ["autoReferenced"] = true };
            frameworks[tfm] = new JsonObject { ["dependencies"] = deps };
        }

        var doc = new JsonObject
        {
            ["version"] = 3,
            ["targets"] = targetsNode,
            ["libraries"] = libraries,
            ["packageFolders"] = new JsonObject { [_cacheDir + Path.DirectorySeparatorChar] = new JsonObject() },
            ["project"] = new JsonObject { ["frameworks"] = frameworks },
        };

        File.WriteAllText(Path.Combine(_projectDir, "obj", "project.assets.json"), doc.ToJsonString());
    }
}
