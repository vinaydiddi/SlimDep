using System.CommandLine;
using System.Text.Json.Nodes;
using SlimDep.Cli.Commands;

namespace SlimDep.UnitTests.Cli;

/// <summary>Runs the real commands in-process and checks exit codes and file effects.</summary>
[Collection("Console")] // commands write to Console; keep these tests sequential
public sealed class CommandTests : IDisposable
{
    private readonly string _dir;

    public CommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"slimdep-cmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<int> Run(params string[] args)
    {
        var root = new RootCommand();
        root.Subcommands.Add(new ScanCommand());
        root.Subcommands.Add(new FixCommand());
        root.Subcommands.Add(new IgnoreCommand());

        var originalOut = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            return await root.Parse(args).InvokeAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    // ── ignore add ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IgnoreAdd_FileWithoutTrailingNewline_KeepsEntriesSeparate()
    {
        string file = Path.Combine(_dir, ".slimdepignore");
        File.WriteAllText(file, "# comment\nMicrosoft.Extensions.Hosting"); // no final newline

        int exit = await Run("ignore", "add", "Foo", "--path", _dir);

        exit.Should().Be(0);
        File.ReadAllLines(file).Should().Equal("# comment", "Microsoft.Extensions.Hosting", "Foo");
    }

    [Fact]
    public async Task IgnoreAdd_FileWithTrailingNewline_NoBlankLineAdded()
    {
        string file = Path.Combine(_dir, ".slimdepignore");
        File.WriteAllText(file, "A\n");

        await Run("ignore", "add", "B", "--path", _dir);

        File.ReadAllLines(file).Should().Equal("A", "B");
    }

    // ── exit codes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_UnexpectedFailure_ExitsTwo_AndOtherProjectsStillScanned()
    {
        // Project A has a structurally invalid assets file (throws a non-SlimDep exception)
        string a = Path.Combine(_dir, "A");
        Directory.CreateDirectory(Path.Combine(a, "obj"));
        File.WriteAllText(Path.Combine(a, "A.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(a, "obj", "project.assets.json"), """{ "targets": 5 }""");

        // Project B is fine and clean
        WriteProject("B", direct: [], transitive: []);

        string report = Path.Combine(_dir, "report.json");
        int exit = await Run("scan", _dir, "--format", "json", "--output", report);

        exit.Should().Be(2, "an unexpected error is an error, not 'unused packages found'");
        JsonNode.Parse(File.ReadAllText(report))!["projectPath"]!.GetValue<string>().Should().EndWith("B.csproj");
    }

    [Fact]
    public async Task Scan_OnlyTransitiveFindings_ExitsZero()
    {
        string project = WriteProject("C", direct: ["Pkg.Direct"], transitive: ["Pkg.Transitive"]);
        File.WriteAllText(Path.Combine(_dir, "C", "Program.cs"), "using SlimDep.Core.Models;");

        int plain = await Run("scan", project);
        string report = Path.Combine(_dir, "t.json");
        int withTransitive = await Run("scan", project, "--include-transitive", "--format", "json", "--output", report);

        plain.Should().Be(0);
        // Make sure the scenario really has a transitive finding…
        JsonNode.Parse(File.ReadAllText(report))!["unusedPackages"]!.AsArray()
            .Should().ContainSingle(p => p!["transitive"]!.GetValue<bool>());
        // …and that it doesn't fail the scan
        withTransitive.Should().Be(0, "transitive findings are informational");
    }

    /// <summary>
    /// Writes a restored project. Every package ships SlimDep.Core.dll (namespaces SlimDep.Core.*),
    /// unique per test run so the namespace cache can't interfere; the direct packages depend on the transitive ones.
    /// </summary>
    private string WriteProject(string name, string[] direct, string[] transitive)
    {
        string dir = Path.Combine(_dir, name);
        string cache = Path.Combine(_dir, "nuget");
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        string csproj = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        string version = $"1.0.{Random.Shared.Next(1_000_000)}";
        var target = new JsonObject();
        var libraries = new JsonObject();
        var deps = new JsonObject();
        foreach (string pkg in direct.Concat(transitive))
        {
            // Direct packages ship SlimDep.Core.dll; transitive ones FluentAssertions.dll (unused by the code)
            string dll = direct.Contains(pkg)
                ? typeof(ScanResult).Assembly.Location
                : typeof(FluentAssertions.AssertionExtensions).Assembly.Location;
            string dllName = Path.GetFileName(dll);
            string rel = $"{pkg.ToLowerInvariant()}/{version}";
            string dest = Path.Combine(cache, rel, "lib", "net8.0", dllName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(dll, dest, overwrite: true);
            libraries[$"{pkg}/{version}"] = new JsonObject { ["type"] = "package", ["path"] = rel };

            var entry = new JsonObject
            {
                ["type"] = "package",
                ["compile"] = new JsonObject { [$"lib/net8.0/{dllName}"] = new JsonObject() },
            };
            if (direct.Contains(pkg) && transitive.Length > 0)
            {
                var d = new JsonObject();
                foreach (string t in transitive) d[t] = version;
                entry["dependencies"] = d;
            }
            target[$"{pkg}/{version}"] = entry;
        }
        foreach (string pkg in direct)
            deps[pkg] = new JsonObject { ["target"] = "Package", ["version"] = $"[{version}, )" };

        var doc = new JsonObject
        {
            ["version"] = 3,
            ["targets"] = new JsonObject { ["net8.0"] = target },
            ["libraries"] = libraries,
            ["packageFolders"] = new JsonObject { [cache + Path.DirectorySeparatorChar] = new JsonObject() },
            ["project"] = new JsonObject { ["frameworks"] = new JsonObject { ["net8.0"] = new JsonObject { ["dependencies"] = deps } } },
        };
        File.WriteAllText(Path.Combine(dir, "obj", "project.assets.json"), doc.ToJsonString());
        return csproj;
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;
