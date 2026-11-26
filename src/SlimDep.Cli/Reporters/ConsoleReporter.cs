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
        }
        else
        {
            sb.AppendLine($"  ✗ {result.UnusedPackages.Count} unused package(s) found:");
            sb.AppendLine();

            foreach (var pkg in result.UnusedPackages)
            {
                string version = string.IsNullOrEmpty(pkg.Version) ? "" : $" {pkg.Version}";
                string tag = pkg.IsTransitive ? " (transitive)" : "";
                sb.AppendLine($"    • {pkg.Name}{version}{tag}");
                sb.AppendLine($"      {pkg.Reason}");
            }

            sb.AppendLine();
            if (result.UnusedPackages.Any(p => !p.IsTransitive))
                sb.AppendLine("  Run 'slimdep fix <project>' to remove them automatically.");
        }

        if (result.UndeclaredPackages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  ⚠ {result.UndeclaredPackages.Count} package(s) used directly but only referenced transitively (SLIMDEP002):");
            sb.AppendLine();
            foreach (var pkg in result.UndeclaredPackages)
            {
                sb.AppendLine($"    • {pkg.Name} {pkg.Version}");
                sb.AppendLine($"      {pkg.Reason}");
            }
        }

        if (result.PinnedPackages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  ℹ {result.PinnedPackages.Count} package(s) kept as version pins:");
            sb.AppendLine();
            foreach (var pkg in result.PinnedPackages)
            {
                sb.AppendLine($"    • {pkg.Name} {pkg.Version}");
                sb.AppendLine($"      {pkg.Reason}");
            }
        }

        if (result.SkippedPackages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  ℹ {result.SkippedPackages.Count} package(s) not reported (default safe list): "
                          + string.Join(", ", result.SkippedPackages.Select(p => p.Name)));
            sb.AppendLine("    Use --no-default-ignores to analyse them.");
        }

        return sb.ToString();
    }
}
