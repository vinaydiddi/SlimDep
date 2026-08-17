namespace SlimDep.Cli.Commands;

internal static class SharedOptions
{
    internal static readonly Argument<string> ProjectArg = new("project")
    {
        Description = "Path to a .csproj file or a directory to search recursively for .csproj files.",
    };

    internal static readonly Option<string> OutputFormatOption = new("--format")
    {
        Description = "Output format: console | json | sarif",
        DefaultValueFactory = _ => "console",
    };

    internal static readonly Option<string?> OutputFileOption = new("--output")
    {
        Description = "Write output to this file path instead of stdout.",
    };

    internal static readonly Option<bool> IncludeTransitiveOption = new("--include-transitive")
    {
        Description = "Also report unused transitive (indirect) packages.",
    };

    internal static readonly Option<string[]> IgnoreOption = new("--ignore")
    {
        Description = "Suppress one or more packages from analysis. Can be repeated: --ignore Pkg1 --ignore Pkg2",
        AllowMultipleArgumentsPerToken = true,
    };

    /// <summary>
    /// Resolves the input to one or more .csproj paths.
    /// Directories are searched recursively; obj/ folders are excluded.
    /// </summary>
    internal static IReadOnlyList<string> ResolveProjectPaths(string input)
    {
        string full = Path.GetFullPath(input);

        if (Directory.Exists(full))
        {
            string[] csproj = Directory.GetFiles(full, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                              .Contains("obj", StringComparer.OrdinalIgnoreCase))
                .ToArray();

            if (csproj.Length == 0)
                throw new SlimDepException($"No .csproj file found under '{full}'.");

            return csproj;
        }

        if (!File.Exists(full))
            throw new SlimDepException($"Project path not found: '{full}'");

        return [full];
    }
}
