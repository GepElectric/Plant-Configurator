using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Eplan;

public sealed class EplanIdWriteResult
{
    public int TerminalsWritten { get; init; }
    public int PagesChanged { get; init; }
    /// <summary>Hardware rows with an ID whose EPLAN terminal was not found in the .epj.</summary>
    public IReadOnlyList<string> NotFound { get; init; } = [];
}

/// <summary>
/// Writes the IDs of the PLC program back into a copy of the EPLAN PXF export: the PLC address of every mapped
/// terminal (schematic and PLC overview) becomes "I0.0 - ID 123", so whoever opens the schematics sees the ID.
/// </summary>
/// <remarks>
/// The copy is made by splicing text: only the value of the PLC address attribute (P20400) of the mapped terminals
/// changes, every other byte stays exactly as EPLAN wrote it. Re-serialising the XML is not an option – EPLAN keeps
/// line breaks and tabs of multi-line texts (cross-reference pointers, forms) as character references
/// (&amp;#10; &amp;#9;) inside attributes, and an XML writer would turn them into plain characters that EPLAN then
/// reads as spaces.
/// </remarks>
public static class EplanIdWriter
{
    private static readonly Regex IdSuffix = new(@"\s*-\s*ID\s*\d+\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageStart = new(@"<O4[\s>]", RegexOptions.Compiled);

    /// <summary>
    /// ID per EPLAN terminal from the hardware rows. A row counts when it is not disabled – ID 0 is a real signal
    /// (default rows are disabled with ID 0).
    /// </summary>
    public static Dictionary<string, int> CollectIds(IEnumerable<PlcDatabase> databases)
    {
        var ids = new Dictionary<string, int>();
        foreach (var database in databases)
        {
            IEnumerable<IEplanDocumented> rows = database.DigitalInputs.Cast<IEplanDocumented>()
                .Concat(database.DigitalOutputs).Concat(database.AnalogInputs).Concat(database.RtdInputs)
                .Concat(database.Counters).Concat(database.AnalogOutputs);
            foreach (var row in rows.Where(r => r.EplanTerminalRef.Length > 0 && r.Id >= 0 && r.Config != SignalConfigState.Disabled))
            {
                ids[row.EplanTerminalRef] = row.Id;
            }
        }

        return ids;
    }

    /// <summary>"I0.0" + 123 → "I0.0 - ID 123"; an older ID suffix is replaced, an empty address becomes "ID 123".</summary>
    public static string WithId(string address, int id)
    {
        var plain = IdSuffix.Replace(address ?? "", "").Trim();
        return plain.Length == 0 ? $"ID {id}" : $"{plain} - ID {id}";
    }

    public static EplanIdWriteResult Write(string sourcePath, string targetPath, IReadOnlyDictionary<string, int> ids)
    {
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The EPLAN file is never overwritten – choose another target file.");
        }

        var bytes = File.ReadAllBytes(sourcePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var found = new HashSet<string>();
        var output = new StringBuilder(text.Length + ids.Count * 16);
        var written = 0;
        var pagesChanged = 0;
        var copied = 0;
        var position = 0;
        while (true)
        {
            var match = PageStart.Match(text, position);
            if (!match.Success)
            {
                break;
            }

            var pageEnd = text.IndexOf("</O4>", match.Index, StringComparison.Ordinal);
            if (pageEnd < 0)
            {
                break;
            }

            pageEnd += "</O4>".Length;
            var edits = PageEdits(text, match.Index, pageEnd, ids, found);
            if (edits.Count > 0)
            {
                pagesChanged++;
                foreach (var (valueStart, valueEnd, value) in edits.OrderBy(e => e.Start))
                {
                    output.Append(text, copied, valueStart - copied).Append(value);
                    copied = valueEnd;
                    written++;
                }
            }

            position = pageEnd;
        }

        output.Append(text, copied, text.Length - copied);
        var temp = targetPath + ".tmp";
        using (var stream = File.Create(temp))
        {
            if (hasBom)
            {
                stream.Write([0xEF, 0xBB, 0xBF]);
            }

            var encoded = new UTF8Encoding(false).GetBytes(output.ToString());
            stream.Write(encoded);
        }

        File.Move(temp, targetPath, overwrite: true);
        return new EplanIdWriteResult
        {
            TerminalsWritten = written,
            PagesChanged = pagesChanged,
            NotFound = ids.Keys.Where(k => !found.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList()
        };
    }

    /// <summary>Positions (in the whole text) of the P20400 values to replace on one page, with their new escaped value.</summary>
    private static List<(int Start, int End, string Value)> PageEdits(string text, int pageStart, int pageEnd,
        IReadOnlyDictionary<string, int> ids, HashSet<string> found)
    {
        var edits = new List<(int, int, string)>();
        var pageText = text.Substring(pageStart, pageEnd - pageStart);
        if (!pageText.Contains("P20400=", StringComparison.Ordinal))
        {
            return edits;
        }

        var page = XElement.Parse(pageText);
        var pageFn = page.Element("P49")?.Attribute("P1100")?.Value ?? "";
        foreach (var o in page.Descendants("O17"))
        {
            var address = o.Element("P11")?.Attribute("P20400");
            var p150 = o.Element("P150");
            var a1 = o.Attribute("A1")?.Value;
            if (address is null || p150 is null || a1 is null)
            {
                continue;
            }

            var fn = p150.Attribute("P1100")?.Value ?? pageFn;
            var tag = (p150.Attribute("P20013")?.Value ?? "") + (p150.Attribute("P20014")?.Value ?? "");
            var key = EplanHardwareMapper.TerminalRef(fn, tag, o.Attribute("A185")?.Value ?? "");
            if (!ids.TryGetValue(key, out var id))
            {
                key = EplanHardwareMapper.TerminalRef(fn, tag, "*");     // counter: one ID for the module
                if (!ids.TryGetValue(key, out id))
                {
                    continue;
                }
            }

            found.Add(key);
            var value = WithId(address.Value, id);
            if (value == address.Value)
            {
                continue;
            }

            // locate this function's P20400 in the raw text: after its A1, before the next function starts
            var at = text.IndexOf($" A1=\"{a1}\"", pageStart, pageEnd - pageStart, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var limit = NextFunction(text, at, pageEnd);
            var attr = text.IndexOf(" P20400=\"", at, limit - at, StringComparison.Ordinal);
            if (attr < 0)
            {
                continue;
            }

            var valueStart = attr + " P20400=\"".Length;
            var valueEnd = text.IndexOf('"', valueStart);
            edits.Add((valueStart, valueEnd, SecurityElement.Escape(value)!));
        }

        return edits;
    }

    private static int NextFunction(string text, int from, int pageEnd)
    {
        var next = text.IndexOf("<O17 ", from + 1, pageEnd - from - 1, StringComparison.Ordinal);
        var close = text.IndexOf("</O17>", from, pageEnd - from, StringComparison.Ordinal);
        var limit = pageEnd;
        if (next >= 0) limit = Math.Min(limit, next);
        if (close >= 0) limit = Math.Min(limit, close);
        return limit;
    }
}
