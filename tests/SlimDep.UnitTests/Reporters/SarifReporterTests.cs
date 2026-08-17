namespace SlimDep.UnitTests.Reporters;

public sealed class SarifReporterTests
{
    private static readonly IReporter Reporter = new SarifReporter();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScanResult MakeResult(string projectPath, params UnusedPackage[] packages) =>
        new()
        {
            ProjectPath = projectPath,
            UnusedPackages = packages,
            TotalDirectPackages = packages.Length + 2,
            Duration = TimeSpan.FromMilliseconds(50),
        };

    private static UnusedPackage Pkg(string name, string version = "1.0.0", int? line = null) =>
        new(name, version, "No namespace from this package appears in any using directive")
        {
            LineNumber = line,
        };

    // ── Schema & version ──────────────────────────────────────────────────────

    [Fact]
    public void Render_AlwaysEmitsSarifVersionAndSchema()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var root = JsonNode.Parse(output)!;

        root["version"]!.GetValue<string>().Should().Be("2.1.0");
        root["$schema"]!.GetValue<string>().Should().Contain("sarif");
    }

    [Fact]
    public void Render_HasExactlyOneRun()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var runs = JsonNode.Parse(output)!["runs"]!.AsArray();

        runs.Should().HaveCount(1);
    }

    // ── Tool descriptor ───────────────────────────────────────────────────────

    [Fact]
    public void Render_ToolDriverContainsSlimDepMetadata()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var driver = JsonNode.Parse(output)!["runs"]![0]!["tool"]!["driver"]!;

        driver["name"]!.GetValue<string>().Should().Be("SlimDep");
        driver["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        driver["informationUri"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Render_ToolDriverContainsRuleDefinition()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var rules = JsonNode.Parse(output)!["runs"]![0]!["tool"]!["driver"]!["rules"]!.AsArray();

        rules.Should().HaveCount(1);
        rules[0]!["id"]!.GetValue<string>().Should().Be("SLIMDEP001");
        rules[0]!["defaultConfiguration"]!["level"]!.GetValue<string>().Should().Be("warning");
    }

    // ── Results: no findings ──────────────────────────────────────────────────

    [Fact]
    public void Render_NoUnusedPackages_EmitsEmptyResultsArray()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var results = JsonNode.Parse(output)!["runs"]![0]!["results"]!.AsArray();

        results.Should().BeEmpty();
    }

    // ── Results: one unused package ───────────────────────────────────────────

    [Fact]
    public void Render_OneUnusedPackage_EmitsOneResult()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json", "13.0.3"));
        var output = Reporter.Render(result);
        var results = JsonNode.Parse(output)!["runs"]![0]!["results"]!.AsArray();

        results.Should().HaveCount(1);
    }

    [Fact]
    public void Render_OneUnusedPackage_ResultHasCorrectRuleId()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Serilog", "3.0.0"));
        var output = Reporter.Render(result);
        var r = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!;

        r["ruleId"]!.GetValue<string>().Should().Be("SLIMDEP001");
    }

    [Fact]
    public void Render_OneUnusedPackage_LevelIsWarning()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("SomePackage"));
        var output = Reporter.Render(result);
        var r = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!;

        r["level"]!.GetValue<string>().Should().Be("warning");
    }

    [Fact]
    public void Render_OneUnusedPackage_MessageContainsPackageNameAndVersion()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json", "13.0.3"));
        var output = Reporter.Render(result);
        var messageText = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!["message"]!["text"]!.GetValue<string>();

        messageText.Should().Contain("Newtonsoft.Json");
        messageText.Should().Contain("13.0.3");
    }

    [Fact]
    public void Render_OneUnusedPackage_MessageContainsReason()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("SomePackage"));
        var output = Reporter.Render(result);
        var messageText = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!["message"]!["text"]!.GetValue<string>();

        messageText.Should().Contain("No namespace from this package appears in any using directive");
    }

    [Fact]
    public void Render_OneUnusedPackage_LocationPointsToCsproj()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "MyApp.csproj");
        var result = MakeResult(projectPath, Pkg("SomePackage"));
        var output = Reporter.Render(result);
        var uri = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!["locations"]![0]!
            ["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>();

        uri.Should().StartWith("file:///");
        uri.Should().Contain("MyApp.csproj");
    }

    // ── Line number in region ─────────────────────────────────────────────────

    [Fact]
    public void Render_PackageWithLineNumber_EmitsRegionWithStartLine()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("SomePackage", line: 12));
        var output = Reporter.Render(result);
        var region = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!["locations"]![0]!
            ["physicalLocation"]!["region"];

        region.Should().NotBeNull();
        region!["startLine"]!.GetValue<int>().Should().Be(12);
    }

    [Fact]
    public void Render_PackageWithoutLineNumber_OmitsRegion()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("SomePackage", line: null));
        var output = Reporter.Render(result);
        var physicalLocation = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!["locations"]![0]!
            ["physicalLocation"]!.AsObject();

        physicalLocation.ContainsKey("region").Should().BeFalse();
    }

    // ── Multiple unused packages ──────────────────────────────────────────────

    [Fact]
    public void Render_MultipleUnusedPackages_EmitsOneResultPerPackage()
    {
        var result = MakeResult(@"C:\Repo\App.csproj",
            Pkg("Newtonsoft.Json"),
            Pkg("Serilog"),
            Pkg("AutoMapper"));
        var output = Reporter.Render(result);
        var results = JsonNode.Parse(output)!["runs"]![0]!["results"]!.AsArray();

        results.Should().HaveCount(3);
    }

    // ── Artifacts ─────────────────────────────────────────────────────────────

    [Fact]
    public void Render_SingleProject_ListsOneArtifact()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "MyApp.csproj");
        var result = MakeResult(projectPath);
        var output = Reporter.Render(result);
        var artifacts = JsonNode.Parse(output)!["runs"]![0]!["artifacts"]!.AsArray();

        artifacts.Should().HaveCount(1);
        artifacts[0]!["location"]!["uri"]!.GetValue<string>().Should().Contain("MyApp.csproj");
    }

    // ── RenderAll (multi-project) ─────────────────────────────────────────────

    [Fact]
    public void RenderAll_MultipleProjects_StillOneRun()
    {
        var results = new[]
        {
            MakeResult(@"C:\Repo\Api\Api.csproj", Pkg("Newtonsoft.Json")),
            MakeResult(@"C:\Repo\Core\Core.csproj"),
        };
        var output = Reporter.RenderAll(results);
        var runs = JsonNode.Parse(output)!["runs"]!.AsArray();

        runs.Should().HaveCount(1);
    }

    [Fact]
    public void RenderAll_MultipleProjects_AllFindingsInOneResultsArray()
    {
        var results = new[]
        {
            MakeResult(@"C:\Repo\Api\Api.csproj", Pkg("Newtonsoft.Json"), Pkg("Serilog")),
            MakeResult(@"C:\Repo\Core\Core.csproj", Pkg("AutoMapper")),
        };
        var output = Reporter.RenderAll(results);
        var sarifResults = JsonNode.Parse(output)!["runs"]![0]!["results"]!.AsArray();

        sarifResults.Should().HaveCount(3);
    }

    [Fact]
    public void RenderAll_MultipleProjects_AllProjectsListedAsArtifacts()
    {
        var results = new[]
        {
            MakeResult(@"C:\Repo\Api\Api.csproj"),
            MakeResult(@"C:\Repo\Core\Core.csproj"),
            MakeResult(@"C:\Repo\Web\Web.csproj"),
        };
        var output = Reporter.RenderAll(results);
        var artifacts = JsonNode.Parse(output)!["runs"]![0]!["artifacts"]!.AsArray();

        artifacts.Should().HaveCount(3);
    }

    [Fact]
    public void RenderAll_EmptyList_ProducesValidSarifWithNoResults()
    {
        var output = Reporter.RenderAll([]);
        var root = JsonNode.Parse(output)!;

        root["version"]!.GetValue<string>().Should().Be("2.1.0");
        root["runs"]![0]!["results"]!.AsArray().Should().BeEmpty();
        root["runs"]![0]!["artifacts"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void RenderAll_MixedCleanAndUnclean_OnlyUncleanProjectsContributeResults()
    {
        var results = new[]
        {
            MakeResult(@"C:\Repo\Api\Api.csproj", Pkg("Newtonsoft.Json")),
            MakeResult(@"C:\Repo\Core\Core.csproj"),          // clean — no unused
        };
        var output = Reporter.RenderAll(results);
        var sarifResults = JsonNode.Parse(output)!["runs"]![0]!["results"]!.AsArray();

        sarifResults.Should().HaveCount(1);
        sarifResults[0]!["message"]!["text"]!.GetValue<string>().Should().Contain("Newtonsoft.Json");
    }

    // ── Output is valid JSON ──────────────────────────────────────────────────

    [Fact]
    public void Render_OutputIsValidJson()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("SomePackage"));
        var output = Reporter.Render(result);

        var act = () => JsonDocument.Parse(output);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderAll_OutputIsValidJson()
    {
        var results = new[]
        {
            MakeResult(@"C:\Repo\Api\Api.csproj", Pkg("Newtonsoft.Json")),
            MakeResult(@"C:\Repo\Core\Core.csproj"),
        };
        var output = Reporter.RenderAll(results);

        var act = () => JsonDocument.Parse(output);
        act.Should().NotThrow();
    }

    // ── File URI format ───────────────────────────────────────────────────────

    [Fact]
    public void Render_ArtifactUri_IsAbsoluteFileUri()
    {
        string projectPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MyApp.csproj"));
        var result = MakeResult(projectPath);
        var output = Reporter.Render(result);
        var uri = JsonNode.Parse(output)!["runs"]![0]!["artifacts"]![0]!["location"]!["uri"]!.GetValue<string>();

        uri.Should().StartWith("file:///");
        Uri.IsWellFormedUriString(uri, UriKind.Absolute).Should().BeTrue();
    }

    // ── Package version edge cases ────────────────────────────────────────────

    [Fact]
    public void Render_PackageWithEmptyVersion_MessageExcludesVersion()
    {
        var pkg = new UnusedPackage("SomePackage", string.Empty, "No namespace found.");
        var result = MakeResult(@"C:\Repo\App.csproj", pkg);
        var output = Reporter.Render(result);
        var messageText = JsonNode.Parse(output)!["runs"]![0]!["results"]![0]!["message"]!["text"]!.GetValue<string>();

        messageText.Should().Contain("SomePackage");
        messageText.Should().NotContain("SomePackage ");  // no trailing space before dot
    }
}
