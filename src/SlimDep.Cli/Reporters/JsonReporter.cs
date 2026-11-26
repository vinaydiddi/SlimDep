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

    public string Render(ScanResult result) => JsonSerializer.Serialize(BuildPayload(result), Options);

    /// <summary>
    /// A single project renders as one JSON object (same as <see cref="Render"/>);
    /// zero or several projects render as one JSON array, so the output is always one valid document.
    /// </summary>
    public string RenderAll(IReadOnlyList<ScanResult> results) =>
        results.Count == 1
            ? Render(results[0])
            : JsonSerializer.Serialize(results.Select(BuildPayload), Options);

    private static object BuildPayload(ScanResult result) => new
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
            // Omitted for direct packages so the output shape is unchanged for existing consumers
            transitive = p.IsTransitive ? true : (bool?)null,
        }),
    };
}
