namespace SlimDep.UnitTests.Analysis;

public sealed class ProjectUsingsResolverTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectDir;

    public ProjectUsingsResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"slimdep-pur-{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteProject(string xml)
    {
        string path = Path.Combine(_projectDir, "App.csproj");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void ImplicitUsingsDisabled_NoImplicitNamespaces()
    {
        string csproj = WriteProject("""<Project Sdk="Microsoft.NET.Sdk"></Project>""");

        ProjectUsingsResolver.Resolve(csproj).Should().BeEmpty();
    }

    [Fact]
    public void WorkerSdk_ImplicitUsingsEnabled_IncludesHosting()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk.Worker">
              <PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);

        var result = ProjectUsingsResolver.Resolve(csproj);

        result.Should().Contain(["System.Linq", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
    }

    [Fact]
    public void PlainSdk_ImplicitUsingsEnabled_DoesNotIncludeAspNetCore()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);

        var result = ProjectUsingsResolver.Resolve(csproj);

        result.Should().Contain("System.Threading.Tasks");
        result.Should().NotContain("Microsoft.AspNetCore.Builder");
    }

    [Fact]
    public void UsingItems_IncludeAndRemove_AreApplied()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup>
                <Using Include="Newtonsoft.Json.Linq" />
                <Using Remove="System.Net.Http" />
              </ItemGroup>
            </Project>
            """);

        var result = ProjectUsingsResolver.Resolve(csproj);

        result.Should().Contain(["Newtonsoft.Json.Linq", "Newtonsoft.Json"]);
        result.Should().NotContain("System.Net.Http");
    }

    [Fact]
    public void DirectoryBuildProps_UsingAndImplicitUsings_AreInherited()
    {
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), """
            <Project>
              <PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><Using Include="Serilog" /></ItemGroup>
            </Project>
            """);
        string csproj = WriteProject("""<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");

        var result = ProjectUsingsResolver.Resolve(csproj);

        result.Should().Contain(["Serilog", "Microsoft.AspNetCore.Builder"]);
    }

    [Fact]
    public void ConditionalUsingRemove_IsNotApplied()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Using Include="Dapper" /></ItemGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Release'"><Using Remove="Dapper" /></ItemGroup>
              <ItemGroup><Using Remove="Serilog" Condition="'$(X)' == 'y'" /></ItemGroup>
              <ItemGroup><Using Include="Serilog" /></ItemGroup>
            </Project>
            """);

        var result = ProjectUsingsResolver.Resolve(csproj);

        result.Should().Contain(["Dapper", "Serilog"], "Debug builds still import them");
    }

    [Fact]
    public void ConditionalImplicitUsingsDisable_KeepsImplicitUsings()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk.Worker">
              <PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'"><ImplicitUsings>disable</ImplicitUsings></PropertyGroup>
            </Project>
            """);

        ProjectUsingsResolver.Resolve(csproj).Should().Contain("Microsoft.Extensions.Hosting");
    }

    [Fact]
    public void ConditionalImplicitUsingsEnable_CountsAsEnabled()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup>
              <PropertyGroup><ImplicitUsings Condition="'$(TargetFramework)' == 'net8.0'">enable</ImplicitUsings></PropertyGroup>
            </Project>
            """);

        ProjectUsingsResolver.Resolve(csproj).Should().Contain("System.Linq");
    }

    [Fact]
    public void UnconditionalDisableAfterEnable_DisablesImplicitUsings()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup>
            </Project>
            """);

        ProjectUsingsResolver.Resolve(csproj).Should().NotContain("System.Linq");
    }

    [Fact]
    public void StaticAndAliasUsingItems_ResolveAsTypeNames()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Using Include="FluentAssertions.Execution.AssertionScope" Static="true" />
                <Using Include="Dapper.SqlMapper.GridReader" Alias="Grid" />
              </ItemGroup>
            </Project>
            """);

        var set = new NamespaceSet();
        ProjectUsingsResolver.ResolveInto(csproj, set);

        // The containing namespace is explicit evidence of use (SLIMDEP002)
        set.Explicit.Should().Contain(["FluentAssertions.Execution", "Dapper"]);
    }

    [Fact]
    public void LegacyMsBuildNamespace_UsingItemsStillFound()
    {
        string csproj = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup><Using Include="Dapper" /></ItemGroup>
            </Project>
            """);

        ProjectUsingsResolver.Resolve(csproj).Should().Contain("Dapper");
    }
}
