using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SlimDep.Analysis;

/// <summary>An MSBuild file that was loaded for static inspection.</summary>
internal sealed record MsBuildFile(string Path, XDocument Document)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? ".";
}

/// <summary>
/// Static (non-evaluating) helpers for reading MSBuild project files. Conditions are not
/// evaluated; callers choose the conservative interpretation for their purpose.
/// </summary>
internal static class MsBuildFiles
{
    private static readonly Regex PropertyReference = new(@"\$\(([A-Za-z_][\w.]*)\)", RegexOptions.Compiled);

    /// <summary>
    /// Loads the files MSBuild evaluates for a project, in evaluation order: the nearest
    /// <c>Directory.Build.props</c>, the project file itself, the nearest <c>Directory.Build.targets</c>.
    /// Files that are missing or unreadable are skipped.
    /// </summary>
    internal static List<MsBuildFile> LoadProjectChain(string projectPath)
    {
        string projectDir = System.IO.Path.GetDirectoryName(projectPath) ?? ".";
        var files = new List<MsBuildFile>();
        AddIfLoaded(files, FindNearest(projectDir, "Directory.Build.props"));
        AddIfLoaded(files, projectPath);
        AddIfLoaded(files, FindNearest(projectDir, "Directory.Build.targets"));
        return files;
    }

    /// <summary>Walks up from <paramref name="startDir"/> and returns the first <paramref name="fileName"/> found.</summary>
    internal static string? FindNearest(string startDir, string fileName)
    {
        string? dir = startDir;
        while (dir is not null)
        {
            string candidate = System.IO.Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return candidate;

            string? parent = System.IO.Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    internal static MsBuildFile? TryLoad(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return new MsBuildFile(System.IO.Path.GetFullPath(path), XDocument.Load(path));
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddIfLoaded(List<MsBuildFile> files, string? path)
    {
        if (TryLoad(path) is { } file) files.Add(file);
    }

    /// <summary>
    /// Every value (trimmed, original case) the property may hold after evaluation, in any
    /// configuration, in evaluation order. An unconditional assignment replaces what came
    /// before; a conditional one may or may not apply, so it is added alongside.
    /// Each value is paired with the file it was set in (for <c>$(MSBuildThisFileDirectory)</c>).
    /// </summary>
    internal static List<(string Value, MsBuildFile File)> PropertyValues(IEnumerable<MsBuildFile> files, string propertyName)
    {
        var values = new List<(string, MsBuildFile)>();
        foreach (var file in files)
        {
            foreach (var prop in file.Document.Descendants().Where(e => e.Name.LocalName == propertyName
                                                                       && e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                if (!IsConditional(prop))
                    values.Clear();
                values.Add((prop.Value.Trim(), file));
            }
        }
        return values;
    }

    /// <summary>The lower-cased set of <see cref="PropertyValues"/>, for boolean-ish properties.</summary>
    internal static HashSet<string> PossibleValues(IEnumerable<MsBuildFile> files, string propertyName) =>
        PropertyValues(files, propertyName).Select(v => v.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when the element or any ancestor carries a <c>Condition</c>, or sits inside a
    /// <c>&lt;Choose&gt;</c> block — i.e. whether it applies depends on the configuration.
    /// </summary>
    internal static bool IsConditional(XElement element) =>
        element.AncestorsAndSelf().Any(e => e.Attribute("Condition") is not null || e.Name.LocalName == "Choose");

    /// <summary>
    /// Replaces <c>$(Name)</c> references using <paramref name="properties"/>. Returns <c>null</c>
    /// if any reference can't be resolved, so callers never act on a half-evaluated path.
    /// </summary>
    internal static string? ExpandProperties(string value, IReadOnlyDictionary<string, string> properties)
    {
        bool unresolved = false;
        string expanded = PropertyReference.Replace(value, m =>
        {
            if (properties.TryGetValue(m.Groups[1].Value, out var v)) return v;
            unresolved = true;
            return m.Value;
        });
        return unresolved || expanded.Contains("$(", StringComparison.Ordinal) ? null : expanded;
    }

    /// <summary>The well-known properties available for a file in a project.</summary>
    internal static Dictionary<string, string> WellKnownProperties(string projectPath, MsBuildFile? thisFile)
    {
        string projectDir = System.IO.Path.GetDirectoryName(projectPath) ?? ".";
        string thisDir = thisFile?.Directory ?? projectDir;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectDirectory"] = projectDir,
            ["MSBuildProjectName"] = System.IO.Path.GetFileNameWithoutExtension(projectPath),
            ["MSBuildProjectFile"] = System.IO.Path.GetFileName(projectPath),
            ["MSBuildThisFileDirectory"] = thisDir.EndsWith(System.IO.Path.DirectorySeparatorChar) ? thisDir : thisDir + System.IO.Path.DirectorySeparatorChar,
            ["MSBuildThisFileFullPath"] = thisFile?.Path ?? projectPath,
        };
    }

    /// <summary>Splits an MSBuild item list (<c>a;b;c</c>).</summary>
    internal static IEnumerable<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
