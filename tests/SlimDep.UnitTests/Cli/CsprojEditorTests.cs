using System.Text;
using SlimDep.Cli;

namespace SlimDep.UnitTests.Cli;

public sealed class CsprojEditorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public CsprojEditorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"slimdep-edit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "App.csproj");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string content, bool bom = false) =>
        File.WriteAllText(_path, content, new UTF8Encoding(bom));

    private string Read() => File.ReadAllText(_path);

    [Fact]
    public void RemovesOnlyTargetLine_PreservesFormattingAndComments()
    {
        const string before =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
            "\r\n" +
            "  <!-- keep me -->\r\n" +
            "  <ItemGroup>\r\n" +
            "    <PackageReference Include=\"Keep\" Version=\"1.0.0\" />\r\n" +
            "    <PackageReference Include=\"Drop\" Version=\"2.0.0\" />\r\n" +
            "  </ItemGroup>\r\n" +
            "\r\n" +
            "</Project>\r\n";
        Write(before);

        var removed = CsprojEditor.RemovePackageReferences(_path, ["Drop"]);

        removed.Should().Equal("Drop");
        Read().Should().Be(before.Replace("    <PackageReference Include=\"Drop\" Version=\"2.0.0\" />\r\n", ""));
    }

    [Fact]
    public void LastPackageInGroup_RemovesEmptyItemGroup()
    {
        const string before =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "\n" +
            "  <ItemGroup>\n" +
            "    <PackageReference Include=\"Drop\" Version=\"2.0.0\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>\n";
        Write(before);

        CsprojEditor.RemovePackageReferences(_path, ["Drop"]);

        Read().Should().Be(
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");
    }

    [Fact]
    public void ConditionalOrCommentedItemGroup_IsKeptWhenEmptied()
    {
        Write("""
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
                <PackageReference Include="Drop" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup>
                <!-- documented group -->
                <PackageReference Include="Drop2" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        CsprojEditor.RemovePackageReferences(_path, ["Drop", "Drop2"]);

        string after = Read();
        after.Should().Contain("Condition=\"'$(TargetFramework)' == 'net48'\"");
        after.Should().Contain("<!-- documented group -->");
        after.Should().NotContain("PackageReference");
    }

    [Fact]
    public void NoXmlDeclaration_NoneAdded_AndBomPreserved()
    {
        Write("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"A\" />\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n", bom: true);

        CsprojEditor.RemovePackageReferences(_path, ["A"]);

        byte[] bytes = File.ReadAllBytes(_path);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Read().Should().NotContain("<?xml");
    }

    [Fact]
    public void XmlDeclaration_IsKept()
    {
        Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project>\n  <ItemGroup>\n    <PackageReference Include=\"A\" />\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n");

        CsprojEditor.RemovePackageReferences(_path, ["A"]);

        Read().Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
    }

    [Fact]
    public void PackageNotInFile_ReturnsEmpty_AndFileUntouched()
    {
        const string before = "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"A\" />\n  </ItemGroup>\n</Project>\n";
        Write(before);
        var stamp = File.GetLastWriteTimeUtc(_path);

        var removed = CsprojEditor.RemovePackageReferences(_path, ["FromDirectoryBuildProps"]);

        removed.Should().BeEmpty();
        Read().Should().Be(before);
        File.GetLastWriteTimeUtc(_path).Should().Be(stamp);
    }

    [Fact]
    public void LegacyMsBuildNamespace_IsHandled()
    {
        Write("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <ItemGroup>\n    <PackageReference Include=\"A\" />\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n");

        var removed = CsprojEditor.RemovePackageReferences(_path, ["a"]);

        removed.Should().Equal("A");
        Read().Should().NotContain("Include=\"A\"").And.Contain("Include=\"B\"");
    }
}
