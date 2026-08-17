using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace SlimDep.Analysis;

/// <summary>
/// Parses <c>project.assets.json</c> and resolves which namespaces each direct
/// <c>PackageReference</c> exposes, by reading type metadata from the DLLs in
/// the local NuGet package cache. Results are cached in-process for speed.
/// </summary>
internal sealed class AssetsFileParser
{
    // Cache: "{packageId}/{version}/{dll}" → set of namespaces
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> _namespaceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a list of direct package references with their exported namespace sets.
    /// </summary>
    internal static IReadOnlyList<PackageNamespaceInfo> Parse(string assetsFilePath)
    {
        if (!File.Exists(assetsFilePath))
            throw new SlimDepException($"project.assets.json not found at '{assetsFilePath}'. Run 'dotnet restore' first.");

        using var stream = File.OpenRead(assetsFilePath);
        var doc = JsonNode.Parse(stream) ?? throw new SlimDepException("Failed to parse project.assets.json.");

        // 1. Get the NuGet package cache root
        var packageFolders = doc["packageFolders"]?.AsObject();
        string cacheRoot = packageFolders?.FirstOrDefault().Key
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        cacheRoot = cacheRoot.TrimEnd('/', '\\');

        // 2. Identify direct package references from project.frameworks.<tfm>.dependencies
        var directNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = doc["project"]?["frameworks"]?.AsObject();
        if (frameworks is not null)
        {
            foreach (var fw in frameworks)
            {
                var deps = fw.Value?["dependencies"]?.AsObject();
                if (deps is null) continue;
                foreach (var dep in deps)
                    directNames.Add(dep.Key);
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

        // 4. For each direct package, find its runtime DLLs from targets section and resolve namespaces
        var result = new List<PackageNamespaceInfo>();

        var targets = doc["targets"]?.AsObject();
        if (targets is null) return result;

        // Use the first target framework found (e.g. "net10.0")
        var targetFramework = targets.FirstOrDefault();
        var packages = targetFramework.Value?.AsObject();
        if (packages is null) return result;

        foreach (var package in packages)
        {
            // package.Key is like "Newtonsoft.Json/13.0.3"
            var parts = package.Key.Split('/');
            if (parts.Length != 2) continue;

            string packageName = parts[0];
            string packageVersion = parts[1];

            if (!directNames.Contains(packageName)) continue;

            // Get DLLs from compile or runtime section
            var dllKeys = GetDllKeys(package.Value);
            if (dllKeys.Count == 0) continue;

            // Resolve library path
            if (!libraryPaths.TryGetValue(package.Key, out string? libPath)) continue;

            var namespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dllKey in dllKeys)
            {
                // dllKey is like "lib/net10.0/Newtonsoft.Json.dll"
                if (dllKey.EndsWith("_._", StringComparison.Ordinal)) continue;

                string fullDllPath = Path.Combine(cacheRoot, libPath, dllKey.Replace('/', Path.DirectorySeparatorChar));
                string cacheKey = $"{package.Key}|{dllKey}";

                IReadOnlySet<string> dllNamespaces = _namespaceCache.GetOrAdd(cacheKey, _ => ExtractNamespaces(fullDllPath));
                foreach (var ns in dllNamespaces)
                    namespaces.Add(ns);
            }

            result.Add(new PackageNamespaceInfo
            {
                Name = packageName,
                Version = packageVersion,
                Namespaces = namespaces,
            });
        }

        return result;
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
}
