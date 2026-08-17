using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace SlimDep.Analysis;

/// <summary>
/// Parses <c>.cs</c>, <c>.xaml</c>, and <c>.razor</c>/<c>.cshtml</c> source files and
/// collects all namespaces referenced via using directives, fully-qualified names,
/// XAML clr-namespace declarations, and Razor @using directives.
/// </summary>
internal static class SourceAnalyzer
{
    private static readonly Regex XamlClrNsRegex = new(
        @"clr-namespace:([^;""'\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the set of all namespace strings referenced across all source files
    /// under <paramref name="projectDirectory"/>.
    /// </summary>
    internal static HashSet<string> CollectUsedNamespaces(string projectDirectory, CancellationToken ct)
    {
        var usedNamespaces = new HashSet<string>(StringComparer.Ordinal);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        // C# source files — using directives + fully-qualified names
        foreach (var csFile in EnumerateFiles(projectDirectory, "*.cs"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string source = File.ReadAllText(csFile);
                var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
                var root = tree.GetRoot(ct);
                CollectUsingDirectives(root, usedNamespaces);
                CollectFullyQualifiedNames(root, usedNamespaces);
            }
            catch (IOException) { /* skip unreadable files */ }
        }

        // XAML files — xmlns clr-namespace declarations (WPF, MAUI, etc.)
        CollectXamlNamespaces(projectDirectory, usedNamespaces, ct);

        // Razor / CSHTML files — @using directives (Blazor, MVC, etc.)
        CollectRazorNamespaces(projectDirectory, usedNamespaces, ct);

        return usedNamespaces;
    }

    // ── Feature 1: Fully-qualified name detection ────────────────────────────

    private static void CollectFullyQualifiedNames(Microsoft.CodeAnalysis.SyntaxNode root, HashSet<string> result)
    {
        foreach (var node in root.DescendantNodes())
        {
            // QualifiedNameSyntax covers type positions:
            //   Foo.Bar.Type x;  typeof(Foo.Bar.Type);  [Foo.Attr];  : Foo.IFace
            if (node is QualifiedNameSyntax { Parent: not QualifiedNameSyntax } qualName)
            {
                string name = qualName.ToString();
                // Strip generic arguments: "Foo.Bar<T>" → "Foo.Bar"
                int genericIdx = name.IndexOf('<');
                if (genericIdx > 0) name = name[..genericIdx];
                name = name.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    result.Add(name);
                    AddParentNamespaces(name, result);
                }
            }
            // MemberAccessExpressionSyntax covers expression positions:
            //   Foo.Bar.Method();  Foo.Bar.StaticProp
            else if (node is MemberAccessExpressionSyntax { Parent: not MemberAccessExpressionSyntax } memberAccess)
            {
                string? chain = BuildIdentifierChain(memberAccess);
                // Only consider chains starting with uppercase (namespace/type naming convention)
                if (chain is not null && chain.Length > 0 && char.IsUpper(chain[0]))
                {
                    result.Add(chain);
                    AddParentNamespaces(chain, result);
                }
            }
        }
    }

    private static string? BuildIdentifierChain(MemberAccessExpressionSyntax rootAccess)
    {
        var parts = new List<string>();
        ExpressionSyntax current = rootAccess;

        while (current is MemberAccessExpressionSyntax ma)
        {
            parts.Insert(0, ma.Name.Identifier.Text);
            current = ma.Expression;
        }

        if (current is IdentifierNameSyntax id)
            parts.Insert(0, id.Identifier.Text);
        else
            return null; // Not a simple identifier chain

        return parts.Count > 1 ? string.Join(".", parts) : null;
    }

    // ── Feature 4a: XAML namespace collection ────────────────────────────────

    private static void CollectXamlNamespaces(string projectDirectory, HashSet<string> result, CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(projectDirectory, "*.xaml"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string content = File.ReadAllText(file);
                foreach (Match match in XamlClrNsRegex.Matches(content))
                {
                    string ns = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(ns))
                    {
                        result.Add(ns);
                        AddParentNamespaces(ns, result);
                    }
                }
            }
            catch (IOException) { }
        }
    }

    // ── Feature 4b: Razor / CSHTML namespace collection ──────────────────────

    private static void CollectRazorNamespaces(string projectDirectory, HashSet<string> result, CancellationToken ct)
    {
        foreach (string pattern in new[] { "*.razor", "*.cshtml" })
        {
            foreach (var file in EnumerateFiles(projectDirectory, pattern))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (string line in File.ReadLines(file))
                    {
                        string trimmed = line.TrimStart();
                        if (!trimmed.StartsWith("@using ", StringComparison.Ordinal))
                            continue;

                        string ns = trimmed["@using ".Length..].Trim().TrimEnd(';');
                        if (!string.IsNullOrEmpty(ns))
                        {
                            result.Add(ns);
                            AddParentNamespaces(ns, result);
                        }
                    }
                }
                catch (IOException) { }
            }
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static void CollectUsingDirectives(Microsoft.CodeAnalysis.SyntaxNode root, HashSet<string> result)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is UsingDirectiveSyntax usingDirective && usingDirective.Name is not null)
            {
                string ns = usingDirective.Name.ToString();
                if (!string.IsNullOrEmpty(ns))
                {
                    result.Add(ns);
                    AddParentNamespaces(ns, result);
                }
            }
        }
    }

    private static void AddParentNamespaces(string fullNamespace, HashSet<string> result)
    {
        int lastDot = fullNamespace.LastIndexOf('.');
        while (lastDot > 0)
        {
            string parent = fullNamespace[..lastDot];
            if (!result.Add(parent)) break; // already there, no need to climb further
            lastDot = parent.LastIndexOf('.');
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(static f =>
            {
                string norm = f.Replace('\\', '/');
                return !norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                    && !norm.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
            });
    }
}
