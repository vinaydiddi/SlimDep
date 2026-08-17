namespace SlimDep.UnitTests.Reporters;

public sealed class ConsoleReporterTests
{
    private static readonly IReporter Reporter = new ConsoleReporter();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScanResult MakeResult(string projectPath, params UnusedPackage[] packages) =>
        new()
        {
            ProjectPath = projectPath,
            UnusedPackages = packages,
            TotalDirectPackages = packages.Length + 2,
            Duration = TimeSpan.FromMilliseconds(75),
        };

    private static UnusedPackage Pkg(string name, string version = "1.0.0") =>
        new(name, version, "No namespace from this package appears in any using directive");

    // ── Header ────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_AlwaysIncludesProjectPath()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);

        output.Should().Contain(@"C:\Repo\App.csproj");
    }

    [Fact]
    public void Render_AlwaysIncludesPackageCount()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);

        output.Should().Contain("2 direct reference(s) analysed");
    }

    [Fact]
    public void Render_AlwaysIncludesDuration()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);

        output.Should().Contain("ms");
    }

    // ── No unused ─────────────────────────────────────────────────────────────

    [Fact]
    public void Render_NoUnusedPackages_ShowsCheckmark()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);

        output.Should().Contain("✓");
        output.Should().Contain("All packages are used");
    }

    [Fact]
    public void Render_NoUnusedPackages_DoesNotShowCross()
    {
        var result = MakeResult(@"C:\Repo\App.csproj");
        var output = Reporter.Render(result);

        output.Should().NotContain("✗");
    }

    // ── Has unused ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_HasUnusedPackages_ShowsCross()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json"));
        var output = Reporter.Render(result);

        output.Should().Contain("✗");
        output.Should().NotContain("✓");
    }

    [Fact]
    public void Render_HasUnusedPackages_ShowsPackageName()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json", "13.0.3"));
        var output = Reporter.Render(result);

        output.Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public void Render_HasUnusedPackages_ShowsVersion()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json", "13.0.3"));
        var output = Reporter.Render(result);

        output.Should().Contain("13.0.3");
    }

    [Fact]
    public void Render_HasUnusedPackages_ShowsReason()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json"));
        var output = Reporter.Render(result);

        output.Should().Contain("No namespace from this package appears in any using directive");
    }

    [Fact]
    public void Render_HasUnusedPackages_ShowsUnusedCount()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Pkg1"), Pkg("Pkg2"));
        var output = Reporter.Render(result);

        output.Should().Contain("2 unused package(s) found");
    }

    [Fact]
    public void Render_HasUnusedPackages_ShowsFixHint()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("SomePackage"));
        var output = Reporter.Render(result);

        output.Should().Contain("slimdep fix");
    }

    [Fact]
    public void Render_MultipleUnusedPackages_AllPackagesListed()
    {
        var result = MakeResult(@"C:\Repo\App.csproj",
            Pkg("Newtonsoft.Json"),
            Pkg("Serilog"),
            Pkg("AutoMapper"));
        var output = Reporter.Render(result);

        output.Should().Contain("Newtonsoft.Json");
        output.Should().Contain("Serilog");
        output.Should().Contain("AutoMapper");
    }

    // ── Version edge cases ────────────────────────────────────────────────────

    [Fact]
    public void Render_PackageWithEmptyVersion_NoTrailingSpace()
    {
        var pkg = new UnusedPackage("SomePackage", string.Empty, "reason");
        var result = MakeResult(@"C:\Repo\App.csproj", pkg);
        var output = Reporter.Render(result);

        output.Should().Contain("SomePackage");
        // The bullet point should not have a trailing space before newline
        output.Should().NotContain("SomePackage \n");
    }

    // ── RenderAll ─────────────────────────────────────────────────────────────

    [Fact]
    public void RenderAll_MultipleResults_JoinsEachRenderWithNewline()
    {
        var results = new[]
        {
            MakeResult(@"C:\Repo\Api\Api.csproj", Pkg("Newtonsoft.Json")),
            MakeResult(@"C:\Repo\Core\Core.csproj"),
        };
        var output = Reporter.RenderAll(results);

        output.Should().Contain(@"C:\Repo\Api\Api.csproj");
        output.Should().Contain(@"C:\Repo\Core\Core.csproj");
        output.Should().Contain("Newtonsoft.Json");
        output.Should().Contain("All packages are used");
    }

    [Fact]
    public void RenderAll_EmptyList_ReturnsEmptyString()
    {
        var output = Reporter.RenderAll([]);
        output.Should().BeEmpty();
    }

    [Fact]
    public void RenderAll_SingleResult_MatchesSingleRender()
    {
        var result = MakeResult(@"C:\Repo\App.csproj", Pkg("Newtonsoft.Json"));
        var renderAllOutput = Reporter.RenderAll([result]);
        var renderOutput = Reporter.Render(result);

        renderAllOutput.Should().Be(renderOutput);
    }
}
