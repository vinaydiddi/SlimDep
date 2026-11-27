using Microsoft.CodeAnalysis.CSharp;

namespace SlimDep.UnitTests.Analysis;

public sealed class SourceAnalyzerTests : IDisposable
{
    private readonly string _tempDir;

    public SourceAnalyzerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"slimdep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void CollectUsedNamespaces_SingleUsing_ReturnsNamespace()
    {
        WriteFile("Foo.cs", "using Newtonsoft.Json;\nnamespace MyApp { }");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public void CollectUsedNamespaces_MultipleUsings_ReturnsAll()
    {
        WriteFile("A.cs", "using System.Text;\nusing System.Linq;");
        WriteFile("B.cs", "using Newtonsoft.Json.Linq;");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("System.Text");
        result.Should().Contain("System.Linq");
        result.Should().Contain("Newtonsoft.Json.Linq");
    }

    [Fact]
    public void CollectUsedNamespaces_NestedNamespace_IncludesParents()
    {
        WriteFile("C.cs", "using Newtonsoft.Json.Linq;");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        // Parent namespaces should also be present
        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("Newtonsoft");
    }

    [Fact]
    public void CollectUsedNamespaces_EmptyDirectory_ReturnsEmpty()
    {
        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public void CollectUsedNamespaces_ObjFolderExcluded()
    {
        string objDir = Path.Combine(_tempDir, "obj");
        Directory.CreateDirectory(objDir);
        WriteFile(Path.Combine(objDir, "Generated.cs"), "using ShouldBeIgnored.Namespace;");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().NotContain("ShouldBeIgnored.Namespace");
    }

    [Fact]
    public void CollectUsedNamespaces_CancellationRequested_Throws()
    {
        WriteFile("Foo.cs", "using X.Y;");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => SourceAnalyzer.CollectUsedNamespaces(_tempDir, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    // ── Feature 1: Fully-qualified name detection ─────────────────────────────

    [Fact]
    public void CollectUsedNamespaces_FullyQualifiedTypeName_Detected()
    {
        WriteFile("Service.cs", "var x = new Newtonsoft.Json.JsonSerializer();");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("Newtonsoft");
    }

    [Fact]
    public void CollectUsedNamespaces_FullyQualifiedStaticCall_Detected()
    {
        WriteFile("Service.cs", "var s = Newtonsoft.Json.JsonConvert.SerializeObject(obj);");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("Newtonsoft");
    }

    [Fact]
    public void Collect_LowercaseChain_NotExplicitEvidence()
    {
        // Instance access like myObj.property.method() must never trigger SLIMDEP002
        WriteFile("Service.cs", "myObject.someProperty.DoSomething();");

        var result = SourceAnalyzer.Collect(_tempDir, CancellationToken.None);

        result.Explicit.Should().NotContain(n => n.StartsWith("myObject"));
    }

    [Fact]
    public void Collect_LowercaseRootNamespace_CountsAsUsed()
    {
        // log4net's root namespace is lower-case; a fully-qualified call must keep it "used"
        WriteFile("Log.cs", "class C { void M() { log4net.Config.BasicConfigurator.Configure(); } }");

        var result = SourceAnalyzer.Collect(_tempDir, CancellationToken.None);

        result.All.Should().Contain(["log4net", "log4net.Config"]);
    }

    [Fact]
    public void Collect_GlobalQualifiedExpression_IsExplicit()
    {
        WriteFile("G.cs", "class C { void M() { var s = global::Newtonsoft.Json.JsonConvert.SerializeObject(1); } }");

        var result = SourceAnalyzer.Collect(_tempDir, CancellationToken.None);

        result.All.Should().Contain("Newtonsoft.Json");
        result.Explicit.Should().Contain("Newtonsoft.Json");
    }

    // ── Which folders are scanned ─────────────────────────────────────────────

    [Fact]
    public void Collect_ProjectUnderFolderNamedBin_IsScanned()
    {
        string projectDir = Path.Combine(_tempDir, "bin", "App");
        WriteFile(Path.Combine(projectDir, "A.cs"), "using Newtonsoft.Json;");

        var result = SourceAnalyzer.CollectUsedNamespaces(projectDir, CancellationToken.None);

        result.Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public void Collect_NestedFolderNamedBinOrObj_IsScanned_OnlyRootOnesSkipped()
    {
        WriteFile(Path.Combine("Models", "Bin", "Sub.cs"), "using Nested.Bin;");
        WriteFile(Path.Combine("Data", "obj", "Sub.cs"), "using Nested.Obj;");
        WriteFile(Path.Combine("bin", "Debug", "Gen.cs"), "using Root.Bin;");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain(["Nested.Bin", "Nested.Obj"]);
        result.Should().NotContain("Root.Bin");
    }

    [Fact]
    public void Collect_NestedProjectAndDotFolders_AreSkipped()
    {
        WriteFile(Path.Combine("tests", "App.Tests", "App.Tests.csproj"), "<Project />");
        WriteFile(Path.Combine("tests", "App.Tests", "T.cs"), "using Only.In.Tests;");
        WriteFile(Path.Combine(".vs", "X.cs"), "using Only.In.Dot;");
        WriteFile("Main.cs", "using Main.Ns;");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Main.Ns");
        result.Should().NotContain(["Only.In.Tests", "Only.In.Dot"]);
    }

    [Fact]
    public void Collect_AdditionalFilesOutsideProject_AreScanned()
    {
        string shared = Path.Combine(Path.GetTempPath(), $"slimdep-shared-{Guid.NewGuid():N}.cs");
        File.WriteAllText(shared, "using Shared.Outside;");
        try
        {
            var result = SourceAnalyzer.Collect(_tempDir, CancellationToken.None, [shared]);
            result.All.Should().Contain("Shared.Outside");
        }
        finally { File.Delete(shared); }
    }

    // ── XAML using: syntax and Avalonia ───────────────────────────────────────

    [Fact]
    public void Collect_XamlUsingSyntaxAndAxaml_Detected()
    {
        WriteFile("Page.xaml", """<Page xmlns:c="using:CommunityToolkit.WinUI.Controls" />""");
        WriteFile("View.axaml", """<UserControl xmlns:vm="clr-namespace:Avalonia.Demo.ViewModels" xmlns:x2="using:Avalonia.Xaml.Interactivity" />""");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain(["CommunityToolkit.WinUI.Controls", "Avalonia.Demo.ViewModels", "Avalonia.Xaml.Interactivity"]);
    }

    [Fact]
    public void Collect_XamlTextMentioningUsing_NotTreatedAsNamespace()
    {
        WriteFile("P.xaml", """<Page><TextBlock Text="using:Fake.Thing" /></Page>""");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().NotContain("Fake.Thing");
    }

    // ── Razor directives and code blocks ──────────────────────────────────────

    [Fact]
    public void Collect_RazorDirectivesAndCodeBlock_Detected()
    {
        WriteFile("Page.razor", """
            @page "/x"
            @using static Foo.Statics.Helpers
            @using Grid = Bar.Grids.DataGrid
            @inject Inj.Services.IClock Clock
            @inherits Base.Components.LayoutBase
            @attribute [Auth.Policies.Authorize]
            @addTagHelper *, Tag.Helpers.Lib
            <p>@Inline.Format.Now()</p>
            @code {
                private Code.Models.Item _item = new();
                string S => "}{";
            }
            """);

        var result = SourceAnalyzer.Collect(_tempDir, CancellationToken.None);

        result.All.Should().Contain(["Foo.Statics", "Bar.Grids", "Inj.Services", "Base.Components",
            "Auth.Policies", "Tag.Helpers.Lib", "Inline.Format", "Code.Models"]);
        result.All.Should().NotContain(n => n.StartsWith("static ") || n.Contains('='));
        result.Explicit.Should().Contain(["Inj.Services", "Code.Models"]);
    }

    [Fact]
    public void Collect_CshtmlModelDirective_Detected()
    {
        WriteFile("Index.cshtml", "@model Web.ViewModels.IndexVm\n<h1>@Model.Title</h1>");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Web.ViewModels");
    }

    [Fact]
    public void Collect_RazorEmailAddress_NotTreatedAsExpression()
    {
        WriteFile("Contact.razor", "<p>Mail support@contoso.example.com</p>");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().NotContain(n => n.StartsWith("contoso"));
    }

    // ── Feature 4a: XAML namespace collection ────────────────────────────────

    [Fact]
    public void CollectUsedNamespaces_XamlClrNamespace_Detected()
    {
        WriteFile("MainWindow.xaml",
            """<?xml version="1.0"?><Window xmlns:local="clr-namespace:MyApp.ViewModels;assembly=MyApp" />""");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("MyApp.ViewModels");
        result.Should().Contain("MyApp");
    }

    [Fact]
    public void CollectUsedNamespaces_XamlObjFolderExcluded()
    {
        string objDir = Path.Combine(_tempDir, "obj");
        Directory.CreateDirectory(objDir);
        WriteFile(Path.Combine(objDir, "Generated.xaml"),
            """<Window xmlns:x="clr-namespace:ShouldBeIgnored.Xaml" />""");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().NotContain("ShouldBeIgnored.Xaml");
    }

    // ── Feature 4b: Razor / CSHTML namespace collection ──────────────────────

    [Fact]
    public void CollectUsedNamespaces_RazorUsing_Detected()
    {
        WriteFile("Index.razor", "@using MyApp.Services\n@using MyApp.Models\n<h1>Hello</h1>");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("MyApp.Services");
        result.Should().Contain("MyApp.Models");
        result.Should().Contain("MyApp");
    }

    [Fact]
    public void CollectUsedNamespaces_CshtmlUsing_Detected()
    {
        WriteFile("Index.cshtml", "@using MyApp.ViewModels\n<p>Hello</p>");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("MyApp.ViewModels");
    }

    // ── Conditional compilation and global:: ──────────────────────────────────

    [Fact]
    public void CollectUsedNamespaces_UsingInsideIfBlock_Detected()
    {
        WriteFile("Compat.cs", "#if NET48\nusing Legacy.Only;\n#else\nusing Modern.Only;\n#endif");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Legacy.Only");
        result.Should().Contain("Modern.Only");
    }

    [Fact]
    public void CollectUsedNamespaces_EveryElifBranch_Detected()
    {
        WriteFile("Multi.cs",
            "#if NET8_0\nusing A.Eight;\n#elif NET9_0\nusing B.Nine;\n#elif NET10_0\nusing C.Ten;\n#else\nusing D.Other;\n#endif");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain(["A.Eight", "B.Nine", "C.Ten", "D.Other"]);
    }

    [Fact]
    public void CollectUsedNamespaces_CombinedCondition_Detected()
    {
        WriteFile("Both.cs", "#if WINDOWS && NET8_0\nusing Win.Eight;\n#endif");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Win.Eight");
    }

    [Fact]
    public void CollectUsedNamespaces_GlobalAliasPrefix_Stripped()
    {
        WriteFile("G.cs", "global using global::Newtonsoft.Json;\nclass C { global::Serilog.ILogger L; }");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("Serilog");
        result.Should().NotContain(n => n.StartsWith("global::"));
    }

    private void WriteFile(string relativePath, string content)
    {
        string fullPath = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
