using Microsoft.CodeAnalysis;
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

    /// <summary>
    /// A dotted name that might involve a namespace but is just as likely a variable or member
    /// access (<c>log4net.LogManager</c> vs <c>order.Customer.Name</c>). It only feeds
    /// <see cref="All"/>, so it can make a package look used but never triggers SLIMDEP002.
    /// </summary>
    public void AddPossibleName(string name)
    {
        name = Normalize(name);
        if (name.Length == 0) return;
        AddWithParents(name);
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
/// Parses the project's <c>.cs</c>, <c>.xaml</c>/<c>.axaml</c> and <c>.razor</c>/<c>.cshtml</c>
/// files and collects the namespaces they reference: using directives, fully-qualified names,
/// XAML <c>clr-namespace:</c>/<c>using:</c> declarations, and Razor directives and code blocks.
/// </summary>
internal static class SourceAnalyzer
{
    // xmlns:x="clr-namespace:Foo.Bar;assembly=…" (WPF, MAUI, Avalonia) and xmlns:x="using:Foo.Bar" (UWP, WinUI, Avalonia)
    private static readonly Regex XamlNamespaceRegex = new(
        @"xmlns(?::[\w.-]+)?\s*=\s*[""'](?:clr-namespace|using):([^;""'\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Razor directives whose argument is C# (types, attributes, constraints)
    private static readonly string[] RazorCSharpDirectives =
        ["@inject ", "@model ", "@inherits ", "@implements ", "@attribute ", "@typeparam ", "@layout "];

    // @code { … } and @functions { … } blocks
    private static readonly Regex RazorCodeBlockStart = new(@"@(?:code|functions)\s*\{", RegexOptions.Compiled);

    // Inline expressions in markup: @Foo.Bar.Baz(…) or @(Foo.Bar.Baz) — not e-mail addresses (x@y.com)
    private static readonly Regex RazorInlineExpression = new(
        @"(?<![\w@])@\(?([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)", RegexOptions.Compiled);

    /// <summary>
    /// Returns the set of all namespace strings (including parent namespaces) referenced across
    /// all source files under <paramref name="projectDirectory"/>.
    /// </summary>
    internal static HashSet<string> CollectUsedNamespaces(string projectDirectory, CancellationToken ct) =>
        Collect(projectDirectory, ct).All;

    /// <summary>
    /// Collects referenced namespaces in both the generous and the explicit view, from the
    /// project folder plus any <paramref name="additionalCSharpFiles"/> it compiles from elsewhere.
    /// </summary>
    internal static NamespaceSet Collect(string projectDirectory, CancellationToken ct, IEnumerable<string>? additionalCSharpFiles = null)
    {
        var result = new NamespaceSet();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // C# source files — using directives + fully-qualified names
        foreach (var csFile in EnumerateProjectFiles(projectDirectory, ".cs").Concat(additionalCSharpFiles ?? []))
        {
            ct.ThrowIfCancellationRequested();
            if (seen.Add(Path.GetFullPath(csFile)))
                CollectCSharpFile(csFile, parseOptions, result, ct);
        }

        // XAML files — xmlns clr-namespace:/using: declarations (WPF, MAUI, UWP, WinUI, Avalonia)
        foreach (var file in EnumerateProjectFiles(projectDirectory, ".xaml", ".axaml"))
        {
            ct.ThrowIfCancellationRequested();
            CollectXamlFile(file, result);
        }

        // Razor / CSHTML files — directives, @code blocks, inline expressions (Blazor, MVC, Razor Pages)
        foreach (var file in EnumerateProjectFiles(projectDirectory, ".razor", ".cshtml"))
        {
            ct.ThrowIfCancellationRequested();
            CollectRazorFile(file, parseOptions, result, ct);
        }

        return result;
    }

    private static void CollectCSharpFile(string path, CSharpParseOptions parseOptions, NamespaceSet result, CancellationToken ct)
    {
        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // skip unreadable files
        }

        var root = CSharpSyntaxTree.ParseText(source, parseOptions).GetRoot(ct);
        CollectUsingDirectives(root, result);
        CollectFullyQualifiedNames(root, result);

        // Code inside #if blocks (e.g. #if NET48 in multi-targeted projects) is parsed
        // as disabled text when no symbols are defined. Re-parse so every branch is
        // active in at least one pass:
        //  • each symbol on its own — takes each branch of '#if A … #elif B …', since
        //    defining both would only ever select the first branch;
        //  • all symbols together — takes branches like '#if A && B'.
        // The no-symbol parse above already covers '#else' and '#if !A'.
        foreach (var symbolSet in ConditionalSymbolSets(root))
        {
            ct.ThrowIfCancellationRequested();
            var altRoot = CSharpSyntaxTree
                .ParseText(source, parseOptions.WithPreprocessorSymbols(symbolSet))
                .GetRoot(ct);
            CollectUsingDirectives(altRoot, result);
            CollectFullyQualifiedNames(altRoot, result);
        }
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
            //   Foo.Bar.Method();  Foo.Bar.StaticProp;  global::Foo.Bar.Method()
            else if (node is MemberAccessExpressionSyntax { Parent: not MemberAccessExpressionSyntax } memberAccess)
            {
                var (chain, isGlobal) = BuildIdentifierChain(memberAccess);
                if (chain is null) continue;

                // A global:: chain or one starting upper-case (namespace/type convention) is treated
                // as a qualified name. Anything else (log4net.LogManager… but also order.Customer…)
                // may still be a namespace, so it counts towards "used" without feeding SLIMDEP002.
                if (isGlobal || char.IsUpper(chain[0]))
                    result.AddQualifiedName(chain);
                else
                    result.AddPossibleName(chain);
            }
        }
    }

    private static (string? Chain, bool IsGlobal) BuildIdentifierChain(MemberAccessExpressionSyntax rootAccess)
    {
        var parts = new List<string>();
        ExpressionSyntax current = rootAccess;

        while (current is MemberAccessExpressionSyntax ma)
        {
            parts.Insert(0, ma.Name.Identifier.Text);
            current = ma.Expression;
        }

        bool isGlobal = false;
        switch (current)
        {
            case IdentifierNameSyntax id:
                parts.Insert(0, id.Identifier.Text);
                break;
            case AliasQualifiedNameSyntax alias:   // global::Foo.Bar.Method()
                parts.Insert(0, alias.Name.Identifier.Text);
                isGlobal = alias.Alias.Identifier.IsKind(SyntaxKind.GlobalKeyword);
                break;
            default:
                return (null, false); // Not a simple identifier chain
        }

        return parts.Count > 1 ? (string.Join(".", parts), isGlobal) : (null, false);
    }

    // ── XAML namespace collection ─────────────────────────────────────────────

    private static void CollectXamlFile(string file, NamespaceSet result)
    {
        try
        {
            string content = File.ReadAllText(file);
            foreach (Match match in XamlNamespaceRegex.Matches(content))
                result.AddImport(match.Groups[1].Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ── Razor / CSHTML namespace collection ───────────────────────────────────

    private static void CollectRazorFile(string file, CSharpParseOptions parseOptions, NamespaceSet result, CancellationToken ct)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Directive arguments and @code blocks are C#; collect them into a synthetic file and run
        // the normal qualified-name pass over it (Roslyn tolerates the incomplete code).
        var directives = new System.Text.StringBuilder();
        var codeBlocks = new System.Text.StringBuilder();

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("@using ", StringComparison.Ordinal))
            {
                string rest = trimmed["@using ".Length..].Trim().TrimEnd(';').Trim();
                int eq = rest.IndexOf('=');
                if (rest.StartsWith("static ", StringComparison.Ordinal))
                    result.AddQualifiedName(rest["static ".Length..]);       // @using static Foo.Bar.Type
                else if (eq > 0)
                    result.AddQualifiedName(rest[(eq + 1)..]);              // @using Alias = Foo.Bar.Type
                else
                    result.AddImport(rest);
                continue;
            }

            if (trimmed.StartsWith("@addTagHelper ", StringComparison.Ordinal))
            {
                // @addTagHelper *, Some.Assembly — the assembly name usually matches its root namespace
                int comma = trimmed.LastIndexOf(',');
                if (comma > 0)
                    result.AddPossibleName(trimmed[(comma + 1)..].Trim().Trim('"'));
                continue;
            }

            string? directive = RazorCSharpDirectives.FirstOrDefault(d => trimmed.StartsWith(d, StringComparison.Ordinal));
            if (directive is not null)
                directives.Append(trimmed[directive.Length..].Trim()).AppendLine(";");
        }

        foreach (Match m in RazorCodeBlockStart.Matches(text))
        {
            int open = m.Index + m.Length - 1;
            int close = FindMatchingBrace(text, open);
            codeBlocks.AppendLine(text[(open + 1)..close]);
        }

        foreach (Match m in RazorInlineExpression.Matches(text))
            result.AddPossibleName(m.Groups[1].Value);

        string synthetic =
            "class __RazorDirectives { void __M() {\n" + directives + "\n} }\n" +
            "class __RazorCode {\n" + codeBlocks + "\n}\n";
        var root = CSharpSyntaxTree.ParseText(synthetic, parseOptions).GetRoot(ct);
        CollectFullyQualifiedNames(root, result);
    }

    /// <summary>Index of the '}' matching the '{' at <paramref name="open"/>, skipping string and char literals.</summary>
    private static int FindMatchingBrace(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                // skip a literal, honouring backslash escapes
                for (i++; i < text.Length && text[i] != c; i++)
                    if (text[i] == '\\') i++;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return text.Length; // unterminated: take the rest of the file
    }

    // ── Project file enumeration ──────────────────────────────────────────────

    /// <summary>
    /// Enumerates files with the given extensions that the SDK's default globs would include:
    /// everything under the project folder except its own <c>bin/</c> and <c>obj/</c>, folders
    /// starting with '.', and folders containing another project (a nested project owns those
    /// files). Paths above the project folder play no part, so a project located under some
    /// folder named "bin" is scanned normally. Inaccessible folders are skipped.
    /// </summary>
    internal static IEnumerable<string> EnumerateProjectFiles(string projectDirectory, params string[] extensions)
    {
        var fileOptions = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var dirOptions = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        var pending = new Stack<string>();
        pending.Push(projectDirectory);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();

            foreach (string file in SafeEnumerate(() => Directory.EnumerateFiles(dir, "*", fileOptions)))
            {
                if (extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    yield return file;
            }

            foreach (string sub in SafeEnumerate(() => Directory.EnumerateDirectories(dir, "*", dirOptions)))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith('.')) continue;
                if (dir == projectDirectory
                    && (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (ContainsProjectFile(sub)) continue;

                pending.Push(sub);
            }
        }
    }

    private static bool ContainsProjectFile(string dir) =>
        SafeEnumerate(() => Directory.EnumerateFiles(dir, "*.*proj", new EnumerationOptions { IgnoreInaccessible = true }))
            .Any(f => f.EndsWith("proj", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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

    // Bounds the extra parses for files with unusually many distinct symbols.
    private const int MaxIndividualSymbolParses = 32;

    private static IEnumerable<string[]> ConditionalSymbolSets(Microsoft.CodeAnalysis.SyntaxNode root)
    {
        var symbols = CollectConditionalSymbols(root);
        if (symbols.Count == 0) yield break;

        foreach (string symbol in symbols.Take(MaxIndividualSymbolParses))
            yield return [symbol];

        if (symbols.Count > 1)
            yield return symbols.ToArray();
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
}
