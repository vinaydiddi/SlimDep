namespace SlimDep.Analysis;

/// <summary>
/// Minimal NuGet/SemVer version handling: enough to compare resolved versions with the
/// lower bounds found in <c>project.assets.json</c> dependency entries.
/// </summary>
internal static class PackageVersion
{
    /// <summary>
    /// Extracts the minimum version from a NuGet version or range string:
    /// <c>"8.0.0"</c>, <c>"[8.0.0, )"</c>, <c>"(, 9.0)"</c> → <c>"8.0.0"</c>, <c>"8.0.0"</c>, <c>null</c>.
    /// </summary>
    internal static string? LowerBound(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        string s = range.Trim().TrimStart('[', '(').TrimEnd(']', ')');
        int comma = s.IndexOf(',');
        if (comma >= 0) s = s[..comma];
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Compares two NuGet versions. Prerelease sorts before the matching release.</summary>
    internal static int Compare(string a, string b)
    {
        var (numsA, preA) = Parse(a);
        var (numsB, preB) = Parse(b);

        for (int i = 0; i < 4; i++)
        {
            int c = numsA[i].CompareTo(numsB[i]);
            if (c != 0) return c;
        }

        if (preA is null && preB is null) return 0;
        if (preA is null) return 1;   // release > prerelease
        if (preB is null) return -1;

        string[] partsA = preA.Split('.');
        string[] partsB = preB.Split('.');
        for (int i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            bool numA = long.TryParse(partsA[i], out long na);
            bool numB = long.TryParse(partsB[i], out long nb);
            int c = numA && numB ? na.CompareTo(nb)
                  : numA ? -1
                  : numB ? 1
                  : string.Compare(partsA[i], partsB[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return partsA.Length.CompareTo(partsB.Length);
    }

    private static (long[] numbers, string? prerelease) Parse(string version)
    {
        string v = version.Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];

        string? pre = null;
        int dash = v.IndexOf('-');
        if (dash >= 0)
        {
            pre = v[(dash + 1)..];
            v = v[..dash];
        }

        var numbers = new long[4];
        string[] parts = v.Split('.');
        for (int i = 0; i < Math.Min(parts.Length, 4); i++)
            long.TryParse(parts[i], out numbers[i]);

        return (numbers, pre);
    }
}
