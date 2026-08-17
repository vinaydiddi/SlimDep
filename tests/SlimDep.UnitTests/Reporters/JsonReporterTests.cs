namespace SlimDep.UnitTests.Reporters;

public sealed class JsonReporterTests
{
    private static readonly IReporter Reporter = new JsonReporter();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScanResult MakeResult(string projectPath, params UnusedPackage[] packages) =>
        new()
        {
            ProjectPath = projectPath,
            UnusedPackages = packages,
            TotalDirectPackages = packages.Length + 3,
            Duration = TimeSpan.FromMilliseconds(120),
        };

    private static UnusedPackage Pkg(string name, string version = "1.0.0") =>
        new(name, version, "No namespace from this package appears in any using directive");

    // ── Output is valid JSON ──────────────────────────────────────────────────

    [Fact]
    public void Render_OutputIsValidJson()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json"));
        var output = Reporter.Render(result);

        var act = () => JsonDocument.Parse(output);
        act.Should().NotThrow();
    }

    // ── Top-level fields ──────────────────────────────────────────────────────

    [Fact]
    public void Render_IncludesProjectPath()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var doc = JsonNode.Parse(output)!;

        doc["projectPath"]!.GetValue<string>().Should().Be(@"C:\Repo\App.csproj");
    }

    [Fact]
    public void Render_IncludesTotalDirectPackages()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var doc = JsonNode.Parse(output)!;

        doc["totalDirectPackages"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void Render_IncludesDurationMs()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var doc = JsonNode.Parse(output)!;

        doc["durationMs"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(0);
    }

    // ── No unused ─────────────────────────────────────────────────────────────

    [Fact]
    public void Render_NoUnusedPackages_UnusedCountIsZero()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var doc = JsonNode.Parse(output)!;

        doc["unusedCount"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void Render_NoUnusedPackages_UnusedPackagesArrayIsEmpty()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);
        var doc = JsonNode.Parse(output)!;

        doc["unusedPackages"]!.AsArray().Should().BeEmpty();
    }

    // ── Has unused ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_OneUnusedPackage_UnusedCountIsOne()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Serilog", "3.0.0"));
        var output = Reporter.Render(result);
        var doc = JsonNode.Parse(output)!;

        doc["unusedCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Render_OneUnusedPackage_PackageNamePresent()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Serilog", "3.0.0"));
        var output = Reporter.Render(result);
        var pkg = JsonNode.Parse(output)!["unusedPackages"]![0]!;

        pkg["name"]!.GetValue<string>().Should().Be("Serilog");
    }

    [Fact]
    public void Render_OneUnusedPackage_PackageVersionPresent()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Serilog", "3.0.0"));
        var output = Reporter.Render(result);
        var pkg = JsonNode.Parse(output)!["unusedPackages"]![0]!;

        pkg["version"]!.GetValue<string>().Should().Be("3.0.0");
    }

    [Fact]
    public void Render_OneUnusedPackage_ReasonPresent()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Serilog"));
        var output = Reporter.Render(result);
        var pkg = JsonNode.Parse(output)!["unusedPackages"]![0]!;

        pkg["reason"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Render_MultipleUnusedPackages_CountMatchesArrayLength()
    {
        var result = MakeResult(@"C:\Repo\App.csproj",
            Pkg("Pkg1"), Pkg("Pkg2"), Pkg("Pkg3"));
        var output = Reporter.Render(result);
        var doc = JsonNode.Parse(output)!;

        doc["unusedCount"]!.GetValue<int>().Should().Be(3);
        doc["unusedPackages"]!.AsArray().Should().HaveCount(3);
    }

    // ── Version edge cases ────────────────────────────────────────────────────

    [Fact]
    public void Render_PackageWithEmptyVersion_VersionFieldIsOmitted()
    {
        var pkg = new UnusedPackage("SomePackage", string.Empty, "reason");
        var result = MakeResult(@"C:\Repo\App.csproj", pkg);
        var output = Reporter.Render(result);
        var pkgNode = JsonNode.Parse(output)!["unusedPackages"]![0]!.AsObject();

        pkgNode.ContainsKey("version").Should().BeFalse();
    }

    [Fact]
    public void Render_PackageWithNonEmptyVersion_VersionFieldIsPresent()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("SomePackage", "2.1.0"));
        var output = Reporter.Render(result);
        var pkgNode = JsonNode.Parse(output)!["unusedPackages"]![0]!;

        pkgNode["version"]!.GetValue<string>().Should().Be("2.1.0");
    }

    // ── RenderAll ─────────────────────────────────────────────────────────────

    [Fact]
    public void RenderAll_MultipleResults_OutputContainsBothProjectPaths()
    {
        var results = new[]
        {
            MakeResult(@"C:\Repo\Api\Api.csproj", Pkg("Newtonsoft.Json")),
            MakeResult(@"C:\Repo\Core\Core.csproj"),
        };
        var output = Reporter.RenderAll(results);

        // Check by filename — no backslash escaping issue since filenames contain no backslashes
        output.Should().Contain("Api.csproj");
        output.Should().Contain("Core.csproj");
    }

    [Fact]
    public void RenderAll_SingleResult_MatchesSingleRender()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Pkg1"));
        var renderAllOutput = Reporter.RenderAll([result]);
        var renderOutput = Reporter.Render(result);

        renderAllOutput.Should().Be(renderOutput);
    }

    [Fact]
    public void RenderAll_EmptyList_ReturnsEmptyString()
    {
        var output = Reporter.RenderAll([]);
        output.Should().BeEmpty();
    }
}
