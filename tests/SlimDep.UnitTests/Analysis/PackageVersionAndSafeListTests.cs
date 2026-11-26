namespace SlimDep.UnitTests.Analysis;

public sealed class PackageVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0", "1.0.0.0", 0)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0+build5", "1.0.0", 0)]
    [InlineData("4.3.0.1", "4.3.0", 1)]
    public void Compare_OrdersLikeNuGet(string a, string b, int expectedSign)
    {
        Math.Sign(PackageVersion.Compare(a, b)).Should().Be(expectedSign);
        Math.Sign(PackageVersion.Compare(b, a)).Should().Be(-expectedSign);
    }

    [Theory]
    [InlineData("8.0.0", "8.0.0")]
    [InlineData("[8.0.0, )", "8.0.0")]
    [InlineData("[8.0.0]", "8.0.0")]
    [InlineData("(, 9.0.0)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LowerBound_ExtractsMinimum(string? range, string? expected)
    {
        PackageVersion.LowerBound(range).Should().Be(expected);
    }
}

public sealed class DefaultIgnoreListTests
{
    [Theory]
    [InlineData("Microsoft.NET.Test.Sdk")]
    [InlineData("xunit.runner.visualstudio")]
    [InlineData("coverlet.collector")]
    [InlineData("COVERLET.MSBUILD")]
    [InlineData("Microsoft.SourceLink.GitHub")]
    [InlineData("Microsoft.EntityFrameworkCore.Design")]
    [InlineData("StyleCop.Analyzers")]
    [InlineData("Microsoft.AspNetCore.SpaProxy")]
    [InlineData("SQLitePCLRaw.bundle_e_sqlite3")]
    public void Match_KnownToolingPackages(string name)
    {
        DefaultIgnoreList.Match(name).Should().NotBeNull();
    }

    [Theory]
    [InlineData("Newtonsoft.Json")]
    [InlineData("xunit")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Serilog")]
    [InlineData("Designer.Core")]
    public void Match_RegularLibraries_NotOnList(string name)
    {
        DefaultIgnoreList.Match(name).Should().BeNull();
    }
}
