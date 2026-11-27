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
    /// Each value is paired with the file it was set in (for <c>$(MSBuildThisFileDirectory)</c>)
    /// and whether it is conditional. If the first entry is unconditional, the property is
    /// definitely set to one of the listed values; otherwise it may also keep its default.
    /// </summary>
    internal static List<(string Value, MsBuildFile File, bool IsConditional)> PropertyValues(IEnumerable<MsBuildFile> files, string propertyName)
    {
        var values = new List<(string, MsBuildFile, bool)>();
        foreach (var file in files)
        {
            foreach (var prop in file.Document.Descendants().Where(e => e.Name.LocalName == propertyName
                                                                       && e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                bool conditional = IsConditional(prop);
                if (!conditional)
                    values.Clear();
                values.Add((prop.Value.Trim(), file, conditional));
            }
        }
        return values;
    }

    /// <summary>
    /// Converts an MSBuild path to the current OS's separators. MSBuild accepts both '\' and '/'
    /// on every platform, but on Linux/macOS '\' is an ordinary file-name character to .NET.
    /// </summary>
    internal static string NormalizeSeparators(string path) =>
        path.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar);

    /// <summary>
    /// Compares file paths the way the OS does: case-insensitively on Windows and macOS (default
    /// file systems), case-sensitively elsewhere, so 'A.cs' and 'a.cs' stay distinct on Linux.
    /// </summary>
    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The lower-cased set of <see cref="PropertyValues"/>, for boolean-ish properties.</summary>
    internal static HashSet<string> PossibleValues(IEnumerable<MsBuildFile> files, string propertyName) =>
        PropertyValues(files, propertyName).Select(v => v.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>True when the property is unconditionally set and every possible value equals <paramref name="value"/>.</summary>
    internal static bool DefinitelyEquals(IEnumerable<MsBuildFile> files, string propertyName, string value)
    {
        var values = PropertyValues(files, propertyName);
        return values.Count > 0 && !values[0].IsConditional
               && values.All(v => v.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

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
        return unresolved || expanded.Contains("$(", StringComparison.Ordinal) ? null : NormalizeSeparators(expanded);
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
