using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SlimDep.Cli;

/// <summary>
/// Removes <c>&lt;PackageReference&gt;</c> elements from a project file while leaving the
/// rest of the file as it was: whitespace, comments, line endings, BOM and XML declaration.
/// </summary>
internal static class CsprojEditor
{
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
        bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        string text = Encoding.UTF8.GetString(raw, hasBom ? 3 : 0, raw.Length - (hasBom ? 3 : 0));
        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);

        // Match on local name so legacy projects with the MSBuild xmlns are handled too
        var matches = doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference"
                        && e.Attribute("Include")?.Value is string include
                        && names.Contains(include))
            .ToList();

        if (matches.Count == 0) return [];

        var removed = new List<string>();
        foreach (var element in matches)
        {
            removed.Add(element.Attribute("Include")!.Value);
            XElement? parent = element.Parent;
            RemoveWithLeadingWhitespace(element);

            // Drop the <ItemGroup> only if it is now empty: no attributes (Condition, Label)
            // and nothing left inside but whitespace — comments are kept.
            if (parent is { Name.LocalName: "ItemGroup", HasAttributes: false }
                && parent.Nodes().All(n => n is XText t && string.IsNullOrWhiteSpace(t.Value)))
            {
                RemoveWithLeadingWhitespace(parent);
            }
        }

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = doc.Declaration is null,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = newLine,
            Indent = false,
        };

        using (var stream = new MemoryStream())
        {
            using (var writer = XmlWriter.Create(stream, settings))
                doc.Save(writer);

            // XmlWriter drops whitespace outside the root element; keep a trailing newline if there was one
            if (text.EndsWith('\n'))
            {
                stream.Seek(0, SeekOrigin.End);
                byte[] nl = Encoding.UTF8.GetBytes(newLine);
                if (!EndsWith(stream, nl))
                    stream.Write(nl);
            }

            File.WriteAllBytes(csprojPath, stream.ToArray());
        }

        return removed;
    }

    private static void RemoveWithLeadingWhitespace(XNode node)
    {
        if (node.PreviousNode is XText ws && string.IsNullOrWhiteSpace(ws.Value))
            ws.Remove();
        node.Remove();
    }

    private static bool EndsWith(MemoryStream stream, byte[] suffix)
    {
        if (stream.Length < suffix.Length) return false;
        byte[] buffer = stream.GetBuffer();
        long start = stream.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
            if (buffer[start + i] != suffix[i]) return false;
        return true;
    }
}
