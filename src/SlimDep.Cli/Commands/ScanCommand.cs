using SlimDep.Cli.Reporters;

namespace SlimDep.Cli.Commands;

/// <summary><c>slimdep scan &lt;project&gt;</c> — report unused packages.</summary>
internal sealed class ScanCommand : Command
{
    public ScanCommand()
        : base("scan", "Scan a .NET project for unused NuGet package references.")
    {
        Arguments.Add(SharedOptions.ProjectArg);
        Options.Add(SharedOptions.OutputFormatOption);
        Options.Add(SharedOptions.OutputFileOption);
        Options.Add(SharedOptions.IncludeTransitiveOption);
        Options.Add(SharedOptions.IgnoreOption);

        this.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            string project = parseResult.GetValue(SharedOptions.ProjectArg)!;
            string format = (parseResult.GetValue(SharedOptions.OutputFormatOption) ?? "console").ToLowerInvariant();
            string? outputFile = parseResult.GetValue(SharedOptions.OutputFileOption);
            bool includeTransitive = parseResult.GetValue(SharedOptions.IncludeTransitiveOption);
            string[] inlineIgnore = parseResult.GetValue(SharedOptions.IgnoreOption) ?? [];

            bool toFile = !string.IsNullOrEmpty(outputFile);
            bool isConsoleFormat = format == "console";

            // When JSON/SARIF goes to stdout, stdout must contain only that document so it can
            // be piped or parsed. Progress lines are only printed when they can't corrupt it.
            TextWriter status = isConsoleFormat || toFile ? Console.Out : TextWriter.Null;

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
                status.WriteLine($"Found {projects.Count} project(s).");

            var analyzer = new ProjectAnalyzer();
            IReporter reporter = ReporterFactory.Create(format);

            // Parallel scan with bounded concurrency
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
                    scanResults[i] = (null, $"Error ({Path.GetFileName(projectPath)}): {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            })).ConfigureAwait(false);

            // Print results in original project order
            bool anyUnused = false;
            bool anyError = false;
            var successResults = new List<ScanResult>();

            foreach (var (result, error) in scanResults)
            {
                if (error is not null)
                {
                    Console.Error.WriteLine(error);
                    anyError = true;
                    continue;
                }

                if (result is null) continue;

                status.WriteLine($"Scanning {Path.GetFileName(result.ProjectPath)} ...");

                if (result.HasUnused)
                    anyUnused = true;

                successResults.Add(result);

                // Console output streams per project. JSON and SARIF are emitted once, below,
                // as a single well-formed document.
                if (isConsoleFormat && !toFile)
                    Console.WriteLine(reporter.Render(result));
            }

            if (!isConsoleFormat && !toFile)
                Console.WriteLine(reporter.RenderAll(successResults));

            if (toFile)
            {
                string combined = reporter.RenderAll(successResults);
                await File.WriteAllTextAsync(outputFile!, combined, ct).ConfigureAwait(false);
                Console.WriteLine($"Report written to: {outputFile}");
            }

            if (anyError) return 2;
            return anyUnused ? 1 : 0;
        });
    }
}
