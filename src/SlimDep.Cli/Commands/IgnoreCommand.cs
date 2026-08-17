namespace SlimDep.Cli.Commands;

/// <summary><c>slimdep ignore</c> — manage the <c>.slimdepignore</c> file.</summary>
internal sealed class IgnoreCommand : Command
{
    public IgnoreCommand()
        : base("ignore", "Manage the .slimdepignore file to suppress packages from analysis.")
    {
        Subcommands.Add(new IgnoreAddCommand());
        Subcommands.Add(new IgnoreRemoveCommand());
        Subcommands.Add(new IgnoreListCommand());
    }
}

// ── slimdep ignore add <package> ─────────────────────────────────────────────

internal sealed class IgnoreAddCommand : Command
{
    private static readonly Argument<string> PackageArg = new("package")
    {
        Description = "Name of the NuGet package to suppress.",
    };

    private static readonly Option<string?> PathOption = new("--path")
    {
        Description = "Directory where .slimdepignore will be created or updated. Defaults to current directory.",
    };

    public IgnoreAddCommand()
        : base("add", "Add a package to .slimdepignore.")
    {
        Arguments.Add(PackageArg);
        Options.Add(PathOption);

        this.SetAction((ParseResult parseResult) =>
        {
            string package = parseResult.GetValue(PackageArg)!.Trim();
            string dir = Path.GetFullPath(parseResult.GetValue(PathOption) ?? Directory.GetCurrentDirectory());

            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"Error: Directory not found: '{dir}'");
                return 1;
            }

            string ignoreFile = Path.Combine(dir, ".slimdepignore");

            if (File.Exists(ignoreFile))
            {
                bool alreadyPresent = File.ReadLines(ignoreFile)
                    .Any(l => string.Equals(l.Trim(), package, StringComparison.OrdinalIgnoreCase));

                if (alreadyPresent)
                {
                    Console.WriteLine($"'{package}' is already in .slimdepignore.");
                    return 0;
                }
            }

            File.AppendAllText(ignoreFile, $"{package}{Environment.NewLine}");
            Console.WriteLine($"Added '{package}' to {ignoreFile}");
            return 0;
        });
    }
}

// ── slimdep ignore remove <package> ──────────────────────────────────────────

internal sealed class IgnoreRemoveCommand : Command
{
    private static readonly Argument<string> PackageArg = new("package")
    {
        Description = "Name of the NuGet package to remove from suppression.",
    };

    private static readonly Option<string?> PathOption = new("--path")
    {
        Description = "Directory containing .slimdepignore. Defaults to current directory.",
    };

    public IgnoreRemoveCommand()
        : base("remove", "Remove a package from .slimdepignore.")
    {
        Arguments.Add(PackageArg);
        Options.Add(PathOption);

        this.SetAction((ParseResult parseResult) =>
        {
            string package = parseResult.GetValue(PackageArg)!.Trim();
            string dir = Path.GetFullPath(parseResult.GetValue(PathOption) ?? Directory.GetCurrentDirectory());
            string ignoreFile = Path.Combine(dir, ".slimdepignore");

            if (!File.Exists(ignoreFile))
            {
                Console.Error.WriteLine($"Error: .slimdepignore not found in '{dir}'");
                return 1;
            }

            var lines = new List<string>(File.ReadAllLines(ignoreFile));
            int removed = lines.RemoveAll(l =>
                string.Equals(l.Trim(), package, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                Console.WriteLine($"'{package}' was not found in .slimdepignore.");
                return 0;
            }

            File.WriteAllLines(ignoreFile, lines);
            Console.WriteLine($"Removed '{package}' from {ignoreFile}");
            return 0;
        });
    }
}

// ── slimdep ignore list ───────────────────────────────────────────────────────

internal sealed class IgnoreListCommand : Command
{
    private static readonly Option<string?> PathOption = new("--path")
    {
        Description = "Starting directory to search for .slimdepignore (walks up to solution root). Defaults to current directory.",
    };

    public IgnoreListCommand()
        : base("list", "List all packages in .slimdepignore.")
    {
        Options.Add(PathOption);

        this.SetAction((ParseResult parseResult) =>
        {
            string startDir = Path.GetFullPath(parseResult.GetValue(PathOption) ?? Directory.GetCurrentDirectory());

            // Walk up to find the ignore file (same behaviour as IgnoreFileParser.Load)
            string? dir = startDir;
            string? foundFile = null;
            while (dir is not null)
            {
                string candidate = Path.Combine(dir, ".slimdepignore");
                if (File.Exists(candidate)) { foundFile = candidate; break; }
                string? parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }

            if (foundFile is null)
            {
                Console.WriteLine("No .slimdepignore file found.");
                Console.WriteLine($"  Run 'slimdep ignore add <package>' to create one.");
                return 0;
            }

            var entries = File.ReadLines(foundFile)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                .ToList();

            Console.WriteLine($".slimdepignore: {foundFile}");
            Console.WriteLine();

            if (entries.Count == 0)
                Console.WriteLine("  (empty)");
            else
                foreach (string entry in entries)
                    Console.WriteLine($"  {entry}");

            return 0;
        });
    }
}
