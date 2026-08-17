namespace SlimDep.Analysis;

/// <summary>
/// Loads a <c>.slimdepignore</c> file from the given directory or any parent directory
/// and returns the set of package names to exclude from unused-package analysis.
/// </summary>
/// <remarks>
/// File format: one package name per line. Lines starting with <c>#</c> are comments.
/// Empty lines are ignored. Package names are matched case-insensitively.
/// <code>
/// # Packages used via reflection or DI — suppress false positives
/// Microsoft.EntityFrameworkCore.Design
/// coverlet.collector
/// </code>
/// </remarks>
internal static class IgnoreFileParser
{
    private const string IgnoreFileName = ".slimdepignore";

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> to find a <c>.slimdepignore</c> file.
    /// Returns an empty set if no file is found.
    /// </summary>
    internal static IReadOnlySet<string> Load(string startDirectory)
    {
        string? dir = startDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, IgnoreFileName);
            if (File.Exists(candidate))
                return Parse(candidate);

            string? parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> Parse(string filePath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in File.ReadLines(filePath))
        {
            string trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('#'))
                result.Add(trimmed);
        }

        return result;
    }
}
