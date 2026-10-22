using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaTraceStudio;

/// <summary>
/// Report in a company Word template (e.g. the FAT report form of Geppert Electric or Prime Volt): everything of the
/// template is kept – header with logo, footer, styles, numbered headings, cover page up to the table of contents –
/// and its sample content is replaced by the report. Texts of the header, cover and the "Conclusion" table are
/// replaced as the user enters them. Landscape pages get copies of the header and footer widened to the page.
/// </summary>
public sealed class WordTemplate
{
    readonly Dictionary<string, byte[]> parts;
    readonly string documentStart, finalSection;
    readonly List<string> front;            // body elements of the cover, before the table of contents
    readonly string? conclusion;            // the template's "Conclusion" table, used for remarks
    readonly string? tocHeading;            // paragraph style of the template's contents heading
    readonly Dictionary<int, string> headingNumbering = []; // numbering set on the template's own headings (not by style)

    public string Path { get; }

    WordTemplate(string path)
    {
        Path = path;
        parts = [];
        using (var zip = ZipFile.OpenRead(path))
            foreach (var entry in zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')))
            {
                using var stream = entry.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory);
                parts[entry.FullName] = memory.ToArray();
            }
        UnbindProperties();
        string document = Text("word/document.xml");
        int body = document.IndexOf("<w:body>", StringComparison.Ordinal) + "<w:body>".Length;
        documentStart = document[..body];
        var elements = TopLevel(document[body..document.LastIndexOf("</w:body>", StringComparison.Ordinal)]);
        finalSection = elements.LastOrDefault(e => e.StartsWith("<w:sectPr", StringComparison.Ordinal)) ?? "";
        int toc = elements.FindIndex(e => e.Contains("TOC \\o", StringComparison.Ordinal) || e.Contains("Table of Contents", StringComparison.Ordinal));
        front = (toc < 0 ? [] : elements.Take(toc)).ToList();
        conclusion = elements.FirstOrDefault(e => e.StartsWith("<w:tbl>", StringComparison.Ordinal) && Regex.IsMatch(Plain(e), @"^\s*Conclusion", RegexOptions.IgnoreCase));
        // Some templates number a heading level on the paragraph instead of the style: take it from their sample headings.
        for (int level = 1; level <= 6; level++)
            if (Regex.Match(document, $@"<w:pPr>(?:(?!</w:pPr>).)*?<w:pStyle w:val=""Heading{level}"" ?/>((?:(?!</w:pPr>).)*?)</w:pPr>", RegexOptions.Singleline) is { Success: true } sample)
                headingNumbering[level] = Regex.Match(sample.Groups[1].Value, @"<w:numPr>.*?</w:numPr>", RegexOptions.Singleline).Value + Regex.Match(sample.Groups[1].Value, @"<w:ind [^>]*/>").Value;
                tocHeading = toc < 0 ? null : Regex.Match(elements[toc], "<w:pStyle w:val=\"(TOCHeading|[^\"]*TOC[^\"]*)\"").Groups[1].Value is { Length: > 0 } style ? style : null;
    }

