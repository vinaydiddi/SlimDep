using System.Xml;
using System.Xml.Linq;

namespace SlimDep.Analysis;

/// <summary>
/// Resolves the global usings that MSBuild injects into a project without any
/// <c>using</c> directive in source: SDK implicit usings (<c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c>)
/// and <c>&lt;Using Include="..." /&gt;</c> items in the project file or <c>Directory.Build.*</c> files.
/// </summary>
/// <remarks>
/// The compiler sees these through a generated <c>obj/**/*.GlobalUsings.g.cs</c> file, which
/// <see cref="SourceAnalyzer"/> skips along with the rest of <c>obj/</c>. That file only exists
/// after a build, so the usings are also evaluated statically from the project XML.
/// MSBuild conditions are not evaluated: conditional items are treated as present, which can
/// only make a package look used, never unused.
/// </remarks>
internal static class ProjectUsingsResolver
{
    private static readonly string[] BaseImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    ];

    private static readonly string[] HostingUsings =
    [
        "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
    ];

    // Additional implicit usings per SDK, on top of BaseImplicitUsings.
    private static readonly Dictionary<string, string[]> SdkImplicitUsings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.NET.Sdk.Web"] =
        [
            "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing", .. HostingUsings,
        ],
        ["Microsoft.NET.Sdk.Worker"] = HostingUsings,
        ["Microsoft.NET.Sdk.BlazorWebAssembly"] =
        [
            "System.Net.Http.Json", "Microsoft.AspNetCore.Components.WebAssembly.Hosting",
            "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.Logging",
        ],
    };

    private static readonly string[] WindowsFormsUsings = ["System.Drawing", "System.Windows.Forms"];

    /// <summary>Returns every namespace (with parents) globally imported by the project's MSBuild files.</summary>
    internal static HashSet<string> Resolve(string projectPath, CancellationToken ct = default)
    {
        var set = new NamespaceSet();
        ResolveInto(projectPath, set, ct);
        return set.All;
    }

    /// <summary>Adds every namespace globally imported by the project's MSBuild files to <paramref name="result"/>.</summary>
    internal static void ResolveInto(string projectPath, NamespaceSet result, CancellationToken ct = default)
    {
        string projectDir = Path.GetDirectoryName(projectPath) ?? ".";

        // MSBuild imports the nearest Directory.Build.props before the project body and the
        // nearest Directory.Build.targets after it; evaluate in that order so later files win.
        var files = new List<XDocument>();
        AddIfLoaded(files, FindNearest(projectDir, "Directory.Build.props"));
        XDocument? project = TryLoad(projectPath);
        AddIfLoaded(files, project);
        AddIfLoaded(files, FindNearest(projectDir, "Directory.Build.targets"));

        string? sdk = project?.Root?.Attribute("Sdk")?.Value;
        string? implicitUsings = LastPropertyValue(files, "ImplicitUsings");
        bool useWindowsForms = string.Equals(LastPropertyValue(files, "UseWindowsForms"), "true", StringComparison.OrdinalIgnoreCase);

        // (namespace, explicit): SDK implicit usings are not explicit — they are imported whether
        // or not the code needs them — while <Using> items are written by the user.
        var namespaces = new List<(string Ns, bool Explicit)>();

        if (implicitUsings is not null
            && (implicitUsings.Equals("enable", StringComparison.OrdinalIgnoreCase)
                || implicitUsings.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            var implicitList = new List<string>(BaseImplicitUsings);
            foreach (string sdkName in SplitList(sdk))
            {
                // Sdk may carry a version: "Microsoft.NET.Sdk.Web/8.0.0"
                string name = sdkName.Split('/')[0];
                if (SdkImplicitUsings.TryGetValue(name, out var extra))
                    implicitList.AddRange(extra);
            }
            if (useWindowsForms)
                implicitList.AddRange(WindowsFormsUsings);

            namespaces.AddRange(implicitList.Select(ns => (ns, false)));
        }

        // <Using Include="..."/> and <Using Remove="..."/> items, in evaluation order.
        foreach (var doc in files)
        {
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "Using"))
            {
                foreach (string ns in SplitList(item.Attribute("Include")?.Value))
                    namespaces.Add((ns, true));
                foreach (string ns in SplitList(item.Attribute("Remove")?.Value))
                    namespaces.RemoveAll(n => n.Ns.Equals(ns, StringComparison.Ordinal));
            }
        }

        foreach (var (ns, isExplicit) in namespaces)
            result.AddImport(ns, isExplicit);

        // If the project has been built, the generated GlobalUsings file is authoritative.
        string objDir = Path.Combine(projectDir, "obj");
        if (Directory.Exists(objDir))
        {
            foreach (string generated in Directory.EnumerateFiles(objDir, "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                SourceAnalyzer.CollectUsingsFromFile(generated, result, ct);
            }
        }
    }

    private static string? LastPropertyValue(List<XDocument> files, string propertyName)
    {
        string? value = null;
        foreach (var doc in files)
        {
            foreach (var prop in doc.Descendants().Where(e => e.Name.LocalName == propertyName
                                                             && e.Parent?.Name.LocalName == "PropertyGroup"))
            {
                value = prop.Value.Trim();
            }
        }
        return value;
    }

    private static IEnumerable<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static XDocument? FindNearest(string startDir, string fileName)
    {
        string? dir = startDir;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return TryLoad(candidate);

            string? parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static XDocument? TryLoad(string path)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddIfLoaded(List<XDocument> files, XDocument? doc)
    {
        if (doc is not null) files.Add(doc);
    }
}
