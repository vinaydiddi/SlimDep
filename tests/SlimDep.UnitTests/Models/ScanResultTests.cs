namespace SlimDep.UnitTests.Models;

public sealed class ScanResultTests
{
    [Fact]
    public void HasUnused_WhenNoUnusedPackages_ReturnsFalse()
    {
        var result = new ScanResult
        {
            ProjectPath = "test.csproj",
            UnusedPackages = Array.Empty<UnusedPackage>(),
            TotalDirectPackages = 3,
            Duration = TimeSpan.Zero,
        };

        result.HasUnused.Should().BeFalse();
    }

    [Fact]
    public void HasUnused_WhenUnusedPackagesExist_ReturnsTrue()
    {
        var result = new ScanResult
        {
            ProjectPath = "test.csproj",
            UnusedPackages = new[] { new UnusedPackage("Foo", "1.0.0", "reason") },
            TotalDirectPackages = 2,
            Duration = TimeSpan.Zero,
        };

        result.HasUnused.Should().BeTrue();
    }
}