    public static WordTemplate Load(string path)
    {
        try { return new WordTemplate(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        { throw new InvalidDataException($"Predložak {System.IO.Path.GetFileName(path)} nije moguće pročitati: {ex.Message}"); }
    }

    /// <summary>
    /// Fields of the header and cover bound to document properties (title, subject, author …) would be refilled
    /// by Word from the properties when opening. They get the text Word shows (the property value) and lose the
    /// binding, so what the user sees in the template is what can be replaced.
    /// </summary>
    void UnbindProperties()
    {
        string core = Text("docProps/core.xml");
        var properties = Regex.Matches(core, @"<(?:\w+:)?(\w+)(?: [^>]*)?>([^<]*)</(?:\w+:)?\1>").ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value), StringComparer.OrdinalIgnoreCase);
        foreach (var key in parts.Keys.Where(k => Regex.IsMatch(k, @"^word/(document|header\d*|footer\d*)\.xml$")).ToList())
        {
            string xml = Text(key);
            string updated = Regex.Replace(xml, @"<w:sdt>(?:(?!<w:sdt>).)*?<w:dataBinding [^>]*w:xpath=""[^""]*?(\w+)\[1\]""[^>]*/>(?:(?!<w:sdt>).)*?</w:sdt>", m =>
            {
                string sdt = Regex.Replace(m.Value, @"<w:dataBinding [^>]*/>", "");
                if (!properties.TryGetValue(m.Groups[1].Value, out var value) || value.Length == 0) return sdt;
                int i = 0;
                return Regex.Replace(sdt, @"(<w:t(?: [^>]*)?>)[^<]*(</w:t>)", t => i++ == 0 ? $"{t.Groups[1].Value.Replace("<w:t>", "<w:t xml:space=\"preserve\">")}{WordReport.X(value)}{t.Groups[2].Value}" : t.Groups[1].Value + t.Groups[2].Value);
            }, RegexOptions.Singleline);
            if (updated != xml) parts[key] = Encoding.UTF8.GetBytes(updated);
        }
    }

    string Text(string part) => parts.TryGetValue(part, out var bytes) ? Encoding.UTF8.GetString(bytes).TrimStart('﻿') : "";

    /// <summary>
    /// Texts the user may change before the export: every paragraph of the headers and footers, of the cover and
    /// of the Conclusion table's name rows (paragraphs with page fields are left alone).
    /// </summary>
    public List<string> EditableTexts()
    {
        var sources = parts.Keys.Where(k => Regex.IsMatch(k, @"^word/(header|footer)\d*\.xml$")).OrderBy(k => k).Select(Text)
            .Concat(front);
        return sources.SelectMany(Paragraphs).Where(p => !p.Contains("fldChar") && !p.Contains("instrText") && !p.Contains("fldSimple"))
            .Select(p => Plain(p).Trim()).Where(t => t.Length > 0).Distinct().ToList();
    }

    /// <summary>A document field found in the template and where it is written (paragraph text, label kept before the value).</summary>
    public sealed record Field(string Name, List<(string Key, string Prefix, bool Upper)> Places)
    {
        public string Current => string.Join(" · ", Places.Select(p => p.Key[p.Prefix.Length..]).Distinct());
    }

    /// <summary>Document fields every report must fill in, recognised by the labels of the form.</summary>
    public static readonly string[] FieldNames = ["Document number", "Project", "Title", "Author", "Customer", "Section", "Date"];

    /// <summary>Finds the document fields of the form (document number, project, title, author, customer, section, date).</summary>
    public List<Field> Fields()
    {
        var texts = EditableTexts();
        var places = FieldNames.ToDictionary(n => n, _ => new List<(string, string, bool)>());
        void Add(string field, string key, string prefix)
        {
            // Only a title written in capitals in the form (e.g. "FAT REPORT") is kept in capitals.
            if (!places[field].Any(p => p.Item1 == key)) places[field].Add((key, prefix, field == "Title" && key.Length > prefix.Length && key[prefix.Length..] == key[prefix.Length..].ToUpperInvariant() && key[prefix.Length..].Any(char.IsLetter)));
        }
        string? Next(int i) => i + 1 < texts.Count ? texts[i + 1] : null;
        for (int i = 0; i < texts.Count; i++)
        {
            string t = texts[i];
            Match m;
            if ((m = Regex.Match(t, @"^(.*Document No\.?:\s*)(\S.*)$", RegexOptions.IgnoreCase)).Success || (m = Regex.Match(t, @"^(Document:\s*)(\S.*)$", RegexOptions.IgnoreCase)).Success) Add("Document number", t, m.Groups[1].Value);
            else if (Regex.IsMatch(t, @"^Document\s+NUMBER$", RegexOptions.IgnoreCase) && Next(i) is string number) Add("Document number", number, "");
            else if ((m = Regex.Match(t, @"^(FB Nr\.?:\s*)(\S.*)$", RegexOptions.IgnoreCase)).Success) Add("Project", t, m.Groups[1].Value);
            else if (Regex.IsMatch(t, @"^Project\s+NAME$", RegexOptions.IgnoreCase) && Next(i) is string project) Add("Project", project, "");
            else if (Regex.IsMatch(t, @"^Document\s+title$", RegexOptions.IgnoreCase) && Next(i) is string title) Add("Title", title, "");
            else if (Regex.IsMatch(t, @"^[FS]AT\s+report$", RegexOptions.IgnoreCase)) Add("Title", t, "");
            else if ((m = Regex.Match(t, @"^((?:Ersteller/)?Author:\s*)(\S.*)$", RegexOptions.IgnoreCase)).Success) Add("Author", t, m.Groups[1].Value);
            else if (Regex.IsMatch(t, @"^author:$", RegexOptions.IgnoreCase) && Next(i) is string author) Add("Author", author, "");
            else if (Regex.IsMatch(t, @"^customer:$", RegexOptions.IgnoreCase) && Next(i) is string customer) Add("Customer", customer, "");
            else if (Regex.IsMatch(t, @"^(Teilbereich/)?Section\s*:$", RegexOptions.IgnoreCase) && Next(i) is string section) Add("Section", section, "");
            else if ((m = Regex.Match(t, @"^(DATE\s+)(\d{1,2}\.\s?\d{1,2}\.\s?\d{4}\.?)$", RegexOptions.IgnoreCase)).Success) Add("Date", t, m.Groups[1].Value);
            else if (Regex.IsMatch(t, @"^\d{1,2}\.\s?\d{1,2}\.\s?\d{4}\.?$")) Add("Date", t, "");
        }
        return FieldNames.Where(n => places[n].Count > 0).Select(n => new Field(n, places[n])).ToList();
    }

    /// <summary>The report's chapters, graphs, tables and remarks inside the template.</summary>
    public void Save(string path, IReadOnlyList<ReportBlock> blocks, IReadOnlyDictionary<string, string> replace, IReadOnlyDictionary<string, string>? fields = null)
    {
        var output = new Dictionary<string, byte[]>(parts);
        // Document properties of the file (File → Info): title, project, author, document number.
        if (fields != null && parts.ContainsKey("docProps/core.xml"))
        {
            string core = Text("docProps/core.xml");
            foreach (var (element, field) in new[] { ("dc:title", "Title"), ("dc:subject", "Project"), ("dc:creator", "Author"), ("cp:keywords", "Document number"), ("cp:category", "Section") })
                if (fields.TryGetValue(field, out var value) && value.Length > 0)
                    core = Regex.IsMatch(core, $"<{element}[ >/]")
                        ? Regex.Replace(core, $"<{element}(?: [^>]*)?(?:/>|>[^<]*</{element}>)", $"<{element}>{WordReport.X(value)}</{element}>")
                        : core.Replace("</cp:coreProperties>", $"<{element}>{WordReport.X(value)}</{element}></cp:coreProperties>");
            output["docProps/core.xml"] = Encoding.UTF8.GetBytes(core);
        }
        string R(string xml) => ReplaceTexts(xml, replace);
        foreach (var key in parts.Keys.Where(k => Regex.IsMatch(k, @"^word/(header|footer)\d*\.xml$"))) output[key] = Encoding.UTF8.GetBytes(R(Text(key)));

        // Page geometry of the template: landscape pages keep its margins.
        int Attr(string xml, string name, int fallback) => int.TryParse(Regex.Match(xml, $"w:{name}=\"(\\d+)\"").Value.Split('"').ElementAtOrDefault(1), out int v) ? v : fallback;
        string pgSz = Regex.Match(finalSection, "<w:pgSz[^>]*/>").Value, pgMar = Regex.Match(finalSection, "<w:pgMar[^>]*/>").Value;
        int pageW = Attr(pgSz, "w", 11906), pageH = Attr(pgSz, "h", 16838), left = Attr(pgMar, "left", 1134), right = Attr(pgMar, "right", 1134), top = Attr(pgMar, "top", 1134), bottom = Attr(pgMar, "bottom", 1134);
        double portraitText = pageW - left - right, landscapeText = pageH - left - right;
        double TwipsToCm(double twips) => twips / 567.0;

        // Landscape copies of the default header and footer, widened by the ratio of the text widths.
        var rels = Text("word/_rels/document.xml.rels");
        string RelTarget(string type) => Regex.Match(finalSection, $"<w:{type}Reference w:type=\"default\" r:id=\"([^\"]+)\"").Groups[1].Value is { Length: > 0 } id
            ? Regex.Match(rels, $"Id=\"{Regex.Escape(id)}\"[^>]*Target=\"([^\"]+)\"|Target=\"([^\"]+)\"[^>]*Id=\"{Regex.Escape(id)}\"") is { Success: true } m ? (m.Groups[1].Value + m.Groups[2].Value) : "" : "";
        var extraRels = new StringBuilder(); var extraTypes = new StringBuilder();
        string landscapeRefs = "";
        foreach (var type in new[] { "header", "footer" })
        {
            string target = RelTarget(type); if (target.Length == 0) continue;
            string source = "word/" + target, copy = $"word/{type}Landscape.xml";
            output[copy] = Encoding.UTF8.GetBytes(Widen(R(Text(source)), landscapeText / portraitText));
            string sourceRels = $"word/_rels/{target}.rels";
            if (parts.TryGetValue(sourceRels, out var relBytes)) output[$"word/_rels/{type}Landscape.xml.rels"] = relBytes;
            extraRels.Append($"<Relationship Id=\"rTrs{type}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{type}\" Target=\"{type}Landscape.xml\"/>");
            extraTypes.Append($"<Override PartName=\"/word/{type}Landscape.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.{type}+xml\"/>");
            landscapeRefs += $"<w:{type}Reference w:type=\"default\" r:id=\"rTrs{type}\"/>";
        }

        // Section properties: the first (cover) section is the template's own; later ones continue the numbering
        // without the first-page header; landscape ones use the widened header and footer.
        string defaultRefs = string.Concat(Regex.Matches(finalSection, "<w:(header|footer)Reference w:type=\"default\"[^>]*/>").Select(m => m.Value));
        string rest = Regex.Replace(finalSection, "<w:(header|footer)Reference[^>]*/>|<w:titlePg[^>]*/>|<w:pgNumType[^>]*/>|<w:titlePg>\\s*</w:titlePg>", "");
        string Section(bool landscape, bool first)
        {
            if (first) return finalSection;
            string x = rest.Insert(rest.IndexOf('>') + 1, landscape ? landscapeRefs : defaultRefs);
            if (landscape) x = x.Replace(pgSz, $"<w:pgSz w:w=\"{pageH}\" w:h=\"{pageW}\" w:orient=\"landscape\"/>");
            return x;
        }

        var body = new StringBuilder();
        foreach (var element in front) body.Append(R(element));
        // Contents like the template's own: three levels.
        var headings = blocks.OfType<ReportHeading>().ToList();
        body.Append($"<w:p><w:pPr><w:pStyle w:val=\"{tocHeading ?? "TOCHeading"}\"/></w:pPr><w:r><w:t>Contents</w:t></w:r></w:p>");
        body.Append(WordReport.TocEntries(headings, 3, (int)portraitText));
        var captions = new WordReport.Captions(blocks);
        body.Append(captions.List("Figure", "List of figures", tocHeading ?? "TOCHeading", (int)portraitText)).Append(captions.List("Table", "List of tables", tocHeading ?? "TOCHeading", (int)portraitText));
        int heading = 0;

        bool landscapePage = false, firstSection = true; ReportSection? pending = null;
        var images = new List<byte[]>();
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block is ReportSection section) { pending = pending is { NewPage: true } ? section with { NewPage = true } : section; continue; }
            if (pending is { } next && (next.Landscape != landscapePage || next.NewPage))
            {
                body.Append($"<w:p><w:pPr>{Section(landscapePage, firstSection)}</w:pPr></w:p>");
                landscapePage = next.Landscape; firstSection = false;
            }
            pending = null;
            switch (block)
            {
                case ReportHeading h: body.Append(WordReport.Heading(h.Text, h.Level, heading++, headingNumbering.GetValueOrDefault(Math.Clamp(h.Level, 1, 6), ""))); break;
                case ReportParagraph p: body.Append(WordReport.Paragraph(p.Text, null, p.Muted ? "64748B" : null, p.Bold)); break;
                case ReportTable t: body.Append(captions.Paragraph(index)).Append(WordReport.Table(t)); break;
                case ReportRemark r: body.Append(Remark(r.Text, replace)); break;
                case ReportPageBreak: body.Append(WordReport.PageBreak()); break;
                case ReportImage i:
                    images.Add(i.Jpeg);
                    double width = TwipsToCm(landscapePage ? landscapeText : portraitText), height = TwipsToCm((landscapePage ? pageW : pageH) - top - bottom) - 6;
                    body.Append(WordReport.Image(images.Count, i, width, height)); body.Append(captions.Paragraph(index));
                    break;
            }
        }
        body.Append(Section(landscapePage, firstSection));
        output["word/document.xml"] = Encoding.UTF8.GetBytes(EnsureNamespaces(documentStart) + body + "</w:body></w:document>");

        for (int i = 1; i <= images.Count; i++)
        {
            output[$"word/media/report{i}.jpeg"] = images[i - 1];
            extraRels.Append($"<Relationship Id=\"rImg{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/report{i}.jpeg\"/>");
        }
        output["word/_rels/document.xml.rels"] = Encoding.UTF8.GetBytes(rels.Replace("</Relationships>", extraRels + "</Relationships>"));
        string types = Text("[Content_Types].xml");
        if (!Regex.IsMatch(types, "Extension=\"jpeg\"", RegexOptions.IgnoreCase)) extraTypes.Insert(0, "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
        output["[Content_Types].xml"] = Encoding.UTF8.GetBytes(types.Replace("</Types>", extraTypes + "</Types>"));

        string temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            foreach (var (name, bytes) in output)
            {
                using var stream = zip.CreateEntry(name, name.StartsWith("word/media/") ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                stream.Write(bytes);
            }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The template's Conclusion table titled "Remark" with the note (or empty for writing by hand), without its signature rows.</summary>
    string Remark(string text, IReadOnlyDictionary<string, string> replace)
    {
        if (conclusion == null) return WordReport.Table(new ReportTable(["Remark"], [new[] { text.Length == 0 ? "\n\n" : text }]));
        var rows = Rows(conclusion);
        string table = conclusion;
        if (rows.Count > 0) table = table.Replace(rows[0], SetText(rows[0], "Remark"));
        if (rows.Count > 1) table = table.Replace(rows[1], SetText(rows[1], text, blankLines: text.Length == 0 ? 2 : 0));
        foreach (var row in rows.Skip(2)) table = table.Replace(row, "");
        return table + "<w:p/>";
    }

    /// <summary>Puts a text into the first paragraph of a row (keeping its run formatting) and empties the others.</summary>
    static string SetText(string row, string text, int blankLines = 0)
    {
        var paragraphs = Paragraphs(row).ToList();
        if (paragraphs.Count == 0) return row;
        string first = paragraphs[0];
        string runProps = Regex.Match(first, "<w:r[ >].*?(<w:rPr>.*?</w:rPr>)", RegexOptions.Singleline).Groups[1].Value;
        string pPr = Regex.Match(first, "<w:pPr>.*?</w:pPr>", RegexOptions.Singleline).Value;
        var lines = text.Replace("\r", "").Split('\n').ToList();
        for (int i = 0; i < blankLines; i++) lines.Add("");
        string runs = string.Join("<w:r>" + runProps + "<w:br/></w:r>", lines.Select(l => $"<w:r>{runProps}<w:t xml:space=\"preserve\">{WordReport.X(l)}</w:t></w:r>"));
        string result = row.Replace(first, $"<w:p>{pPr}{runs}</w:p>");
        foreach (var other in paragraphs.Skip(1)) result = result.Replace(other, $"<w:p>{Regex.Match(other, "<w:pPr>.*?</w:pPr>", RegexOptions.Singleline).Value}</w:p>");
        return result;
    }

    /// <summary>
    /// Replaces whole paragraph texts; only the changed middle of the text is rewritten, so labels like
    /// "Document No.:" keep their formatting when only the number after them changes.
    /// </summary>
    static string ReplaceTexts(string xml, IReadOnlyDictionary<string, string> replace)
    {
        if (replace.Count == 0) return xml;
        return Regex.Replace(xml, @"<w:p[ >](?:(?!<w:p[ >]).)*?</w:p>", m =>
        {
            string p = m.Value;
            if (p.Contains("fldChar") || p.Contains("instrText")) return p;
            var texts = Regex.Matches(p, @"(<w:t(?: [^>]*)?>)([^<]*)(</w:t>)").ToList();
            string old = string.Concat(texts.Select(t => WebUtility.HtmlDecode(t.Groups[2].Value)));
            string trimmed = old.Trim();
            if (trimmed.Length == 0 || !replace.TryGetValue(trimmed, out var wanted) || wanted == trimmed) return p;
            string updated = old.Replace(trimmed, wanted);
            int prefix = 0; while (prefix < old.Length && prefix < updated.Length && old[prefix] == updated[prefix]) prefix++;
            int suffix = 0; while (suffix < old.Length - prefix && suffix < updated.Length - prefix && old[^(suffix + 1)] == updated[^(suffix + 1)]) suffix++;
            string middle = updated[prefix..(updated.Length - suffix)];
            int changeEnd = old.Length - suffix;
            var result = new StringBuilder(); int last = 0, position = 0; bool inserted = false;
            foreach (var t in texts)
            {
                string value = WebUtility.HtmlDecode(t.Groups[2].Value);
                int start = position, end = position + value.Length; position = end;
                var kept = new StringBuilder();
                // Characters before and after the change stay in their runs; the new middle goes where the change starts.
                for (int i = start; i < end; i++)
                {
                    if (i == prefix && !inserted) { kept.Append(middle); inserted = true; }
                    if (i < prefix || i >= changeEnd) kept.Append(value[i - start]);
                }
                result.Append(p, last, t.Index - last).Append(t.Groups[1].Value.Contains("xml:space") ? t.Groups[1].Value : t.Groups[1].Value.Replace("<w:t", "<w:t xml:space=\"preserve\""))
                    .Append(WordReport.X(kept.ToString())).Append(t.Groups[3].Value);
                last = t.Index + t.Length;
            }
            if (!inserted && texts.Count > 0)
            {
                // Change at the very end: append to the last text run.
                var tail = result.ToString(); int close = tail.LastIndexOf("</w:t>", StringComparison.Ordinal);
                result.Clear().Append(tail.Insert(close, WordReport.X(middle)));
            }
            result.Append(p, last, p.Length - last);
            return result.ToString();
        }, RegexOptions.Singleline);
    }

    /// <summary>
    /// Widens a header or footer to the landscape text width: tables, cells, columns, tab stops, text frames,
    /// horizontal positions of floating objects and horizontal lines; pictures (logos) keep their size.
    /// </summary>
    static string Widen(string xml, double factor)
    {
        string Scale(string value) => Math.Round(long.Parse(value) * factor).ToString(System.Globalization.CultureInfo.InvariantCulture);
        xml = Regex.Replace(xml, "(<w:(?:tblW|tcW|tblInd)\\b[^>]*?w:w=\")(\\d+)(\"[^>]*?w:type=\"dxa\")", m => m.Groups[1].Value + Scale(m.Groups[2].Value) + m.Groups[3].Value);
        xml = Regex.Replace(xml, "(<w:gridCol\\b[^>]*?w:w=\")(\\d+)(\")", m => m.Groups[1].Value + Scale(m.Groups[2].Value) + m.Groups[3].Value);
        xml = Regex.Replace(xml, "(<w:tab\\b[^>]*?w:pos=\")(\\d+)(\")", m => m.Groups[1].Value + Scale(m.Groups[2].Value) + m.Groups[3].Value);
        xml = Regex.Replace(xml, "<w:framePr\\b[^>]*>", m => Regex.Replace(m.Value, "(w:(?:w|x)=\")(\\d+)(\")", a => a.Groups[1].Value + Scale(a.Groups[2].Value) + a.Groups[3].Value));
        xml = Regex.Replace(xml, "(<wp:positionH\\b[^>]*>\\s*<wp:posOffset>)(-?\\d+)(</wp:posOffset>)", m => m.Groups[1].Value + Scale(m.Groups[2].Value) + m.Groups[3].Value);
        // Lines have no height: stretch them; everything with a height (logos) keeps its proportions.
        xml = Regex.Replace(xml, "(<(?:wp:extent|a:ext)\\b[^>]*?cx=\")(\\d+)(\"[^>]*?cy=\"0\")", m => m.Groups[1].Value + Scale(m.Groups[2].Value) + m.Groups[3].Value);
        return xml;
    }

    static string EnsureNamespaces(string start)
    {
        foreach (var (prefix, uri) in new[] { ("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"), ("wp", "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"),
            ("a", "http://schemas.openxmlformats.org/drawingml/2006/main"), ("pic", "http://schemas.openxmlformats.org/drawingml/2006/picture") })
            if (!start.Contains($"xmlns:{prefix}=")) start = start.Replace("<w:document ", $"<w:document xmlns:{prefix}=\"{uri}\" ");
        return start;
    }

    static string Plain(string xml) => WebUtility.HtmlDecode(string.Concat(Regex.Matches(xml, @"<w:t(?: [^>]*)?>([^<]*)</w:t>").Select(m => m.Groups[1].Value)));
    static IEnumerable<string> Paragraphs(string xml) => Regex.Matches(xml, @"<w:p[ >](?:(?!<w:p[ >]).)*?</w:p>", RegexOptions.Singleline).Select(m => m.Value);
    static List<string> Rows(string table) => Regex.Matches(table, @"<w:tr[ >].*?</w:tr>", RegexOptions.Singleline).Select(m => m.Value).ToList();

    /// <summary>Top-level elements (paragraphs, tables, content controls, section properties) of a document body.</summary>
    static List<string> TopLevel(string body)
    {
        var result = new List<string>(); int depth = 0, start = 0; string kind = "";
        foreach (Match m in Regex.Matches(body, @"<(/?)w:(p|tbl|sdt|sectPr)\b[^>]*?(/?)>"))
        {
            bool close = m.Groups[1].Value == "/", single = m.Groups[3].Value == "/"; string tag = m.Groups[2].Value;
            if (depth == 0 && single) { result.Add(m.Value); continue; }
            if (single) continue;
            if (!close) { if (depth == 0) { start = m.Index; kind = tag; depth = 1; } else if (tag == kind) depth++; }
            else if (tag == kind && --depth == 0) result.Add(body[start..(m.Index + m.Length)]);
        }
        return result;
    }
}
