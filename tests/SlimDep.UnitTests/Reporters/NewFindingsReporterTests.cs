namespace SlimDep.UnitTests.Reporters;

/// <summary>Rendering of SLIMDEP002, version pins and safe-list skips in every format.</summary>
public sealed class NewFindingsReporterTests
{
    private static ScanResult MakeResult(
        UndeclaredPackage[]? undeclared = null,
        PinnedPackage[]? pinned = null,
        SkippedPackage[]? skipped = null) =>
        new()
        {
            ProjectPath = @"C:\Repo\App.csproj",
            UnusedPackages = [],
            TotalDirectPackages = 3,
            Duration = TimeSpan.FromMilliseconds(10),
            UndeclaredPackages = undeclared ?? [],
            PinnedPackages = pinned ?? [],
            SkippedPackages = skipped ?? [],
        };

    private static readonly UndeclaredPackage Undeclared =
        new("Microsoft.Extensions.Logging.Abstractions", "8.0.0", ["Serilog.Extensions.Logging"], ["Microsoft.Extensions.Logging"]);

    private static readonly PinnedPackage Pinned =
        new("System.Text.Json", "8.0.5", "8.0.0", ["Azure.Core"]);

    private static readonly SkippedPackage Skipped =
        new("coverlet.collector", "6.0.4", "On the built-in safe list (coverlet.*)");

    // ── Console ───────────────────────────────────────────────────────────────

    [Fact]
    public void Console_ShowsAllSections()
    {
        var output = new ConsoleReporter().Render(MakeResult([Undeclared], [Pinned], [Skipped]));

        output.Should().Contain("✓ All packages are used");
        output.Should().Contain("SLIMDEP002").And.Contain("Microsoft.Extensions.Logging.Abstractions 8.0.0");
        output.Should().Contain("version pins").And.Contain("overrides 8.0.0 required by Azure.Core");
        output.Should().Contain("default safe list").And.Contain("coverlet.collector").And.Contain("--no-default-ignores");
    }

    [Fact]
    public void Console_NoNewFindings_NoExtraSections()
    {
        var output = new ConsoleReporter().Render(MakeResult());

        output.Should().NotContain("SLIMDEP002").And.NotContain("version pin").And.NotContain("safe list");
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Json_IncludesNewArrays()
    {
        var doc = JsonNode.Parse(new JsonReporter().Render(MakeResult([Undeclared], [Pinned], [Skipped])))!;

        var u = doc["undeclaredPackages"]![0]!;
        u["name"]!.GetValue<string>().Should().Be("Microsoft.Extensions.Logging.Abstractions");
        u["introducedBy"]![0]!.GetValue<string>().Should().Be("Serilog.Extensions.Logging");
        u["usedNamespaces"]![0]!.GetValue<string>().Should().Be("Microsoft.Extensions.Logging");

        var p = doc["pinnedPackages"]![0]!;
        p["transitiveVersion"]!.GetValue<string>().Should().Be("8.0.0");
        p["requiredBy"]![0]!.GetValue<string>().Should().Be("Azure.Core");

        doc["skippedPackages"]![0]!["name"]!.GetValue<string>().Should().Be("coverlet.collector");
    }

    [Fact]
    public void Json_EmptyNewArrays_AreEmitted()
    {
        var doc = JsonNode.Parse(new JsonReporter().Render(MakeResult()))!;

        doc["undeclaredPackages"]!.AsArray().Should().BeEmpty();
        doc["pinnedPackages"]!.AsArray().Should().BeEmpty();
        doc["skippedPackages"]!.AsArray().Should().BeEmpty();
    }

    // ── SARIF ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Sarif_Undeclared_AddsRuleAndNoteResult()
    {
        var doc = JsonNode.Parse(new SarifReporter().Render(MakeResult([Undeclared])))!;
        var run = doc["runs"]![0]!;

        var rules = run["tool"]!["driver"]!["rules"]!.AsArray();
        rules.Select(r => r!["id"]!.GetValue<string>()).Should().Equal("SLIMDEP001", "SLIMDEP002");

        var result = run["results"]!.AsArray().Should().ContainSingle().Subject!;
        result["ruleId"]!.GetValue<string>().Should().Be("SLIMDEP002");
        result["level"]!.GetValue<string>().Should().Be("note");
    }

    [Fact]
    public void Sarif_PinsAndSkips_ProduceNoResults()
    {
        var doc = JsonNode.Parse(new SarifReporter().Render(MakeResult(pinned: [Pinned], skipped: [Skipped])))!;
        var run = doc["runs"]![0]!;

        run["results"]!.AsArray().Should().BeEmpty();
        run["tool"]!["driver"]!["rules"]!.AsArray().Should().HaveCount(1);
    }
}
