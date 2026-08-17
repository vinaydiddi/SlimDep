using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimDep.Cli.Reporters;

/// <summary>Machine-readable JSON output.</summary>
internal sealed class JsonReporter : IReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Render(ScanResult result)
    {
        var payload = new
        {
            projectPath = result.ProjectPath,
            totalDirectPackages = result.TotalDirectPackages,
            unusedCount = result.UnusedPackages.Count,
            durationMs = Math.Round(result.Duration.TotalMilliseconds, 2),
            unusedPackages = result.UnusedPackages.Select(p => new
            {
                name = p.Name,
                version = string.IsNullOrEmpty(p.Version) ? null : p.Version,
                reason = p.Reason,
            }),
        };

        return JsonSerializer.Serialize(payload, Options);
    }
}
