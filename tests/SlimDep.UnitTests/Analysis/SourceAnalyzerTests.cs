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
    public void CollectUsedNamespaces_LowercaseChain_NotCollected()
    {
        // Instance access like myObj.property.method() should not be treated as a namespace
        WriteFile("Service.cs", "myObject.someProperty.DoSomething();");

        var result = SourceAnalyzer.CollectUsedNamespaces(_tempDir, CancellationToken.None);

        result.Should().NotContain("myObject");
        result.Should().NotContain("myObject.someProperty");
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

    private void WriteFile(string relativePath, string content)
    {
        string fullPath = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
