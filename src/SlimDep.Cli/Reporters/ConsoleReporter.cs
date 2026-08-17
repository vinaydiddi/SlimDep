using System.Text;

namespace SlimDep.Cli.Reporters;

/// <summary>Human-readable console output with ANSI colour hints.</summary>
internal sealed class ConsoleReporter : IReporter
{
    public string Render(ScanResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine($"  Project : {result.ProjectPath}");
        sb.AppendLine($"  Packages: {result.TotalDirectPackages} direct reference(s) analysed");
        sb.AppendLine($"  Duration: {result.Duration.TotalMilliseconds:F0} ms");
        sb.AppendLine();

        if (!result.HasUnused)
        {
            sb.AppendLine("  ✓ All packages are used. Nothing to remove.");
            return sb.ToString();
        }

        sb.AppendLine($"  ✗ {result.UnusedPackages.Count} unused package(s) found:");
        sb.AppendLine();

        foreach (var pkg in result.UnusedPackages)
        {
            string version = string.IsNullOrEmpty(pkg.Version) ? "" : $" {pkg.Version}";
            sb.AppendLine($"    • {pkg.Name}{version}");
            sb.AppendLine($"      {pkg.Reason}");
        }

        sb.AppendLine();
        sb.AppendLine("  Run 'slimdep fix <project>' to remove them automatically.");

        return sb.ToString();
    }
}
