using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace SlimDep.Analysis;

/// <summary>
/// Parses <c>project.assets.json</c> and resolves which namespaces each package exposes,
/// by reading type metadata from the DLLs in the local NuGet package cache. Also records the
/// dependency graph (who brings in which package, at which minimum version).
/// Namespace results are cached in-process for speed.
/// </summary>
internal sealed class AssetsFileParser
{
    // Cache: "{packageId}/{version}|{dll}" → set of namespaces
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> _namespaceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns every direct package and every transitive package that has assemblies, with
    /// exported namespaces and dependency information. All target frameworks are merged, so
    /// multi-targeted projects are fully covered.
    /// </summary>
    internal static AssetsInfo Parse(string assetsFilePath)
    {
        if (!File.Exists(assetsFilePath))
            throw new SlimDepException($"project.assets.json not found at '{assetsFilePath}'. Run 'dotnet restore' first.");

        JsonNode doc;
        using (var stream = File.OpenRead(assetsFilePath))
        {
            try
            {
                doc = JsonNode.Parse(stream) ?? throw new SlimDepException("Failed to parse project.assets.json.");
            }
            catch (JsonException ex)
            {
                throw new SlimDepException($"Failed to parse project.assets.json: {ex.Message}");
            }
        }

        // 1. Get the NuGet package cache root
        var packageFolders = doc["packageFolders"]?.AsObject();
        string cacheRoot = packageFolders?.FirstOrDefault().Key
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        cacheRoot = cacheRoot.TrimEnd('/', '\\');

        // 2. Identify direct package references from project.frameworks.<tfm>.dependencies
        //    across ALL target frameworks. Implicit SDK references (autoReferenced) are skipped
        //    because they are not in the project file and cannot be removed.
        var directNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var autoReferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var privateAssetsAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworkReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = doc["project"]?["frameworks"]?.AsObject();
        if (frameworks is not null)
        {
            foreach (var fw in frameworks)
            {
                if (fw.Value?["dependencies"] is JsonObject deps)
                {
                    foreach (var dep in deps)
                    {
                        if (GetBool(dep.Value, "autoReferenced"))
                        {
                            autoReferenced.Add(dep.Key);
                            continue;
                        }
                        directNames.Add(dep.Key);

                        // PrivateAssets="all" is recorded as suppressParent: "All"
                        if (GetString(dep.Value, "suppressParent")?.Contains("All", StringComparison.OrdinalIgnoreCase) == true)
                            privateAssetsAll.Add(dep.Key);
                    }
                }

                if (fw.Value?["frameworkReferences"] is JsonObject fwRefs)
                    foreach (var fwRef in fwRefs)
                        frameworkReferences.Add(fwRef.Key);
            }
        }

        // A package referenced explicitly by any TFM is a real reference, not an SDK implicit one
        autoReferenced.ExceptWith(directNames);

        // 3. Get the library path map: "{Name/Version}" → relative path in cache
        var libraryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var libraries = doc["libraries"]?.AsObject();
        if (libraries is not null)
        {
            foreach (var lib in libraries)
            {
                var path = GetString(lib.Value, "path");
                if (!string.IsNullOrEmpty(path))
                    libraryPaths[lib.Key] = path;
            }
        }

        var targets = doc["targets"]?.AsObject();
        if (targets is null)
            return new AssetsInfo { Packages = [], FrameworkReferences = frameworkReferences.ToList() };

        // 4. Walk every target (one per TFM, plus RID-specific ones) and merge per package.
        var merged = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        // dependency name → packages/projects that depend on it (reverse graph, all targets)
        var dependents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // TFM → dependency name → (dependent label → highest minimum version it asks for)
        var requirements = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        // package name → TFM → resolved version
        var resolved = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var projectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetFrameworks = new List<string>();

        foreach (var target in targets)
        {
            if (target.Value is not JsonObject packages) continue;

            // "net8.0/win-x64" → "net8.0": RID-specific graphs share their TFM's requirements
            string tfm = target.Key.Split('/')[0];
            if (!targetFrameworks.Contains(tfm, StringComparer.OrdinalIgnoreCase))
                targetFrameworks.Add(tfm);
            if (!requirements.TryGetValue(tfm, out var tfmRequirements))
                requirements[tfm] = tfmRequirements = new(StringComparer.OrdinalIgnoreCase);

            foreach (var package in packages)
            {
                // package.Key is like "Newtonsoft.Json/13.0.3"
                var parts = package.Key.Split('/');
                if (parts.Length != 2) continue;

                string packageName = parts[0];
                string packageVersion = parts[1];
                string? type = GetString(package.Value, "type");
                bool isProject = string.Equals(type, "project", StringComparison.OrdinalIgnoreCase);
                if (isProject) projectNames.Add(packageName);

                if (package.Value?["dependencies"] is JsonObject deps)
                {
                    string label = isProject ? $"project {packageName}" : packageName;
                    foreach (var dep in deps)
                    {
                        if (!dependents.TryGetValue(dep.Key, out var set))
                            dependents[dep.Key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(packageName);

                        string? min = PackageVersion.LowerBound(GetString(dep.Value) ?? GetString(dep.Value, "version"));
                        if (min is null) continue;
                        if (!tfmRequirements.TryGetValue(dep.Key, out var reqs))
                            tfmRequirements[dep.Key] = reqs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!reqs.TryGetValue(label, out var existing) || PackageVersion.Compare(min, existing) > 0)
                            reqs[label] = min;
                    }
                }

                // Project references are part of the graph but are not analysed — only NuGet packages are.
                if (type is not null && !type.Equals("package", StringComparison.OrdinalIgnoreCase)) continue;

                if (!resolved.TryGetValue(packageName, out var versions))
                    resolved[packageName] = versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                versions.TryAdd(tfm, packageVersion);

                bool isDirect = directNames.Contains(packageName);
                var dllKeys = GetDllKeys(package.Value);

                // Transitive packages without assemblies can't be used from code — not needed.
                if (!isDirect && dllKeys.Count == 0) continue;

                if (!merged.TryGetValue(packageName, out var acc))
                    merged[packageName] = acc = new Accumulator(packageName, packageVersion, isDirect);

                if (dllKeys.Count > 0) acc.HasAssemblies = true;
                if (!libraryPaths.TryGetValue(package.Key, out string? libPath)) continue;

                foreach (var dllKey in dllKeys)
                {
                    // dllKey is like "lib/net10.0/Newtonsoft.Json.dll"
                    if (dllKey.EndsWith("_._", StringComparison.Ordinal)) continue;

                    string fullDllPath = Path.Combine(cacheRoot, libPath, dllKey.Replace('/', Path.DirectorySeparatorChar));
                    string cacheKey = $"{package.Key}|{dllKey}";

                    IReadOnlySet<string> dllNamespaces = _namespaceCache.GetOrAdd(cacheKey, _ => ExtractNamespaces(fullDllPath));
                    acc.Namespaces.UnionWith(dllNamespaces);
                }
            }
        }

        var result = new List<PackageNamespaceInfo>();
        foreach (var acc in merged.Values)
        {
            IReadOnlyList<string> introducedBy = acc.IsDirect
                ? []
                : FindDirectAncestors(acc.Name, directNames, projectNames, autoReferenced, dependents);

            // SDK implicit packages (e.g. NETStandard.Library) and everything that only they bring
            // in are not the project's dependencies: never report them in any category.
            if (!acc.IsDirect
                && (autoReferenced.Contains(acc.Name)
                    || (introducedBy.Count > 0 && introducedBy.All(i => i.StartsWith(SdkPrefix, StringComparison.Ordinal)))))
            {
                continue;
            }

            result.Add(new PackageNamespaceInfo
            {
                Name = acc.Name,
                Version = acc.Version,
                Namespaces = acc.Namespaces,
                IsDirect = acc.IsDirect,
                HasNoAssemblies = !acc.HasAssemblies,
                PrivateAssetsAll = acc.IsDirect && privateAssetsAll.Contains(acc.Name),
                IntroducedBy = introducedBy.Where(i => !i.StartsWith(SdkPrefix, StringComparison.Ordinal)).ToList(),
                Targets = BuildTargets(acc.Name, targetFrameworks, resolved, requirements),
            });
        }

        return new AssetsInfo
        {
            Packages = result,
            FrameworkReferences = frameworkReferences.ToList(),
            TargetFrameworks = targetFrameworks,
        };
    }

