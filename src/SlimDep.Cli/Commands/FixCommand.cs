using System.Xml.Linq;

namespace SlimDep.Cli.Commands;

/// <summary><c>slimdep fix &lt;project&gt;</c> — remove unused packages from the .csproj.</summary>
internal sealed class FixCommand : Command
{
    private static readonly Option<bool> DryRunOption = new("--dry-run")
    {
        Description = "Show what would be removed without writing changes.",
    };

    public FixCommand()
        : base("fix", "Remove unused NuGet package references from a project file.")
    {
        Arguments.Add(SharedOptions.ProjectArg);
        Options.Add(DryRunOption);
        Options.Add(SharedOptions.IncludeTransitiveOption);
        Options.Add(SharedOptions.IgnoreOption);

        this.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            string project = parseResult.GetValue(SharedOptions.ProjectArg)!;
            bool dryRun = parseResult.GetValue(DryRunOption);
            bool includeTransitive = parseResult.GetValue(SharedOptions.IncludeTransitiveOption);
            string[] inlineIgnore = parseResult.GetValue(SharedOptions.IgnoreOption) ?? [];

            IReadOnlyList<string> projects;
            try
            {
                projects = SharedOptions.ResolveProjectPaths(project);
            }
            catch (SlimDepException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 2;
            }

            if (projects.Count > 1)
                Console.WriteLine($"Found {projects.Count} project(s).");

            var analyzer = new ProjectAnalyzer();

            // Parallel scan phase
            int maxParallel = Math.Max(1, Math.Min(projects.Count, Environment.ProcessorCount));
            using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
            var scanResults = new (ScanResult? result, string? error)[projects.Count];

            await Task.WhenAll(projects.Select(async (projectPath, i) =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    scanResults[i] = (await analyzer.AnalyzeAsync(new ScanOptions
                    {
                        ProjectPath = projectPath,
                        IncludeTransitive = includeTransitive,
                        InlineIgnoredPackages = inlineIgnore.Length > 0 ? inlineIgnore : null,
                        CancellationToken = ct,
                    }).ConfigureAwait(false), null);
                }
                catch (SlimDepException ex)
                {
                    scanResults[i] = (null, ex.Message);
                }
                finally
                {
                    semaphore.Release();
                }
            })).ConfigureAwait(false);

            // Sequential fix phase (file writes must be sequential for safety)
            foreach (var (result, errorMsg) in scanResults)
            {
                if (errorMsg is not null)
                {
                    Console.Error.WriteLine($"Error: {errorMsg}");
                    continue;
                }

                if (result is null) continue;

                Console.WriteLine($"\nScanning {Path.GetFileName(result.ProjectPath)} ...");

                if (!result.HasUnused)
                {
                    Console.WriteLine("  No unused packages found.");
                    continue;
                }

                Console.WriteLine($"  Unused packages ({result.UnusedPackages.Count}):");
                foreach (var pkg in result.UnusedPackages)
                    Console.WriteLine($"    - {pkg.Name} {pkg.Version}");

                if (dryRun)
                {
                    Console.WriteLine("  [dry-run] No changes written.");
                    continue;
                }

                RemovePackagesFromCsproj(result.ProjectPath, result.UnusedPackages);
                Console.WriteLine($"  Removed {result.UnusedPackages.Count} package(s) from {Path.GetFileName(result.ProjectPath)}.");
            }

            if (!dryRun)
                Console.WriteLine("\nRun 'dotnet restore' to update the lockfile.");

            return 0;
        });
    }

    private static void RemovePackagesFromCsproj(string csprojPath, IReadOnlyList<UnusedPackage> toRemove)
    {
        var toRemoveNames = new HashSet<string>(
            toRemove.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        var doc = XDocument.Load(csprojPath);

        var removed = doc.Descendants("PackageReference")
            .Where(e =>
            {
                string? include = e.Attribute("Include")?.Value;
                return include is not null && toRemoveNames.Contains(include);
            })
            .ToList();

        foreach (var element in removed)
        {
            var parent = element.Parent;
            element.Remove();
            if (parent is not null && !parent.HasElements)
                parent.Remove();
        }

        doc.Save(csprojPath);
    }
}
