namespace SlimDep.UnitTests.Analysis;

public sealed class IgnoreFileParserTests : IDisposable
{
    private readonly string _tempDir;

    public IgnoreFileParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"slimdep-ignore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Load_NoIgnoreFile_ReturnsEmptySet()
    {
        var result = IgnoreFileParser.Load(_tempDir);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Load_IgnoreFileInSameDir_ReturnsPackages()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".slimdepignore"),
            "Newtonsoft.Json\nSerilog\n");

        var result = IgnoreFileParser.Load(_tempDir);

        result.Should().Contain("Newtonsoft.Json");
        result.Should().Contain("Serilog");
    }

    [Fact]
    public void Load_CommentsAndEmptyLines_Ignored()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".slimdepignore"),
            "# This is a comment\n\nNewtonsoft.Json\n  # another comment\n");

        var result = IgnoreFileParser.Load(_tempDir);

        result.Should().ContainSingle().Which.Should().Be("Newtonsoft.Json");
    }

    [Fact]
    public void Load_PackageNamesAreCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".slimdepignore"), "newtonsoft.json\n");

        var result = IgnoreFileParser.Load(_tempDir);

        result.Should().Contain("Newtonsoft.Json"); // case-insensitive match
    }

    [Fact]
    public void Load_IgnoreFileInParentDir_Found()
    {
        // Write ignore file in parent, look from subdirectory
        File.WriteAllText(Path.Combine(_tempDir, ".slimdepignore"), "SomePackage\n");
        string subDir = Path.Combine(_tempDir, "src", "MyApp");
        Directory.CreateDirectory(subDir);

        var result = IgnoreFileParser.Load(subDir);

        result.Should().Contain("SomePackage");
    }
}
