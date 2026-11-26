using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace SlimDep.Analysis;

/// <summary>
/// Parses <c>project.assets.json</c> and resolves which namespaces each
/// <c>PackageReference</c> exposes, by reading type metadata from the DLLs in
/// the local NuGet package cache. Results are cached in-process for speed.
/// </summary>
internal sealed class AssetsFileParser
{
    // Cache: "{packageId}/{version}|{dll}" → set of namespaces
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> _namespaceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the direct package references (and, when <paramref name="includeTransitive"/>
    /// is set, every transitive package too) with their exported namespace sets.
    /// All target frameworks are merged, so multi-targeted projects are fully covered.
    /// </summary>
    internal static IReadOnlyList<PackageNamespaceInfo> Parse(string assetsFilePath, bool includeTransitive = false)
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
        var frameworks = doc["project"]?["frameworks"]?.AsObject();
        if (frameworks is not null)
        {
            foreach (var fw in frameworks)
            {
                var deps = fw.Value?["dependencies"]?.AsObject();
                if (deps is null) continue;
                foreach (var dep in deps)
                {
                    if (IsAutoReferenced(dep.Value)) continue;
                    directNames.Add(dep.Key);
                }
            }
        }

        // 3. Get the library path map: "{Name/Version}" → relative path in cache
        var libraryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var libraries = doc["libraries"]?.AsObject();
        if (libraries is not null)
        {
            foreach (var lib in libraries)
            {
                var path = lib.Value?["path"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(path))
                    libraryPaths[lib.Key] = path;
            }
        }

        var targets = doc["targets"]?.AsObject();
        if (targets is null) return [];

        // 4. Walk every target (one per TFM, plus RID-specific ones) and merge namespaces
        //    per package. Previously only the first target was read, so packages used only
        //    by a secondary TFM were never analysed.
        var merged = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        // dependency name → packages that depend on it (used to explain transitive findings)
        var dependents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var projectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (target.Value is not JsonObject packages) continue;

            foreach (var package in packages)
            {
                // package.Key is like "Newtonsoft.Json/13.0.3"
                var parts = package.Key.Split('/');
                if (parts.Length != 2) continue;

                string packageName = parts[0];
                string packageVersion = parts[1];

                if (package.Value?["dependencies"] is JsonObject deps)
                {
                    foreach (var dep in deps)
                    {
                        if (!dependents.TryGetValue(dep.Key, out var set))
                            dependents[dep.Key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(packageName);
                    }
                }

                // Project references are recorded in the dependency graph (so transitive findings
                // can name them) but are not analysed — only NuGet packages are.
                string? type = package.Value?["type"]?.GetValue<string>();
                if (type is not null && !type.Equals("package", StringComparison.OrdinalIgnoreCase))
                {
                    if (type.Equals("project", StringComparison.OrdinalIgnoreCase))
                        projectNames.Add(packageName);
                    continue;
                }

                bool isDirect = directNames.Contains(packageName);
                if (!isDirect && !includeTransitive) continue;

                // Get DLLs from compile or runtime section
                var dllKeys = GetDllKeys(package.Value);
                if (dllKeys.Count == 0) continue;

                if (!merged.TryGetValue(packageName, out var acc))
                    merged[packageName] = acc = new Accumulator(packageName, packageVersion, isDirect);

                // Resolve library path
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

        return merged.Values
            .Select(acc => new PackageNamespaceInfo
            {
                Name = acc.Name,
                Version = acc.Version,
                Namespaces = acc.Namespaces,
                IsDirect = acc.IsDirect,
                IntroducedBy = acc.IsDirect ? [] : FindDirectAncestors(acc.Name, directNames, projectNames, dependents),
            })
            .ToList();
    }

    private static bool IsAutoReferenced(JsonNode? dependency)
    {
        try
        {
            return dependency?["autoReferenced"]?.GetValue<bool>() == true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walks the reverse dependency graph up to the direct package references (or project
    /// references, labelled "project X") that pull in <paramref name="packageName"/>.
    /// </summary>
    private static IReadOnlyList<string> FindDirectAncestors(
        string packageName,
        HashSet<string> directNames,
        HashSet<string> projectNames,
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
                if (item.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    dlls.Add(item.Key);
            }
        }

        return dlls;
    }

    private static IReadOnlySet<string> ExtractNamespaces(string dllPath)
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
        catch (BadImageFormatException) { /* skip malformed DLLs */ }
        catch (IOException) { /* skip inaccessible DLLs */ }

        return namespaces;
    }

    private sealed class Accumulator(string name, string version, bool isDirect)
    {
        public string Name { get; } = name;
        public string Version { get; } = version;
        public bool IsDirect { get; } = isDirect;
        public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);
    }
}
