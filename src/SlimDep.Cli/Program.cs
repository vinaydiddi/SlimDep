using SlimDep.Cli.Commands;

var rootCommand = new RootCommand("SlimDep — detect unused NuGet package references in .NET projects.");
rootCommand.Subcommands.Add(new ScanCommand());
rootCommand.Subcommands.Add(new FixCommand());
rootCommand.Subcommands.Add(new IgnoreCommand());

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
