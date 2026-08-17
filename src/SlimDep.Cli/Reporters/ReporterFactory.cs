namespace SlimDep.Cli.Reporters;

internal static class ReporterFactory
{
    internal static IReporter Create(string format) =>
        format.ToLowerInvariant() switch
        {
            "json"    => new JsonReporter(),
            "sarif"   => new SarifReporter(),
            "console" or _ => new ConsoleReporter(),
        };
}
