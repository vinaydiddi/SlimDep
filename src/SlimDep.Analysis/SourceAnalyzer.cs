using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace SlimDep.Analysis;

/// <summary>
/// The namespaces a project references, in two views.
/// </summary>
/// <remarks>
/// <see cref="All"/> is used for unused-package detection and is deliberately generous: every
/// parent of an imported namespace is included (<c>using A.B.C</c> also adds <c>A.B</c> and <c>A</c>).
/// <see cref="Explicit"/> is used for SLIMDEP002 and holds only names the code really refers to:
/// imported namespaces themselves, and every prefix of a qualified name (<c>Foo.Bar.Type</c> →
/// <c>Foo.Bar.Type</c>, <c>Foo.Bar</c>, <c>Foo</c>), since any of those could be the namespace.
/// </remarks>
internal sealed class NamespaceSet
{
    public HashSet<string> All { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Explicit { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// A namespace imported as a whole (using directive, @using, clr-namespace, &lt;Using&gt;).
    /// Pass <paramref name="isExplicit"/> = <c>false</c> for imports the SDK adds regardless of
    /// whether the code needs them (implicit usings), so they don't count as direct use.
    /// </summary>
    public void AddImport(string ns, bool isExplicit = true)
    {
        ns = Normalize(ns);
        if (ns.Length == 0) return;
        if (isExplicit) Explicit.Add(ns);
        AddWithParents(ns);
    }

    /// <summary>A dotted name whose namespace is one of its prefixes (Foo.Bar.Type, using static, aliases).</summary>
    public void AddQualifiedName(string name)
    {
        name = Normalize(name);
        if (name.Length == 0) return;
        AddWithParents(name);

        Explicit.Add(name);
        int lastDot = name.LastIndexOf('.');
        while (lastDot > 0)
        {
            name = name[..lastDot];
            Explicit.Add(name);
            lastDot = name.LastIndexOf('.');
        }
    }

    public void UnionWith(NamespaceSet other)
    {
        All.UnionWith(other.All);
        Explicit.UnionWith(other.Explicit);
    }

    private void AddWithParents(string ns)
    {
        All.Add(ns);
        int lastDot = ns.LastIndexOf('.');
        while (lastDot > 0)
        {
            string parent = ns[..lastDot];
            if (!All.Add(parent)) break; // already there, no need to climb further
            lastDot = parent.LastIndexOf('.');
        }
    }

    private static string Normalize(string ns)
    {
        ns = ns.Trim();
        if (ns.StartsWith("global::", StringComparison.Ordinal))
            ns = ns["global::".Length..];
        return ns;
    }
}

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
    /// Returns the set of all namespace strings (including parent namespaces) referenced across
    /// all source files under <paramref name="projectDirectory"/>.
    /// </summary>
    internal static HashSet<string> CollectUsedNamespaces(string projectDirectory, CancellationToken ct) =>
        Collect(projectDirectory, ct).All;

    /// <summary>Collects referenced namespaces in both the generous and the explicit view.</summary>
    internal static NamespaceSet Collect(string projectDirectory, CancellationToken ct)
    {
        var result = new NamespaceSet();
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
                CollectUsingDirectives(root, result);
                CollectFullyQualifiedNames(root, result);

                // Code inside #if blocks (e.g. #if NET48 in multi-targeted projects) is parsed
                // as disabled text when no symbols are defined. Re-parse with every symbol the
                // file tests so usings in those blocks are also counted.
                var symbols = CollectConditionalSymbols(root);
                if (symbols.Count > 0)
                {
                    var altRoot = CSharpSyntaxTree
                        .ParseText(source, parseOptions.WithPreprocessorSymbols(symbols))
                        .GetRoot(ct);
                    CollectUsingDirectives(altRoot, result);
                    CollectFullyQualifiedNames(altRoot, result);
                }
            }
            catch (IOException) { /* skip unreadable files */ }
        }

        // XAML files — xmlns clr-namespace declarations (WPF, MAUI, etc.)
        CollectXamlNamespaces(projectDirectory, result, ct);

        // Razor / CSHTML files — @using directives (Blazor, MVC, etc.)
        CollectRazorNamespaces(projectDirectory, result, ct);

