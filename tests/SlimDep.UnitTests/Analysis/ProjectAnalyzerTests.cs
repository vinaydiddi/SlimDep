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
    private static readonly string CoreDll = typeof(ScanResult).Assembly.Location;
    private static readonly string FaDll = typeof(FluentAssertions.AssertionExtensions).Assembly.Location;

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

    // ── Bug: only the first target framework was analysed ─────────────────────

    [Fact]
    public async Task MultiTarget_PackageOnlyInSecondTfm_IsAnalysed()
    {
        WriteCsproj($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="{Fa}" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        WriteAssets(
            direct: [Fa],
            targets: new()
            {
                ["net8.0"] = [],
                ["net48"] = [Package(Fa, FaDll)],
            });
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        result.TotalDirectPackages.Should().Be(1);
        result.UnusedPackages.Should().ContainSingle(p => p.Name == Fa);
    }

    [Fact]
    public async Task MultiTarget_UsingInsideIfBlock_CountsAsUsed()
    {
        WriteCsproj($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="{Fa}" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        WriteAssets(direct: [Fa], targets: new() { ["net8.0"] = [], ["net48"] = [Package(Fa, FaDll)] });
        WriteSource("Program.cs", """
            #if NET48
            using FluentAssertions;
            #endif
            class P { }
            """);

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
    }

    // ── Bug: --include-transitive did nothing ────────────────────────────────

    [Fact]
    public async Task Transitive_Off_TransitivePackagesNotReported()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: new()
        {
            ["net8.0"] = [Package(Core, CoreDll, dependsOn: Fa), Package(Fa, FaDll)],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze(includeTransitive: false);

        result.UnusedPackages.Should().BeEmpty();
        result.TotalDirectPackages.Should().Be(1);
    }

    [Fact]
    public async Task Transitive_On_UnusedTransitivePackageReportedWithParent()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: new()
        {
            ["net8.0"] = [Package(Core, CoreDll, dependsOn: Fa), Package(Fa, FaDll)],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;");

        var result = await Analyze(includeTransitive: true);

        var pkg = result.UnusedPackages.Should().ContainSingle().Subject;
        pkg.Name.Should().Be(Fa);
        pkg.IsTransitive.Should().BeTrue();
        pkg.LineNumber.Should().BeNull();
        pkg.Reason.Should().Contain(Core);
        result.TotalDirectPackages.Should().Be(1, "transitive packages are not direct references");
    }

    [Fact]
    public async Task Transitive_On_UsedTransitivePackageNotReported()
    {
        WriteCsprojWith(Core);
        WriteAssets(direct: [Core], targets: new()
        {
            ["net8.0"] = [Package(Core, CoreDll, dependsOn: Fa), Package(Fa, FaDll)],
        });
        WriteSource("Program.cs", "using SlimDep.Core.Models;\nusing FluentAssertions;");

        var result = await Analyze(includeTransitive: true);

        result.UnusedPackages.Should().BeEmpty();
    }

    // ── Bug: usings declared in the project file were ignored ────────────────

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
        WriteAssets(direct: [Fa], targets: new() { ["net8.0"] = [Package(Fa, FaDll)] });
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        result.UnusedPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task GeneratedGlobalUsingsFile_CountsAsUsed()
    {
        WriteCsprojWith(Fa);
        WriteAssets(direct: [Fa], targets: new() { ["net8.0"] = [Package(Fa, FaDll)] });
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
        WriteAssets(direct: [Fa], targets: new() { ["net8.0"] = [Package(Fa, FaDll)] });
        WriteSource("Program.cs", "class P { }");

        var result = await Analyze();

        var pkg = result.UnusedPackages.Should().ContainSingle().Subject;
        pkg.IsTransitive.Should().BeFalse();
        pkg.LineNumber.Should().Be(3);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<ScanResult> Analyze(bool includeTransitive = false) =>
        new ProjectAnalyzer().AnalyzeAsync(new ScanOptions { ProjectPath = _csproj, IncludeTransitive = includeTransitive });

    private void WriteCsproj(string xml) => File.WriteAllText(_csproj, xml);

    private void WriteCsprojWith(string package) => WriteCsproj($"""
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="{package}" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """);

    private void WriteSource(string name, string content) =>
        File.WriteAllText(Path.Combine(_projectDir, name), content);

    private sealed record FakePackage(string Name, string DllPath, string? DependsOn);

    private static FakePackage Package(string name, string dllPath, string? dependsOn = null) => new(name, dllPath, dependsOn);

    private void WriteAssets(string[] direct, Dictionary<string, FakePackage[]> targets)
    {
        var libraries = new JsonObject();
        var targetsNode = new JsonObject();

        foreach (var (tfm, packages) in targets)
        {
            var tfmNode = new JsonObject();
            foreach (var pkg in packages)
            {
                string key = $"{pkg.Name}/1.0.0";
                string dllName = Path.GetFileName(pkg.DllPath);
                string relDir = $"{pkg.Name.ToLowerInvariant()}/1.0.0";

                string dest = Path.Combine(_cacheDir, relDir, "lib", "net8.0", dllName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(pkg.DllPath, dest, overwrite: true);

                libraries[key] = new JsonObject { ["type"] = "package", ["path"] = relDir };

                var entry = new JsonObject
                {
                    ["type"] = "package",
                    ["compile"] = new JsonObject { [$"lib/net8.0/{dllName}"] = new JsonObject() },
                };
                if (pkg.DependsOn is not null)
                    entry["dependencies"] = new JsonObject { [pkg.DependsOn] = "1.0.0" };
                tfmNode[key] = entry;
            }
            targetsNode[tfm] = tfmNode;
        }

        var frameworks = new JsonObject();
        foreach (string tfm in targets.Keys)
        {
            var deps = new JsonObject();
            foreach (string d in direct)
                deps[d] = new JsonObject { ["target"] = "Package", ["version"] = "[1.0.0, )" };
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
