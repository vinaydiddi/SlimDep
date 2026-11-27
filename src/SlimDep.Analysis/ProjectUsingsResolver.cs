namespace SlimDep.Analysis;

/// <summary>
/// Resolves the global usings that MSBuild injects into a project without any
/// <c>using</c> directive in source: SDK implicit usings (<c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c>)
/// and <c>&lt;Using Include="..." /&gt;</c> items in the project file or <c>Directory.Build.*</c> files.
/// </summary>
/// <remarks>
/// The compiler sees these through a generated <c>*.GlobalUsings.g.cs</c> file in the intermediate
/// output folder, which <see cref="SourceAnalyzer"/> does not scan. That file only exists after a
/// build, so the usings are also evaluated statically from the project XML. MSBuild conditions are
/// not evaluated: conditional items count as present and conditional removals are ignored, which
/// can only make a package look used, never unused.
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
        ResolveInto(projectPath, set, intermediateDirectory: null, ct);
        return set.All;
    }

    /// <summary>
    /// Adds every namespace globally imported by the project's MSBuild files to <paramref name="result"/>.
    /// <paramref name="intermediateDirectory"/> is where generated <c>GlobalUsings.g.cs</c> files live
    /// (the folder holding <c>project.assets.json</c>); defaults to <c>obj/</c>.
    /// </summary>
    internal static void ResolveInto(string projectPath, NamespaceSet result, string? intermediateDirectory = null, CancellationToken ct = default)
    {
        string projectDir = Path.GetDirectoryName(projectPath) ?? ".";
        var files = MsBuildFiles.LoadProjectChain(projectPath);
        var project = files.FirstOrDefault(f => string.Equals(f.Path, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase));

        string? sdk = project?.Document.Root?.Attribute("Sdk")?.Value;
        // Conditions are not evaluated, so consider every value a property could take in some
        // configuration: implicit usings count as enabled if any reachable assignment enables them.
        var implicitUsings = MsBuildFiles.PossibleValues(files, "ImplicitUsings");
        bool implicitUsingsEnabled = implicitUsings.Contains("enable") || implicitUsings.Contains("true");
        bool useWindowsForms = MsBuildFiles.PossibleValues(files, "UseWindowsForms").Contains("true");

        // SDK implicit usings are not explicit — they are imported whether or not the code needs
        // them — while <Using> items are written by the user. Static/aliased items name a type.
        var namespaces = new List<(string Ns, bool Explicit, bool IsTypeName)>();

        if (implicitUsingsEnabled)
        {
            var implicitList = new List<string>(BaseImplicitUsings);
            foreach (string sdkName in MsBuildFiles.SplitList(sdk))
            {
                // Sdk may carry a version: "Microsoft.NET.Sdk.Web/8.0.0"
                string name = sdkName.Split('/')[0];
                if (SdkImplicitUsings.TryGetValue(name, out var extra))
                    implicitList.AddRange(extra);
            }
            if (useWindowsForms)
                implicitList.AddRange(WindowsFormsUsings);

            namespaces.AddRange(implicitList.Select(ns => (ns, false, false)));
        }

        // <Using Include="..."/> and <Using Remove="..."/> items, in evaluation order.
        foreach (var file in files)
        {
            foreach (var item in file.Document.Descendants().Where(e => e.Name.LocalName == "Using"))
            {
                // <Using Include="Foo.Bar.Type" Static="true"/> and <Using Include="Foo.Bar.Type" Alias="T"/>
                // name a type (like 'using static' / 'using T = ...'), not a namespace.
                bool isTypeName = string.Equals(item.Attribute("Static")?.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                                  || !string.IsNullOrWhiteSpace(item.Attribute("Alias")?.Value);

                foreach (string ns in MsBuildFiles.SplitList(item.Attribute("Include")?.Value))
                    namespaces.Add((ns, true, isTypeName));

                // A conditional removal may not apply in every configuration; applying it anyway
                // could make a needed package look unused, so only unconditional removals count.
                if (!MsBuildFiles.IsConditional(item))
                {
                    foreach (string ns in MsBuildFiles.SplitList(item.Attribute("Remove")?.Value))
                        namespaces.RemoveAll(n => n.Ns.Equals(ns, StringComparison.Ordinal));
                }
            }
        }

        foreach (var (ns, isExplicit, isTypeName) in namespaces)
        {
            if (isTypeName)
                result.AddQualifiedName(ns);
            else
                result.AddImport(ns, isExplicit);
        }

        // If the project has been built, the generated GlobalUsings file is authoritative.
        string objDir = intermediateDirectory ?? Path.Combine(projectDir, "obj");
        if (Directory.Exists(objDir))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (string generated in Directory.EnumerateFiles(objDir, "*.GlobalUsings.g.cs", options))
            {
                ct.ThrowIfCancellationRequested();
                SourceAnalyzer.CollectUsingsFromFile(generated, result, ct);
            }
        }
    }
}
