using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace SlimDep.Analysis;

/// <summary>
/// Orchestrates the full package analysis for a single .NET project.
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

        var ct = options.CancellationToken;
        var sw = Stopwatch.StartNew();

        // 1. Parse project.assets.json → packages, namespaces and the dependency graph.
        //    Its location honours UseArtifactsOutput / BaseIntermediateOutputPath.
        string assetsFile = ProjectLayout.FindAssetsFile(projectPath);
        string intermediateDir = Path.GetDirectoryName(assetsFile)!;
        AssetsInfo assets = await Task.Run(() => AssetsFileParser.Parse(assetsFile), ct).ConfigureAwait(false);

        // 2. Collect namespaces referenced from .cs, .xaml and .razor files (including <Compile>
        //    items and shared projects outside the project folder), plus global usings declared in
        //    MSBuild (ImplicitUsings / <Using> items / generated GlobalUsings.g.cs)
        NamespaceSet used = await Task.Run(() =>
        {
            var compiled = ProjectLayout.CompiledSourceFiles(projectPath, ct);
            var ns = SourceAnalyzer.Collect(projectDir, ct, compiled);
            ProjectUsingsResolver.ResolveInto(projectPath, ns, intermediateDir, ct);
            return ns;
        }, ct).ConfigureAwait(false);

        // 3. Load .slimdepignore (walk up from project directory to solution root)
        //    and merge with any inline --ignore values passed via CLI
        IReadOnlySet<string> fileIgnored = IgnoreFileParser.Load(projectDir);
        var allIgnored = new HashSet<string>(fileIgnored, StringComparer.OrdinalIgnoreCase);
        if (options.InlineIgnoredPackages is not null)
            foreach (string pkg in options.InlineIgnoredPackages)
                allIgnored.Add(pkg);

        // 4. Detect CPM GlobalPackageReferences (applied globally, cannot be removed per-project)
        IReadOnlySet<string> globalPackageRefs = GetGlobalPackageReferences(projectDir);

        var byName = assets.Packages.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var unused = new List<UnusedPackage>();
        var pinned = new List<PinnedPackage>();
        var skipped = new List<SkippedPackage>();
        var undeclaredCandidates = new List<PackageNamespaceInfo>();

        // 5. Classify every package
        foreach (var pkg in assets.Packages)
        {
            ct.ThrowIfCancellationRequested();

            if (pkg.Namespaces.Count == 0)
                continue; // metapackage / build tool / source generator — nothing to match

            if (allIgnored.Contains(pkg.Name) || globalPackageRefs.Contains(pkg.Name))
                continue;

            bool isUsed = pkg.Namespaces.Any(used.All.Contains);

            if (!pkg.IsDirect)
            {
                if (isUsed)
                {
                    undeclaredCandidates.Add(pkg);
                }
                else if (options.IncludeTransitive)
                {
                    // Runtime-loaded and tooling packages are just as legitimate when transitive
                    if (options.UseDefaultIgnores && DefaultIgnoreList.Match(pkg.Name) is string transitivePattern)
                        skipped.Add(new SkippedPackage(pkg.Name, pkg.Version, $"Transitive; on the built-in safe list ({transitivePattern})"));
                    else
                        unused.Add(TransitiveUnused(pkg));
                }
                continue;
            }

            if (isUsed)
                continue;

            // Looks unused. Before reporting it, apply the built-in safe list...
            if (options.UseDefaultIgnores)
            {
                if (DefaultIgnoreList.Match(pkg.Name) is string pattern)
                {
                    skipped.Add(new SkippedPackage(pkg.Name, pkg.Version, $"On the built-in safe list ({pattern})"));
                    continue;
                }
                if (pkg.PrivateAssetsAll)
                {
                    skipped.Add(new SkippedPackage(pkg.Name, pkg.Version, "Marked PrivateAssets=\"all\" (development-only dependency)"));
                    continue;
                }
            }

            // ...and keep references that exist to raise the version of a transitive dependency.
            if (FindPin(pkg) is PinnedPackage pin)
            {
                pinned.Add(pin);
                continue;
            }

            unused.Add(new UnusedPackage(pkg.Name, pkg.Version, DirectReason));
        }

        // 6. SLIMDEP002: transitive packages the code uses directly
        var undeclared = FindUndeclared(undeclaredCandidates, assets, byName, used);

        // 7. Enrich unused direct packages with line numbers from the .csproj XML
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
            UndeclaredPackages = undeclared,
            PinnedPackages = pinned.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            SkippedPackages = skipped.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            TotalDirectPackages = assets.Packages.Count(p => p.IsDirect && !p.HasNoAssemblies),
            Duration = sw.Elapsed,
        };
    }

    private static UnusedPackage TransitiveUnused(PackageNamespaceInfo pkg)
    {
        string via = pkg.IntroducedBy.Count > 0
            ? $" (pulled in by {string.Join(", ", pkg.IntroducedBy)})"
            : string.Empty;
        return new UnusedPackage(pkg.Name, pkg.Version, $"Transitive dependency{via}; no namespace from it is used directly")
        {
            IsTransitive = true,
        };
    }

    /// <summary>
    /// A direct reference is a version pin on a target framework when other packages in that
    /// target's graph depend on it, but all of them ask for a lower version than it resolves to.
    /// Without the direct reference NuGet would resolve the highest of those lower bounds, i.e. a
    /// downgrade — often re-introducing a vulnerable version. Each target is checked on its own,
    /// because dependencies differ by TFM; the reference is kept if it pins on any target.
    /// </summary>
    internal static PinnedPackage? FindPin(PackageNamespaceInfo pkg)
    {
        var pinnedTargets = new List<string>();
        string? lowestFallback = null;
        string? pinnedVersion = null;
        var requiredBy = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in pkg.Targets)
        {
            var reqs = target.RequiredBy
                .Where(r => !r.Dependent.Equals(pkg.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (reqs.Count == 0) continue;

            string fallback = reqs
                .Select(r => r.MinVersion)
                .Aggregate((a, b) => PackageVersion.Compare(a, b) >= 0 ? a : b);

            if (PackageVersion.Compare(fallback, target.ResolvedVersion) >= 0)
                continue; // removing it would keep the same version on this target — not a pin here

            pinnedTargets.Add(target.TargetFramework);
            requiredBy.UnionWith(reqs.Select(r => r.Dependent));
            if (lowestFallback is null || PackageVersion.Compare(fallback, lowestFallback) < 0)
            {
                lowestFallback = fallback;
                pinnedVersion = target.ResolvedVersion;
            }
        }

        if (lowestFallback is null) return null;

        // Name the TFMs only when it disambiguates (the reference doesn't pin on every target)
        IReadOnlyList<string>? tfms = pinnedTargets.Count < pkg.Targets.Count ? pinnedTargets : null;
        return new PinnedPackage(pkg.Name, pinnedVersion ?? pkg.Version, lowestFallback, requiredBy.ToList(), tfms);
    }

    /// <summary>
    /// Finds transitive packages whose namespaces the code references explicitly. To stay
    /// precise, a namespace is ignored when it is also provided by a direct package or by the
    /// shared framework, and a package is ignored when it only arrives through a project
    /// reference or through a direct metapackage (e.g. <c>xunit</c> → <c>xunit.assert</c>), which
    /// already acts as its declaration.
    /// </summary>
    private static IReadOnlyList<UndeclaredPackage> FindUndeclared(
        List<PackageNamespaceInfo> candidates,
        AssetsInfo assets,
        Dictionary<string, PackageNamespaceInfo> byName,
        NamespaceSet used)
    {
        if (candidates.Count == 0) return [];

        var directNamespaces = new HashSet<string>(
            assets.Packages.Where(p => p.IsDirect).SelectMany(p => p.Namespaces),
            StringComparer.Ordinal);

        // Framework namespaces depend on which target frameworks the package is present in: a
        // System.Text.Json package that only exists in the net8.0 graph is exempt there, even if
        // the project also targets net48. Cached per distinct set of frameworks.
        var frameworkCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> FrameworkNamespacesFor(PackageNamespaceInfo pkg)
        {
            var tfms = pkg.Targets.Select(t => t.TargetFramework).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (tfms.Count == 0) tfms = assets.TargetFrameworks.ToList();
            string key = string.Join(";", tfms.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
            if (!frameworkCache.TryGetValue(key, out var set))
                frameworkCache[key] = set = FrameworkNamespaceProvider.GetNamespaces(assets.FrameworkReferences, tfms);
            return set;
        }

        var result = new List<UndeclaredPackage>();

        foreach (var pkg in candidates)
        {
            var packageIntroducers = pkg.IntroducedBy
                .Where(i => !i.StartsWith("project ", StringComparison.Ordinal))
                .ToList();

            // Only via own projects: you control those, so the dependency can't vanish silently
            if (packageIntroducers.Count == 0) continue;

            // A direct metapackage is the declaration of what it brings in
            if (packageIntroducers.Any(i => byName.TryGetValue(i, out var d) && d.HasNoAssemblies)) continue;

            var usedNamespaces = pkg.Namespaces
                .Where(ns => used.Explicit.Contains(ns) && !directNamespaces.Contains(ns))
                .ToList();
            if (usedNamespaces.Count == 0) continue;

            var frameworkNamespaces = FrameworkNamespacesFor(pkg);
            usedNamespaces.RemoveAll(frameworkNamespaces.Contains);
            if (usedNamespaces.Count == 0) continue;

            usedNamespaces.Sort(StringComparer.Ordinal);
            result.Add(new UndeclaredPackage(pkg.Name, pkg.Version, packageIntroducers, usedNamespaces));
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