    private const string SdkPrefix = "sdk ";

    private static IReadOnlyList<TargetDependencies> BuildTargets(
        string packageName,
        List<string> targetFrameworks,
        Dictionary<string, Dictionary<string, string>> resolved,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> requirements)
    {
        if (!resolved.TryGetValue(packageName, out var versions)) return [];

        var list = new List<TargetDependencies>();
        foreach (string tfm in targetFrameworks)
        {
            if (!versions.TryGetValue(tfm, out var version)) continue;

            IReadOnlyList<DependencyRequirement> reqs =
                requirements.TryGetValue(tfm, out var tfmReqs) && tfmReqs.TryGetValue(packageName, out var byDependent)
                    ? byDependent.Select(r => new DependencyRequirement(r.Key, r.Value))
                                 .OrderBy(r => r.Dependent, StringComparer.OrdinalIgnoreCase)
                                 .ToList()
                    : [];

            list.Add(new TargetDependencies(tfm, version, reqs));
        }
        return list;
    }

    private static string? GetString(JsonNode? node, string? property = null)
    {
        var target = property is null ? node : node?[property];
        return target is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    }

    private static bool GetBool(JsonNode? node, string property) =>
        node?[property] is JsonValue v && v.TryGetValue(out bool b) && b;

    /// <summary>
    /// Walks the reverse dependency graph up to the direct package references (or project
    /// references, labelled "project X", and SDK implicit references, labelled "sdk X") that
    /// pull in <paramref name="packageName"/>.
    /// </summary>
    private static IReadOnlyList<string> FindDirectAncestors(
        string packageName,
        HashSet<string> directNames,
        HashSet<string> projectNames,
        HashSet<string> autoReferenced,
        Dictionary<string, HashSet<string>> dependents)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { packageName };
        var queue = new Queue<string>();
        queue.Enqueue(packageName);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!dependents.TryGetValue(current, out var parents)) continue;

            foreach (string parent in parents)
            {
                if (!visited.Add(parent)) continue;
                if (directNames.Contains(parent))
                    result.Add(parent);
                else if (projectNames.Contains(parent))
                    result.Add($"project {parent}");
                else if (autoReferenced.Contains(parent))
                    result.Add(SdkPrefix + parent);
                else
                    queue.Enqueue(parent);
            }
        }

        return result.ToList();
    }

    private static List<string> GetDllKeys(JsonNode? packageNode)
    {
        var dlls = new List<string>();

        // Prefer compile-time assemblies; fall back to runtime
        var section = packageNode?["compile"] ?? packageNode?["runtime"];
        if (section is JsonObject obj)
        {
            foreach (var item in obj)
            {
                if (item.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !item.Key.EndsWith("_._", StringComparison.Ordinal))
                    dlls.Add(item.Key);
            }
        }

        return dlls;
    }

    internal static IReadOnlySet<string> ExtractNamespaces(string dllPath)
    {
        if (!File.Exists(dllPath))
            return new HashSet<string>();

        var namespaces = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata) return namespaces;

            var metadataReader = peReader.GetMetadataReader();

            foreach (var typeHandle in metadataReader.TypeDefinitions)
            {
                var typeDef = metadataReader.GetTypeDefinition(typeHandle);

                // Only public types
                if (!typeDef.Attributes.HasFlag(System.Reflection.TypeAttributes.Public)) continue;

                var ns = metadataReader.GetString(typeDef.Namespace);
                if (!string.IsNullOrEmpty(ns))
                    namespaces.Add(ns);
            }
        }
        catch (BadImageFormatException) { /* skip malformed or native DLLs */ }
        catch (InvalidOperationException) { /* no metadata */ }
        catch (IOException) { /* skip inaccessible DLLs */ }
        catch (UnauthorizedAccessException) { /* skip inaccessible DLLs */ }

        return namespaces;
    }

    private sealed class Accumulator(string name, string version, bool isDirect)
    {
        public string Name { get; } = name;
        public string Version { get; } = version;
        public bool IsDirect { get; } = isDirect;
        public bool HasAssemblies { get; set; }
        public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);
    }
}
