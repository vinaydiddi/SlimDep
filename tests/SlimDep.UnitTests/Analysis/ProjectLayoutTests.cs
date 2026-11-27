namespace SlimDep.UnitTests.Analysis;

public sealed class ProjectLayoutTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectDir;
    private readonly string _csproj;

    public ProjectLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"slimdep-layout-{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(_projectDir);
        _csproj = Path.Combine(_projectDir, "App.csproj");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── project.assets.json location ──────────────────────────────────────────

    [Fact]
    public void Assets_Default_IsObjFolder()
    {
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ProjectLayout.AssetsFileCandidates(_csproj).Should().Equal(Path.Combine(_projectDir, "obj", "project.assets.json"));
    }

    [Fact]
    public void Assets_ArtifactsOutput_PreferredOverStaleObj()
    {
        Write(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>");
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string artifacts = Path.Combine(_root, "artifacts", "obj", "App", "project.assets.json");
        string staleObj = Path.Combine(_projectDir, "obj", "project.assets.json");
        Write(artifacts, "{}");
        Write(staleObj, "{}");

        ProjectLayout.FindAssetsFile(_csproj).Should().Be(artifacts);
    }

    [Fact]
    public void Assets_CustomArtifactsPath_IsHonoured()
    {
        Write(Path.Combine(_root, "Directory.Build.props"), """
            <Project><PropertyGroup>
              <UseArtifactsOutput>true</UseArtifactsOutput>
              <ArtifactsPath>$(MSBuildThisFileDirectory)out</ArtifactsPath>
            </PropertyGroup></Project>
            """);
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ProjectLayout.AssetsFileCandidates(_csproj)[0].Should().Be(Path.Combine(_root, "out", "obj", "App", "project.assets.json"));
    }

    [Fact]
    public void Assets_BaseIntermediateOutputPath_IsHonoured()
    {
        Write(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup><BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)build/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string expected = Path.Combine(_root, "build", "obj", "App", "project.assets.json");
        Write(expected, "{}");

        ProjectLayout.FindAssetsFile(_csproj).Should().Be(expected);
    }

    [Fact]
    public void Assets_UnresolvableProperty_FallsBackToObj()
    {
        Write(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup><BaseIntermediateOutputPath>$(SolutionDir)obj/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ProjectLayout.AssetsFileCandidates(_csproj).Should().Equal(Path.Combine(_projectDir, "obj", "project.assets.json"));
    }

    [Fact]
    public void Assets_BackslashPaths_ResolveOnEveryOs()
    {
        // MSBuild accepts '\' on Linux/macOS too; .NET treats it as a file-name character there
        Write(Path.Combine(_root, "Directory.Build.props"),
            @"<Project><PropertyGroup><BaseIntermediateOutputPath>..\..\build\obj\$(MSBuildProjectName)\</BaseIntermediateOutputPath></PropertyGroup></Project>");
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string expected = Path.Combine(_root, "build", "obj", "App", "project.assets.json");
        Write(expected, "{}");

        ProjectLayout.FindAssetsFile(_csproj).Should().Be(expected);
    }

    [Fact]
    public void Assets_BackslashArtifactsPath_ResolvesOnEveryOs()
    {
        Write(Path.Combine(_root, "Directory.Build.props"), """
            <Project><PropertyGroup>
              <UseArtifactsOutput>true</UseArtifactsOutput>
              <ArtifactsPath>$(MSBuildThisFileDirectory)out\artifacts</ArtifactsPath>
            </PropertyGroup></Project>
            """);
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ProjectLayout.AssetsFileCandidates(_csproj).Should().Equal(
            Path.Combine(_root, "out", "artifacts", "obj", "App", "project.assets.json"));
    }

    [Fact]
    public void Assets_RedirectedButNotRestored_StaleObjIsNotUsed()
    {
        Write(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>");
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(Path.Combine(_projectDir, "obj", "project.assets.json"), "{}"); // left over from before

        var act = () => ProjectLayout.FindAssetsFile(_csproj);

        act.Should().Throw<SlimDepException>().WithMessage("*artifacts*dotnet restore*");
        ProjectLayout.AssetsFileCandidates(_csproj).Should().NotContain(c => c.Contains($"{Path.DirectorySeparatorChar}App{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void Assets_ConditionalRedirect_ObjStillPossible()
    {
        Write(Path.Combine(_root, "Directory.Build.props"),
            "<Project><PropertyGroup Condition=\"'$(CI)' == 'true'\"><BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)ci-obj/</BaseIntermediateOutputPath></PropertyGroup></Project>");
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string obj = Path.Combine(_projectDir, "obj", "project.assets.json");
        Write(obj, "{}");

        ProjectLayout.FindAssetsFile(_csproj).Should().Be(obj);
    }

    [Fact]
    public void Compiled_FilesDifferingOnlyInCase_BothKeptOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return; // only Linux file systems are case-sensitive by default

        Write(Path.Combine(_root, "src", "Shared", "Util.cs"), "");
        Write(Path.Combine(_root, "src", "Shared", "util.cs"), "");
        Write(_csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Shared/*.cs" /></ItemGroup>
            </Project>
            """);

        ProjectLayout.CompiledSourceFiles(_csproj).Should().HaveCount(2);
    }

    [Fact]
    public void Assets_Missing_ErrorListsLocations()
    {
        Write(_csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var act = () => ProjectLayout.FindAssetsFile(_csproj);

        act.Should().Throw<SlimDepException>().WithMessage("*obj*project.assets.json*dotnet restore*");
    }

    // ── Source files compiled from outside the project folder ────────────────

    [Fact]
    public void Compiled_LinkedFilesWithGlob_AreResolved()
    {
        Write(Path.Combine(_root, "src", "Shared", "A.cs"), "");
        Write(Path.Combine(_root, "src", "Shared", "Deep", "B.cs"), "");
        Write(Path.Combine(_root, "src", "Shared", "notes.txt"), "");
        Write(Path.Combine(_root, "src", "Other", "C.cs"), "");
        Write(_csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="..\Shared\**\*.cs" Link="Shared\%(RecursiveDir)%(Filename)%(Extension)" />
                <Compile Include="../Other/C.cs" />
              </ItemGroup>
            </Project>
            """);

        var files = ProjectLayout.CompiledSourceFiles(_csproj).Select(Path.GetFileName);

        files.Should().BeEquivalentTo(["A.cs", "B.cs", "C.cs"]);
    }

    [Fact]
    public void Compiled_SharedProjectProjitems_AreResolved()
    {
        string sharedDir = Path.Combine(_root, "src", "Shared.Lib");
        Write(Path.Combine(sharedDir, "Util.cs"), "");
        Write(Path.Combine(sharedDir, "Shared.Lib.projitems"), """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Util.cs" />
              </ItemGroup>
            </Project>
            """);
        Write(_csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="..\Shared.Lib\Shared.Lib.projitems" Label="Shared" />
            </Project>
            """);

        ProjectLayout.CompiledSourceFiles(_csproj).Should().ContainSingle()
            .Which.Should().Be(Path.Combine(sharedDir, "Util.cs"));
    }

    [Fact]
    public void Compiled_UnresolvableProperty_IsSkipped()
    {
        Write(_csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="$(SharedRoot)\*.cs" /></ItemGroup>
            </Project>
            """);

        ProjectLayout.CompiledSourceFiles(_csproj).Should().BeEmpty();
    }
}
