using System.Text;
using System.Text.RegularExpressions;

namespace SlimDep.Analysis;

/// <summary>
/// Locates a project's restore output and the source files it compiles from outside its folder,
/// by reading (not evaluating) its MSBuild files.
/// </summary>
internal static class ProjectLayout
{
    // ── project.assets.json ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the path of the project's <c>project.assets.json</c>. Honours
    /// <c>MSBuildProjectExtensionsPath</c>, <c>BaseIntermediateOutputPath</c> and
    /// <c>UseArtifactsOutput</c>/<c>ArtifactsPath</c> before falling back to <c>obj/</c>, so a
    /// stale <c>obj/</c> left behind is not analysed when restore writes elsewhere.
    /// </summary>
    internal static string FindAssetsFile(string projectPath)
    {
        var candidates = AssetsFileCandidates(projectPath);
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        throw new SlimDepException(
            $"project.assets.json not found (looked in: {string.Join(", ", candidates.Select(c => $"'{c}'"))}). Run 'dotnet restore' first.");
    }

    /// <summary>Possible locations of <c>project.assets.json</c>, most specific first.</summary>
    internal static List<string> AssetsFileCandidates(string projectPath)
    {
        projectPath = Path.GetFullPath(projectPath);
        string projectDir = Path.GetDirectoryName(projectPath) ?? ".";
        string projectName = Path.GetFileNameWithoutExtension(projectPath);
        var files = MsBuildFiles.LoadProjectChain(projectPath);
        var candidates = new List<string>();

        void AddDirectory(string dir) =>
            candidates.Add(Path.GetFullPath(Path.Combine(projectDir, dir, "project.assets.json")));

        // 1. Explicitly configured intermediate folders (restore writes to MSBuildProjectExtensionsPath,
        //    which defaults to BaseIntermediateOutputPath)
        foreach (string property in new[] { "MSBuildProjectExtensionsPath", "BaseIntermediateOutputPath" })
        {
            foreach (var (value, file) in MsBuildFiles.PropertyValues(files, property))
            {
                if (MsBuildFiles.ExpandProperties(value, MsBuildFiles.WellKnownProperties(projectPath, file)) is { } dir)
                    AddDirectory(dir);
            }
        }

        // 2. Artifacts output layout: <ArtifactsPath>/obj/<ProjectName>/
        if (MsBuildFiles.PossibleValues(files, "UseArtifactsOutput").Contains("true"))
        {
            var roots = new List<string>();
            foreach (var (value, file) in MsBuildFiles.PropertyValues(files, "ArtifactsPath"))
            {
                if (MsBuildFiles.ExpandProperties(value, MsBuildFiles.WellKnownProperties(projectPath, file)) is { } root)
                    roots.Add(Path.GetFullPath(Path.Combine(projectDir, root)));
            }
            if (roots.Count == 0)
            {
                // Default: an 'artifacts' folder next to Directory.Build.props, else next to the project
                string? props = MsBuildFiles.FindNearest(projectDir, "Directory.Build.props");
                roots.Add(Path.Combine(props is null ? projectDir : Path.GetDirectoryName(props)!, "artifacts"));
            }
            foreach (string root in roots)
                candidates.Add(Path.Combine(root, "obj", projectName, "project.assets.json"));
        }

        // 3. SDK default
        AddDirectory("obj");

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Source files compiled from outside the project folder ────────────────

    /// <summary>
    /// Resolves C# files the project compiles through <c>&lt;Compile Include="..."/&gt;</c> items —
    /// in the project, its <c>Directory.Build.*</c> files and imported shared-project
    /// <c>.projitems</c> files. Globs and the well-known <c>$(MSBuild…)</c> properties are
    /// supported; items with other property references can't be resolved statically and are skipped.
    /// </summary>
    internal static IReadOnlyList<string> CompiledSourceFiles(string projectPath, CancellationToken ct = default)
    {
        projectPath = Path.GetFullPath(projectPath);
        string projectDir = Path.GetDirectoryName(projectPath) ?? ".";
        var files = MsBuildFiles.LoadProjectChain(projectPath);

        // Shared projects: <Import Project="..\Shared\Shared.projitems" Label="Shared" />
        var toScan = new List<MsBuildFile>(files);
        foreach (var file in files)
        {
            foreach (var import in file.Document.Descendants().Where(e => e.Name.LocalName == "Import"))
            {
                string? project = import.Attribute("Project")?.Value;
                if (project is null || !project.Trim().EndsWith(".projitems", StringComparison.OrdinalIgnoreCase)) continue;
                if (MsBuildFiles.ExpandProperties(project.Trim(), MsBuildFiles.WellKnownProperties(projectPath, file)) is not { } expanded) continue;

                // Import paths are relative to the importing file
                if (MsBuildFiles.TryLoad(Path.GetFullPath(Path.Combine(file.Directory, expanded))) is { } projitems)
                    toScan.Add(projitems);
            }
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in toScan)
        {
            foreach (var item in file.Document.Descendants().Where(e => e.Name.LocalName == "Compile"))
            {
                foreach (string pattern in MsBuildFiles.SplitList(item.Attribute("Include")?.Value))
                {
                    ct.ThrowIfCancellationRequested();
                    if (MsBuildFiles.ExpandProperties(pattern, MsBuildFiles.WellKnownProperties(projectPath, file)) is not { } expanded) continue;

                    // Item paths are relative to the project directory, wherever the item is declared
                    foreach (string source in ExpandGlob(projectDir, expanded))
                    {
                        if (source.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            result.Add(source);
                    }
                }
            }
        }

        return result.ToList();
    }

    /// <summary>Expands an MSBuild-style path or glob (<c>*</c>, <c>?</c>, <c>**</c>) relative to <paramref name="baseDir"/>.</summary>
    internal static IEnumerable<string> ExpandGlob(string baseDir, string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            string path = Path.GetFullPath(Path.Combine(baseDir, pattern));
            if (File.Exists(path)) yield return path;
            yield break;
        }

        string[] segments = pattern.Split('/');
        int firstWild = Array.FindIndex(segments, s => s.IndexOfAny(['*', '?']) >= 0);
        string root = Path.GetFullPath(Path.Combine(baseDir, string.Join('/', segments[..firstWild])));
        if (!Directory.Exists(root)) yield break;

        var regex = GlobToRegex(string.Join('/', segments[firstWild..]));
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (regex.IsMatch(relative)) yield return file;
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                bool slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
