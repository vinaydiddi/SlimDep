using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace SlimDep.Analysis;

/// <summary>
/// Orchestrates the full unused-package analysis for a single .NET project.
/// </summary>
public sealed class ProjectAnalyzer : IProjectAnalyzer
{
    internal const string DirectReason = "No namespace from this package appears in any using directive";

    public async Task<ScanResult> AnalyzeAsync(ScanOptions options)
    {
        string projectPath = Path.GetFullPath(options.ProjectPath);

        if (!File.Exists(projectPath))
            throw new SlimDepException($"Project file not found: '{projectPath}'");

        string projectDir = Path.GetDirectoryName(projectPath)
            ?? throw new SlimDepException($"Cannot determine directory for '{projectPath}'");

        string assetsFile = Path.Combine(projectDir, "obj", "project.assets.json");

        var sw = Stopwatch.StartNew();

        // 1. Parse project.assets.json → list of (package, version, namespaces)
        IReadOnlyList<PackageNamespaceInfo> packageInfos = await Task.Run(
            () => AssetsFileParser.Parse(assetsFile, options.IncludeTransitive),
            options.CancellationToken).ConfigureAwait(false);

        // 2. Collect all used namespaces from .cs, .xaml, and .razor source files,
        //    plus global usings declared in MSBuild (ImplicitUsings / <Using> items)
        HashSet<string> usedNamespaces = await Task.Run(() =>
        {
            var ns = SourceAnalyzer.CollectUsedNamespaces(projectDir, options.CancellationToken);
            ns.UnionWith(ProjectUsingsResolver.Resolve(projectPath, options.CancellationToken));
            return ns;
        }, options.CancellationToken).ConfigureAwait(false);

        // 3. Load .slimdepignore (walk up from project directory to solution root)
        //    and merge with any inline --ignore values passed via CLI
        IReadOnlySet<string> fileIgnored = IgnoreFileParser.Load(projectDir);
        var allIgnored = new HashSet<string>(fileIgnored, StringComparer.OrdinalIgnoreCase);
        if (options.InlineIgnoredPackages is not null)
            foreach (string pkg in options.InlineIgnoredPackages)
                allIgnored.Add(pkg);

        // 4. Detect CPM GlobalPackageReferences (applied globally, cannot be removed per-project)
        IReadOnlySet<string> globalPackageRefs = GetGlobalPackageReferences(projectDir);

        // 5. Cross-reference: which packages have no namespace overlap with used namespaces?
        var unused = new List<UnusedPackage>();

        foreach (var pkg in packageInfos)
        {
            options.CancellationToken.ThrowIfCancellationRequested();

            if (pkg.Namespaces.Count == 0)
                continue; // build tool / source generator / contentFiles-only — skip

            // Skip packages explicitly ignored via .slimdepignore or --ignore flag
            if (allIgnored.Contains(pkg.Name))
                continue;

            // Skip packages applied globally via CPM GlobalPackageReference
            if (globalPackageRefs.Contains(pkg.Name))
                continue;

            bool hasMatch = pkg.Namespaces.Any(ns => usedNamespaces.Contains(ns));
            if (hasMatch)
                continue;

            if (pkg.IsDirect)
            {
                unused.Add(new UnusedPackage(pkg.Name, pkg.Version, DirectReason));
            }
            else
            {
                string via = pkg.IntroducedBy.Count > 0
                    ? $" (pulled in by {string.Join(", ", pkg.IntroducedBy)})"
                    : string.Empty;
                unused.Add(new UnusedPackage(
                    pkg.Name,
                    pkg.Version,
                    $"Transitive dependency{via}; no namespace from it is used directly")
                {
                    IsTransitive = true,
                });
            }
        }

        // 6. Enrich unused direct packages with line numbers from the .csproj XML
        var unusedNames = new HashSet<string>(
            unused.Where(p => !p.IsTransitive).Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);
        var lineNumbers = GetPackageReferenceLineNumbers(projectPath, unusedNames);
        var unusedWithLines = unused
            .Select(p => !p.IsTransitive && lineNumbers.TryGetValue(p.Name, out int line) ? p with { LineNumber = line } : p)
            .OrderBy(p => p.IsTransitive)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sw.Stop();

        return new ScanResult
        {
            ProjectPath = projectPath,
            UnusedPackages = unusedWithLines,
            TotalDirectPackages = packageInfos.Count(p => p.IsDirect),
            Duration = sw.Elapsed,
        };
    }

    /// <summary>
    /// Reads the .csproj with line info and returns a map of package name → line number
    /// for each <c>&lt;PackageReference&gt;</c> in <paramref name="packageNames"/>.
    /// </summary>
    private static Dictionary<string, int> GetPackageReferenceLineNumbers(string csprojPath, HashSet<string> packageNames)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (packageNames.Count == 0) return result;
        try
        {
            var doc = XDocument.Load(csprojPath, LoadOptions.SetLineInfo);
            // Match on local name so legacy projects with the MSBuild xmlns are handled too
            foreach (var elem in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                string? include = elem.Attribute("Include")?.Value;
                if (include is not null && packageNames.Contains(include)
                    && elem is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
                {
                    result[include] = lineInfo.LineNumber;
                }
            }
        }
        catch { /* best-effort: skip on any parse error */ }
        return result;
    }

    /// <summary>
    /// Walks up from <paramref name="projectDirectory"/> detecting CPM
    /// <c>GlobalPackageReference</c> elements in <c>Directory.Packages.props</c>
    /// and <c>Directory.Build.props</c>. These packages are applied to all projects
    /// globally and cannot be removed from individual project files.
    /// </summary>
    private static IReadOnlySet<string> GetGlobalPackageReferences(string projectDirectory)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? dir = projectDirectory;

        while (dir is not null)
        {
            foreach (string propsFileName in new[] { "Directory.Packages.props", "Directory.Build.props" })
            {
                string propsPath = Path.Combine(dir, propsFileName);
                if (!File.Exists(propsPath)) continue;

                try
                {
                    var doc = XDocument.Load(propsPath);
                    foreach (var elem in doc.Descendants().Where(e => e.Name.LocalName == "GlobalPackageReference"))
                    {
                        string? include = elem.Attribute("Include")?.Value;
                        if (include is not null)
                            result.Add(include);
                    }
                }
                catch { /* skip malformed props files */ }
            }

            string? parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        return result;
    }
}
