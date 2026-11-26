using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlimDep.Cli.Reporters;

/// <summary>
/// Produces SARIF 2.1.0 output — consumed by GitHub Code Scanning, Azure DevOps, and
/// any tool that understands the Static Analysis Results Interchange Format.
/// </summary>
internal sealed class SarifReporter : IReporter
{
    private const string SarifSchema = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json";
    private const string SarifVersion = "2.1.0";
    private const string RuleId = "SLIMDEP001";
    private const string ToolName = "SlimDep";
    private const string ToolUri = "https://www.nuget.org/packages/SlimDep";

    /// <summary>
    /// Tool version taken from the assembly (set by &lt;Version&gt; in the .csproj), so it can
    /// never drift from the package version. Any "+commit" build metadata is stripped.
    /// </summary>
    internal static readonly string ToolVersion = GetToolVersion();

    private static string GetToolVersion()
    {
        var assembly = typeof(SarifReporter).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString(3);
        if (string.IsNullOrEmpty(version)) return "0.0.0";
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Renders a single project as a self-contained SARIF document.</summary>
    public string Render(ScanResult result) => RenderAll([result]);

    /// <summary>
    /// Renders all results as one SARIF document with a single run.
    /// All per-project findings appear as results under that run, and each scanned
    /// .csproj is listed in the artifacts array.
    /// </summary>
    public string RenderAll(IReadOnlyList<ScanResult> results)
    {
        var root = new JsonObject
        {
            ["$schema"] = SarifSchema,
            ["version"] = SarifVersion,
            ["runs"] = new JsonArray(BuildRun(results)),
        };

        return root.ToJsonString(WriteOptions);
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    private static JsonObject BuildRun(IReadOnlyList<ScanResult> results)
    {
        return new JsonObject
        {
            ["tool"] = BuildTool(),
            ["results"] = BuildResults(results),
            ["artifacts"] = BuildArtifacts(results),
        };
    }

    // ── Tool descriptor ───────────────────────────────────────────────────────

    private static JsonObject BuildTool()
    {
        var rule = new JsonObject
        {
            ["id"] = RuleId,
            ["name"] = "UnusedPackageReference",
            ["shortDescription"] = new JsonObject
            {
                ["text"] = "Unused NuGet package reference detected.",
            },
            ["fullDescription"] = new JsonObject
            {
                ["text"] = "The project contains a <PackageReference> whose namespaces do not appear in any source file. "
                         + "The package may be safe to remove. Run 'slimdep fix' to remove it automatically, "
                         + "or 'slimdep ignore add <package>' to suppress this warning.",
            },
            ["helpUri"] = ToolUri,
            ["defaultConfiguration"] = new JsonObject { ["level"] = "warning" },
        };

        return new JsonObject
        {
            ["driver"] = new JsonObject
            {
                ["name"] = ToolName,
                ["version"] = ToolVersion,
                ["informationUri"] = ToolUri,
                ["rules"] = new JsonArray(rule),
            },
        };
    }

    // ── Results ───────────────────────────────────────────────────────────────

    private static JsonArray BuildResults(IReadOnlyList<ScanResult> results)
    {
        var arr = new JsonArray();

        foreach (var scanResult in results)
        {
            foreach (var pkg in scanResult.UnusedPackages)
            {
                string version = string.IsNullOrEmpty(pkg.Version) ? string.Empty : $" {pkg.Version}";
                string kind = pkg.IsTransitive ? "Unused transitive package" : "Unused package reference";
                string messageText = $"{kind}: {pkg.Name}{version}. {pkg.Reason}.";

                var physicalLocation = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject
                    {
                        ["uri"] = ToFileUri(scanResult.ProjectPath),
                    },
                };

                if (pkg.LineNumber.HasValue)
                    physicalLocation["region"] = new JsonObject { ["startLine"] = pkg.LineNumber.Value };

                arr.Add(new JsonObject
                {
                    ["ruleId"] = RuleId,
                    // Transitive findings are informational: they can't be fixed in the project file
                    ["level"] = pkg.IsTransitive ? "note" : "warning",
                    ["message"] = new JsonObject { ["text"] = messageText },
                    ["locations"] = new JsonArray(new JsonObject
                    {
                        ["physicalLocation"] = physicalLocation,
                    }),
                });
            }
        }

        return arr;
    }

    // ── Artifacts ─────────────────────────────────────────────────────────────

    private static JsonArray BuildArtifacts(IReadOnlyList<ScanResult> results)
    {
        var arr = new JsonArray();
        foreach (var r in results)
        {
            arr.Add(new JsonObject
            {
                ["location"] = new JsonObject { ["uri"] = ToFileUri(r.ProjectPath) },
            });
        }
        return arr;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToFileUri(string absolutePath) => new Uri(absolutePath).AbsoluteUri;
}
