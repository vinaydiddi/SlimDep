using System.Xml;

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
                    scanResults[i] = (null, $"{Path.GetFileName(projectPath)}: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            })).ConfigureAwait(false);

            // Sequential fix phase (file writes must be sequential for safety)
            bool hadError = false;
            bool anyWritten = false;

            foreach (var (result, errorMsg) in scanResults)
            {
                if (errorMsg is not null)
                {
                    Console.Error.WriteLine($"Error: {errorMsg}");
                    hadError = true;
                    continue;
                }

                if (result is null) continue;

                string fileName = Path.GetFileName(result.ProjectPath);
                Console.WriteLine($"\nScanning {fileName} ...");

                if (!result.HasUnused)
                {
                    Console.WriteLine("  No unused packages found.");
                    continue;
                }

                var direct = result.UnusedPackages.Where(p => !p.IsTransitive).ToList();
                var transitive = result.UnusedPackages.Where(p => p.IsTransitive).ToList();

                Console.WriteLine($"  Unused packages ({result.UnusedPackages.Count}):");
                foreach (var pkg in direct)
                    Console.WriteLine($"    - {pkg.Name} {pkg.Version}");
                foreach (var pkg in transitive)
                    Console.WriteLine($"    - {pkg.Name} {pkg.Version} (transitive)");

                if (transitive.Count > 0)
                    Console.WriteLine($"  Note: {transitive.Count} transitive package(s) cannot be removed from the project file; they are pulled in by other packages.");

                if (direct.Count == 0)
                    continue;

                if (dryRun)
                {
                    Console.WriteLine("  [dry-run] No changes written.");
                    continue;
                }

                IReadOnlyList<string> removed;
                try
                {
                    removed = CsprojEditor.RemovePackageReferences(result.ProjectPath, direct.Select(p => p.Name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
                {
                    Console.Error.WriteLine($"Error: could not update {fileName}: {ex.Message}");
                    hadError = true;
                    continue;
                }

                if (removed.Count > 0)
                {
                    anyWritten = true;
                    Console.WriteLine($"  Removed {removed.Count} package(s) from {fileName}.");
                }

                var notFound = direct
                    .Select(p => p.Name)
                    .Where(n => !removed.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (notFound.Count > 0)
                {
                    Console.WriteLine($"  Not removed — no <PackageReference> for these in {fileName} (likely declared in Directory.Build.props or an imported file):");
                    foreach (string name in notFound)
                        Console.WriteLine($"    - {name}");
                }
            }

            if (anyWritten)
                Console.WriteLine("\nRun 'dotnet restore' to update the lockfile.");

            return hadError ? 2 : 0;
        });
    }
}
