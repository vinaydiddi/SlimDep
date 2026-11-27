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

    // ── Untouched content stays byte-for-byte identical ───────────────────────

    private const string Quirky =
        "<Project Sdk='Microsoft.NET.Sdk'>\n" +
        "  <PropertyGroup>\n" +
        "    <Description>A &amp; B &#x41; \"q\"</Description>\n" +
        "    <Foo   Condition = \" '$(X)' == 'y' \"   >bar</Foo>\n" +
        "  </PropertyGroup>\n" +
        "  <ItemGroup>\n" +
        "    <PackageReference Include='Keep'   Version='1.0.0' />\n" +
        "    <PackageReference\n" +
        "        Include=\"Drop\"\n" +
        "        Version=\"2.0.0\" />\n" +
        "    <PackageReference Include=\"Other\"><Version>3.0</Version></PackageReference>\n" +
        "  </ItemGroup>\n" +
        "</Project>";

    [Fact]
    public void MultiLineElement_RemovedAndEverythingElseUntouched()
    {
        Write(Quirky);

        CsprojEditor.RemovePackageReferences(_path, ["Drop"]);

        Read().Should().Be(Quirky.Replace(
            "    <PackageReference\n        Include=\"Drop\"\n        Version=\"2.0.0\" />\n", ""));
    }

    [Fact]
    public void ElementWithChildren_Removed_QuotesAndEntitiesUntouched()
    {
        Write(Quirky);

        CsprojEditor.RemovePackageReferences(_path, ["Other"]);

        Read().Should().Be(Quirky.Replace(
            "    <PackageReference Include=\"Other\"><Version>3.0</Version></PackageReference>\n", ""));
    }

    [Fact]
    public void ElementSharingALine_KeepsNeighbourAndIndentation()
    {
        Write("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"A\" /> <PackageReference Include=\"B\" />\n    <PackageReference Include=\"C\" />\n  </ItemGroup>\n</Project>\n");

        CsprojEditor.RemovePackageReferences(_path, ["A"]);
        Read().Should().Be("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"B\" />\n    <PackageReference Include=\"C\" />\n  </ItemGroup>\n</Project>\n");

        CsprojEditor.RemovePackageReferences(_path, ["C"]);
        Read().Should().Be("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n");
    }

    [Fact]
    public void TwoElementsOnOneLine_BothRemoved_LineRemovedWhole()
    {
        Write("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"A\" /> <PackageReference Include=\"B\" />\n    <PackageReference Include=\"C\" />\n  </ItemGroup>\n</Project>\n");

        var removed = CsprojEditor.RemovePackageReferences(_path, ["A", "B"]);

        removed.Should().BeEquivalentTo(["A", "B"]);
        Read().Should().Be("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"C\" />\n  </ItemGroup>\n</Project>\n");
    }

    [Fact]
    public void ThreeElementsOnOneLine_OuterTwoRemoved_MiddleKept()
    {
        Write("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"A\" /> <PackageReference Include=\"B\" /> <PackageReference Include=\"C\" />\n  </ItemGroup>\n</Project>\n");

        CsprojEditor.RemovePackageReferences(_path, ["A", "C"]);

        Read().Should().Be("<Project>\n  <ItemGroup>\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n");
    }

    [Fact]
    public void GreaterThanInsideAttributeAndComment_DoesNotConfuseSpan()
    {
        Write("<Project>\n  <ItemGroup>\n    <!-- <PackageReference Include=\"A\" /> -->\n    <PackageReference Include=\"A\" Condition=\"'$(V)' > '1'\" />\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n");

        CsprojEditor.RemovePackageReferences(_path, ["A"]);

        Read().Should().Be("<Project>\n  <ItemGroup>\n    <!-- <PackageReference Include=\"A\" /> -->\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n");
    }

    // ── Encodings ─────────────────────────────────────────────────────────────

    [Fact]
    public void Utf16File_ParsedAndWrittenBackAsUtf16()
    {
        const string before = "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<Project>\r\n  <!-- naïve -->\r\n  <ItemGroup>\r\n    <PackageReference Include=\"A\" />\r\n    <PackageReference Include=\"B\" />\r\n  </ItemGroup>\r\n</Project>\r\n";
        File.WriteAllText(_path, before, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

        var removed = CsprojEditor.RemovePackageReferences(_path, ["A"]);

        removed.Should().Equal("A");
        byte[] bytes = File.ReadAllBytes(_path);
        bytes.Take(2).Should().Equal(0xFF, 0xFE);
        File.ReadAllText(_path, Encoding.Unicode).Should().Be(before.Replace("    <PackageReference Include=\"A\" />\r\n", ""));
    }

    [Fact]
    public void DeclaredLegacyEncoding_NonAsciiPreserved()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1252 = Encoding.GetEncoding(1252);
        const string before = "<?xml version=\"1.0\" encoding=\"windows-1252\"?>\n<Project>\n  <!-- café -->\n  <ItemGroup>\n    <PackageReference Include=\"A\" />\n    <PackageReference Include=\"B\" />\n  </ItemGroup>\n</Project>\n";
        File.WriteAllBytes(_path, cp1252.GetBytes(before));

        CsprojEditor.RemovePackageReferences(_path, ["A"]);

        byte[] bytes = File.ReadAllBytes(_path);
        bytes.Should().Contain(0xE9, "é must stay a single windows-1252 byte");
        cp1252.GetString(bytes).Should().Be(before.Replace("    <PackageReference Include=\"A\" />\n", ""));
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