        return result;
    }

    // ── Fully-qualified name detection ────────────────────────────────────────

    private static void CollectFullyQualifiedNames(Microsoft.CodeAnalysis.SyntaxNode root, NamespaceSet result)
    {
        foreach (var node in root.DescendantNodes())
        {
            // QualifiedNameSyntax covers type positions:
            //   Foo.Bar.Type x;  typeof(Foo.Bar.Type);  [Foo.Attr];  : Foo.IFace
            if (node is QualifiedNameSyntax { Parent: not QualifiedNameSyntax } qualName)
            {
                // Using directives are handled separately (an imported namespace is not a qualified type name)
                if (qualName.Parent is UsingDirectiveSyntax) continue;

                string name = qualName.ToString();
                // Strip generic arguments: "Foo.Bar<T>" → "Foo.Bar"
                int genericIdx = name.IndexOf('<');
                if (genericIdx > 0) name = name[..genericIdx];
                result.AddQualifiedName(name);
            }
            // MemberAccessExpressionSyntax covers expression positions:
            //   Foo.Bar.Method();  Foo.Bar.StaticProp
            else if (node is MemberAccessExpressionSyntax { Parent: not MemberAccessExpressionSyntax } memberAccess)
            {
                string? chain = BuildIdentifierChain(memberAccess);
                // Only consider chains starting with uppercase (namespace/type naming convention)
                if (chain is not null && chain.Length > 0 && char.IsUpper(chain[0]))
                    result.AddQualifiedName(chain);
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

    // ── XAML namespace collection ─────────────────────────────────────────────

    private static void CollectXamlNamespaces(string projectDirectory, NamespaceSet result, CancellationToken ct)
    {
        foreach (var file in EnumerateFiles(projectDirectory, "*.xaml"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string content = File.ReadAllText(file);
                foreach (Match match in XamlClrNsRegex.Matches(content))
                    result.AddImport(match.Groups[1].Value);
            }
            catch (IOException) { }
        }
    }

    // ── Razor / CSHTML namespace collection ───────────────────────────────────

    private static void CollectRazorNamespaces(string projectDirectory, NamespaceSet result, CancellationToken ct)
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

                        result.AddImport(trimmed["@using ".Length..].Trim().TrimEnd(';'));
                    }
                }
                catch (IOException) { }
            }
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a single C# file (e.g. a generated <c>GlobalUsings.g.cs</c>) and adds its
    /// using directives to <paramref name="result"/>. Generated files mix SDK implicit usings
    /// with user ones, so they are added as non-explicit imports.
    /// </summary>
    internal static void CollectUsingsFromFile(string filePath, NamespaceSet result, CancellationToken ct)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath), new CSharpParseOptions(LanguageVersion.Preview));
            foreach (var u in tree.GetRoot(ct).DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (u.Name is not null)
                    result.AddImport(u.Name.ToString(), isExplicit: false);
            }
        }
        catch (IOException) { /* skip unreadable files */ }
    }

    private static List<string> CollectConditionalSymbols(Microsoft.CodeAnalysis.SyntaxNode root)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directive in root.DescendantNodes(descendIntoTrivia: true).OfType<DirectiveTriviaSyntax>())
        {
            ExpressionSyntax? condition = directive switch
            {
                IfDirectiveTriviaSyntax ifDirective => ifDirective.Condition,
                ElifDirectiveTriviaSyntax elifDirective => elifDirective.Condition,
                _ => null,
            };
            if (condition is null) continue;

            foreach (var id in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                symbols.Add(id.Identifier.ValueText);
        }
        return symbols.ToList();
    }

    private static void CollectUsingDirectives(Microsoft.CodeAnalysis.SyntaxNode root, NamespaceSet result)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is not UsingDirectiveSyntax { Name: not null } usingDirective) continue;

            string name = usingDirective.Name.ToString();
            // 'using static Foo.Bar.Type;' and 'using X = Foo.Bar.Type;' name a type, not a namespace
            if (usingDirective.StaticKeyword != default || usingDirective.Alias is not null)
                result.AddQualifiedName(name);
            else
                result.AddImport(name);
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
