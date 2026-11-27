using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SlimDep.Cli;

/// <summary>
/// Removes <c>&lt;PackageReference&gt;</c> elements from a project file without rewriting
/// anything else in it.
/// </summary>
/// <remarks>
/// The XML is parsed only to locate the elements to remove. Their exact character spans are
/// then cut from the original text, so every untouched byte — attribute quotes and layout,
/// entity spellings, comments, whitespace, line endings, encoding and BOM — is preserved.
/// </remarks>
internal static class CsprojEditor
{
    private static readonly Regex DeclaredEncoding = new(
        @"^\s*<\?xml[^>]*\bencoding\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static CsprojEditor()
    {
        // Legacy code pages such as windows-1252 that an XML declaration may name
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Removes the <c>&lt;PackageReference&gt;</c> elements whose <c>Include</c> matches one of
    /// <paramref name="packageNames"/>, and returns the names actually removed. A package
    /// declared elsewhere (e.g. <c>Directory.Build.props</c>) is not in the returned list.
    /// The file is not rewritten when nothing matches.
    /// </summary>
    internal static IReadOnlyList<string> RemovePackageReferences(string csprojPath, IEnumerable<string> packageNames)
    {
        var names = new HashSet<string>(packageNames, StringComparer.OrdinalIgnoreCase);

        byte[] raw = File.ReadAllBytes(csprojPath);
        var (encoding, preambleLength) = DetectEncoding(raw);
        string text = encoding.GetString(raw, preambleLength, raw.Length - preambleLength);

        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);

        // Match on local name so legacy projects with the MSBuild xmlns are handled too
        var matches = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference"
                        && e.Attribute("Include")?.Value is string include
                        && names.Contains(include))
            .ToList();

        if (matches.Count == 0) return [];

        // Decide what to cut: each matching element, or its whole <ItemGroup> when nothing else
        // would remain in it (no attributes such as Condition/Label, no comments, no other items).
        var matchSet = new HashSet<XElement>(matches);
        var toCut = new List<XElement>();
        foreach (var element in matches)
        {
            XElement? group = element.Parent;
            bool removeGroup = group is { Name.LocalName: "ItemGroup", HasAttributes: false }
                && group.Nodes().All(n => n is XElement e ? matchSet.Contains(e)
                                         : n is XText t && string.IsNullOrWhiteSpace(t.Value));

            XElement target = removeGroup ? group! : element;
            if (!toCut.Contains(target)) toCut.Add(target);
        }

        var lineStarts = ComputeLineStarts(text);
        var spans = toCut
            .Select(e => ExpandToLines(text, ElementSpan(text, lineStarts, e)))
            .OrderBy(s => s.Start)
            .ToList();

        var sb = new StringBuilder(text.Length);
        int pos = 0;
        foreach (var (start, end) in spans)
        {
            if (start < pos) continue; // nested in a span already cut
            sb.Append(text, pos, start - pos);
            pos = end;
        }
        sb.Append(text, pos, text.Length - pos);

        byte[] preamble = raw.AsSpan(0, preambleLength).ToArray();
        byte[] body = encoding.GetBytes(sb.ToString());
        using (var stream = File.Create(csprojPath))
        {
            stream.Write(preamble);
            stream.Write(body);
        }

        return matches.Select(e => e.Attribute("Include")!.Value).ToList();
    }

    // ── Encoding ──────────────────────────────────────────────────────────────

