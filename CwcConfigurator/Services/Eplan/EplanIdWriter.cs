using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
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
/// The source file is never changed; everything outside the touched PLC terminals is copied as it is.
/// </summary>
public static class EplanIdWriter
{
    private static readonly Regex IdSuffix = new(@"\s*-\s*ID\s*\d+\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ID per EPLAN terminal from the hardware rows (rows without ID or disabled are skipped).</summary>
    public static Dictionary<string, int> CollectIds(IEnumerable<PlcDatabase> databases)
    {
        var ids = new Dictionary<string, int>();
        foreach (var database in databases)
        {
            IEnumerable<IEplanDocumented> rows = database.DigitalInputs.Cast<IEplanDocumented>()
                .Concat(database.DigitalOutputs).Concat(database.AnalogInputs).Concat(database.RtdInputs)
                .Concat(database.Counters).Concat(database.AnalogOutputs);
            foreach (var row in rows.Where(r => r.EplanTerminalRef.Length > 0 && r.Id > 0 && r.Config != SignalConfigState.Disabled))
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

        var found = new HashSet<string>();
        var written = 0;
        var pagesChanged = 0;
        var temp = targetPath + ".tmp";

        var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        var writerSettings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None };
        using (var reader = XmlReader.Create(sourcePath, readerSettings))
        using (var writer = XmlWriter.Create(temp, writerSettings))
        {
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "O4")
                {
                    var page = (XElement)XNode.ReadFrom(reader);
                    var changed = WritePage(page, ids, found);
                    written += changed;
                    pagesChanged += changed > 0 ? 1 : 0;
                    page.WriteTo(writer);
                    continue;
                }

                CopyNode(reader, writer);
                reader.Read();
            }
        }

        File.Move(temp, targetPath, overwrite: true);
        return new EplanIdWriteResult
        {
            TerminalsWritten = written,
            PagesChanged = pagesChanged,
            NotFound = ids.Keys.Where(k => !found.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList()
        };
    }

    private static int WritePage(XElement page, IReadOnlyDictionary<string, int> ids, HashSet<string> found)
    {
        var pageFn = page.Element("P49")?.Attribute("P1100")?.Value ?? "";
        var changed = 0;
        foreach (var o in page.Descendants("O17"))
        {
            var address = o.Element("P11")?.Attribute("P20400");
            var p150 = o.Element("P150");
            if (address is null || p150 is null)
            {
                continue;
            }

            var fn = p150.Attribute("P1100")?.Value ?? pageFn;
            var tag = (p150.Attribute("P20013")?.Value ?? "") + (p150.Attribute("P20014")?.Value ?? "");
            var terminal = o.Attribute("A185")?.Value ?? "";
            var key = EplanHardwareMapper.TerminalRef(fn, tag, terminal);
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
            if (value != address.Value)
            {
                address.Value = value;
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Shallow copy of the current reader node (elements are opened/closed node by node).</summary>
    private static void CopyNode(XmlReader reader, XmlWriter writer)
    {
        switch (reader.NodeType)
        {
            case XmlNodeType.XmlDeclaration:
                writer.WriteStartDocument();
                break;
            case XmlNodeType.Element:
                var empty = reader.IsEmptyElement;
                writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                writer.WriteAttributes(reader, true);
                if (empty)
                {
                    writer.WriteEndElement();
                }

                break;
            case XmlNodeType.EndElement:
                writer.WriteFullEndElement();
                break;
            case XmlNodeType.Text:
                writer.WriteString(reader.Value);
                break;
            case XmlNodeType.CDATA:
                writer.WriteCData(reader.Value);
                break;
            case XmlNodeType.Whitespace:
            case XmlNodeType.SignificantWhitespace:
                writer.WriteWhitespace(reader.Value);
                break;
            case XmlNodeType.Comment:
                writer.WriteComment(reader.Value);
                break;
            case XmlNodeType.ProcessingInstruction:
                writer.WriteProcessingInstruction(reader.Name, reader.Value);
                break;
        }
    }
}