    /// <summary>Detects the encoding from the BOM, else from the XML declaration, else UTF-8.</summary>
    internal static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return (new UTF8Encoding(false), 3);
        if (raw.Length >= 4 && raw[0] == 0xFF && raw[1] == 0xFE && raw[2] == 0 && raw[3] == 0)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4);
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);

        // No BOM: the declaration is ASCII-compatible in every encoding XML allows without one
        string head = Encoding.ASCII.GetString(raw, 0, Math.Min(raw.Length, 256));
        var m = DeclaredEncoding.Match(head);
        if (m.Success)
        {
            try
            {
                var declared = Encoding.GetEncoding(m.Groups[1].Value);
                // A declared UTF-8 must not gain a BOM on write
                return (declared.CodePage == Encoding.UTF8.CodePage ? new UTF8Encoding(false) : declared, 0);
            }
            catch (ArgumentException) { /* unknown name — fall back to UTF-8 */ }
        }

        return (new UTF8Encoding(false), 0);
    }

    // ── Locating spans in the original text ───────────────────────────────────

    /// <summary>Offsets at which each line starts; \r\n, \r and \n all end a line, as in XML line counting.</summary>
    private static List<int> ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts;
    }

    /// <summary>Returns [start, end) of <paramref name="element"/> in the original text.</summary>
    private static (int Start, int End) ElementSpan(string text, List<int> lineStarts, XElement element)
    {
        var info = (IXmlLineInfo)element;
        if (!info.HasLineInfo())
            throw new XmlException("Missing line information for project element.");

        // Line info points at the element name; step back to its '<'
        int offset = lineStarts[info.LineNumber - 1] + info.LinePosition - 1;
        int start = Math.Min(offset, text.Length - 1);
        while (start > 0 && text[start] != '<') start--;

        return (start, FindElementEnd(text, start));
    }

    /// <summary>
    /// Given the offset of an element's '&lt;', returns the offset just past its end
    /// (self-closing tag or matching end tag), skipping comments, CDATA, PIs and quoted values.
    /// </summary>
    internal static int FindElementEnd(string text, int start)
    {
        int depth = 0;
        int i = start;
        while (i < text.Length)
        {
            if (text[i] != '<') { i++; continue; }

            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                i = IndexAfter(text, "-->", i + 4);
                continue;
            }
            if (string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                i = IndexAfter(text, "]]>", i + 9);
                continue;
            }
            if (i + 1 < text.Length && (text[i + 1] == '?' || text[i + 1] == '!'))
            {
                i = IndexAfter(text, ">", i + 2);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '/')
            {
                // End tag
                i = IndexAfter(text, ">", i + 2);
                depth--;
                if (depth == 0) return i;
                continue;
            }

            // Start tag: find its closing '>' outside quoted attribute values
            int j = i + 1;
            char quote = '\0';
            for (; j < text.Length; j++)
            {
                char c = text[j];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '"' or '\'') quote = c;
                else if (c == '>') break;
            }
            bool selfClosing = j > 0 && j < text.Length && text[j - 1] == '/';
            i = Math.Min(j + 1, text.Length);
            if (!selfClosing) depth++;
            if (depth == 0) return i;
        }
        throw new XmlException("Unterminated element in project file.");
    }

    private static int IndexAfter(string text, string token, int from)
    {
        int idx = text.IndexOf(token, from, StringComparison.Ordinal);
        if (idx < 0) throw new XmlException("Unterminated markup in project file.");
        return idx + token.Length;
    }

    /// <summary>
    /// Widens a span so removal leaves tidy text: if the element sits alone on its line(s), the
    /// whole lines go (including the line break); a blank line left dangling directly before a
    /// closing tag or another blank line goes too. Otherwise only the element and the spaces
    /// before it on the same line are removed.
    /// </summary>
    private static (int Start, int End) ExpandToLines(string text, (int Start, int End) span)
    {
        var (start, end) = span;

        int lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t') lineStart--;
        bool aloneBefore = lineStart == 0 || text[lineStart - 1] is '\n' or '\r';

        int lineEnd = end;
        while (lineEnd < text.Length && text[lineEnd] is ' ' or '\t') lineEnd++;
        bool aloneAfter = lineEnd == text.Length || text[lineEnd] is '\n' or '\r';

        if (!(aloneBefore && aloneAfter))
        {
            // Shares its line with other markup: keep the line's indentation. If the element
            // starts the line, drop the spaces after it; otherwise drop the spaces before it.
            return aloneBefore ? (start, lineEnd) : (lineStart, end);
        }

        // Consume the line break after the element
        if (lineEnd < text.Length && text[lineEnd] == '\r') lineEnd++;
        if (lineEnd < text.Length && text[lineEnd] == '\n') lineEnd++;

        // Drop the preceding line too if it is blank and what follows the removed lines is a
        // closing tag, another blank line or the end of the file — otherwise a stray blank line
        // would be left behind (e.g. before </Project>).
        if (lineStart > 0)
        {
            int breakStart = lineStart;                          // previous line's line break
            if (text[breakStart - 1] == '\n') breakStart--;
            if (breakStart > 0 && text[breakStart - 1] == '\r') breakStart--;

            int prevStart = breakStart;                           // previous line's first char
            while (prevStart > 0 && text[prevStart - 1] is ' ' or '\t') prevStart--;
            bool prevIsBlank = prevStart > 0 && text[prevStart - 1] is '\n' or '\r';

            int next = lineEnd;
            while (next < text.Length && text[next] is ' ' or '\t') next++;
            bool nextIsCloseOrBlank = next >= text.Length
                || text[next] is '\n' or '\r'
                || string.CompareOrdinal(text, next, "</", 0, 2) == 0;

            if (prevIsBlank && nextIsCloseOrBlank)
                lineStart = prevStart;
        }

        return (lineStart, lineEnd);
    }
}
