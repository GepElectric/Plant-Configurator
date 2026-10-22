using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace TiaTraceStudio;

/// <summary>Content of a Word report, in document order.</summary>
public abstract record ReportBlock;
/// <summary>Chapter heading; Number is printed by the basic report, templates number their headings themselves.</summary>
public sealed record ReportHeading(int Level, string Text, string Number = "") : ReportBlock
{
    public string Full => Number.Length > 0 ? $"{Number} {Text}" : Text;
}
/// <summary>Remark of a test: in a template it takes the form of the template's "Conclusion" table.</summary>
public sealed record ReportRemark(string Text) : ReportBlock;
public sealed record ReportParagraph(string Text, bool Muted = false, bool Bold = false) : ReportBlock;
/// <summary>Table; Widths are column shares in percent (optional).</summary>
public sealed record ReportTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<int>? Widths = null, string? Caption = null) : ReportBlock;
public sealed record ReportImage(byte[] Jpeg, int WidthPx, int HeightPx, string Caption) : ReportBlock;
public sealed record ReportPageBreak : ReportBlock;
/// <summary>The following content starts a new Word section: portrait or landscape; NewPage also when the orientation stays.</summary>
public sealed record ReportSection(bool Landscape, bool NewPage = false) : ReportBlock;

/// <summary>
/// Minimal .docx writer (Office Open XML, no extra libraries): title page, table of contents, Heading 1–6
/// chapters, tables, graph images with captions, portrait and landscape sections, page number footer.
/// </summary>
public static class WordReport
{
    const double PortraitImageCm = 16.5, LandscapeImageCm = 24.5, LandscapeImageMaxHeightCm = 13.5;
    const string Margins = "<w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"567\" w:footer=\"567\" w:gutter=\"0\"/>";
    static string SectionProperties(bool landscape) => "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rFooter\"/>"
        + (landscape ? "<w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/>" : "<w:pgSz w:w=\"11906\" w:h=\"16838\"/>") + Margins + "</w:sectPr>";

    public static void Save(string path, string title, IReadOnlyList<string> subtitle, IEnumerable<ReportBlock> content)
    {
        var blocks = content.ToList();
        var images = new List<byte[]>();
        var body = new StringBuilder();
        body.Append(Paragraph(title, "Title"));
        foreach (var line in subtitle) body.Append(Paragraph(line, "Subtitle"));
        body.Append("<w:p><w:pPr><w:pStyle w:val=\"TOCHeading\"/></w:pPr><w:r><w:t>Contents</w:t></w:r></w:p>");
        // Table of contents field whose shown result already is the chapter list, so Word asks nothing when opening;
        // right click → Update field adds page numbers.
        var headings = blocks.OfType<ReportHeading>().ToList();
        body.Append(TocEntries(headings, 6, 9628));
        var captions = new Captions(blocks);
        body.Append(captions.List("Figure", "List of figures", "TOCHeading", 9628)).Append(captions.List("Table", "List of tables", "TOCHeading", 9628));
        int heading = 0;
        // Sections: each break closes the running section with its own page size (portrait or landscape).
        bool landscape = false; ReportSection? pending = null;
        void Flush()
        {
            if (pending is { } next && (next.Landscape != landscape || next.NewPage))
            { body.Append($"<w:p><w:pPr>{SectionProperties(landscape)}</w:pPr></w:p>"); landscape = next.Landscape; }
            pending = null;
        }
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block is ReportSection section) { pending = pending is { NewPage: true } ? section with { NewPage = true } : section; continue; }
            Flush();
            switch (block)
            {
                case ReportHeading h: body.Append(Heading(h.Full, h.Level, heading++)); break;
                case ReportRemark r: body.Append(Table(new ReportTable(["Remark"], [new[] { r.Text.Length == 0 ? "\n\n" : r.Text }]))); break;
                case ReportParagraph p: body.Append(Paragraph(p.Text, null, p.Muted ? "64748B" : null, p.Bold)); break;
                case ReportTable t: body.Append(captions.Paragraph(index)).Append(Table(t)); break;
                case ReportImage i: images.Add(i.Jpeg); body.Append(Image(images.Count, i, landscape ? LandscapeImageCm : PortraitImageCm, landscape ? LandscapeImageMaxHeightCm : 22)); body.Append(captions.Paragraph(index)); break;
                case ReportPageBreak: body.Append(PageBreak()); break;
            }
        }
        string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\""
            + " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\""
            + " xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body
            + SectionProperties(landscape) + "</w:body></w:document>";

        string temp = path + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Entry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
                + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
                + "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>"
                + "<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/></Types>");
            Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                + "<Relationship Id=\"rSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>"
                + "<Relationship Id=\"rFooter\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>");
            for (int i = 1; i <= images.Count; i++)
            {
                rels.Append($"<Relationship Id=\"rImg{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{i}.jpeg\"/>");
                var entry = zip.CreateEntry($"word/media/image{i}.jpeg", CompressionLevel.NoCompression);
                using var stream = entry.Open(); stream.Write(images[i - 1]);
            }
            rels.Append("</Relationships>");
            Entry(zip, "word/_rels/document.xml.rels", rels.ToString());
            Entry(zip, "word/document.xml", document);
            Entry(zip, "word/styles.xml", Styles);
            // Footer of every section: report title and page number.
            const string grey = "<w:rPr><w:color w:val=\"64748B\"/><w:sz w:val=\"17\"/></w:rPr>";
            Entry(zip, "word/footer1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                + $"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r>{grey}<w:t xml:space=\"preserve\">{X(title)} · Commissioning report · Page </w:t></w:r>"
                + $"<w:r>{grey}<w:fldChar w:fldCharType=\"begin\"/></w:r><w:r>{grey}<w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r>"
                + $"<w:r>{grey}<w:fldChar w:fldCharType=\"separate\"/></w:r><w:r>{grey}<w:t>1</w:t></w:r><w:r>{grey}<w:fldChar w:fldCharType=\"end\"/></w:r></w:p></w:ftr>");
            Entry(zip, "word/settings.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>");
        }
        File.Move(temp, path, overwrite: true);
    }

    static void Entry(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    static string BookmarkName(int index) => $"_TocReport{index:D5}";

    /// <summary>Heading with a bookmark, the target of its contents entry.</summary>
    internal static string Heading(string text, int level, int index, string paragraphProperties = "") =>
        $"<w:p><w:pPr><w:pStyle w:val=\"Heading{Math.Clamp(level, 1, 6)}\"/>{paragraphProperties}</w:pPr><w:bookmarkStart w:id=\"{9000 + index}\" w:name=\"{BookmarkName(index)}\"/>"
        + $"<w:r><w:t xml:space=\"preserve\">{X(text)}</w:t></w:r><w:bookmarkEnd w:id=\"{9000 + index}\"/></w:p>";

    /// <summary>
    /// Table of contents: a TOC field whose shown result already lists the chapters (Word asks nothing when
    /// opening); every entry is a link to its heading, page numbers appear with right click → Update field.
    /// </summary>
    internal static string TocEntries(IReadOnlyList<ReportHeading> headings, int levels, int tabPosition)
    {
        var entries = headings.Select((h, i) => (Heading: h, Index: i)).Where(e => e.Heading.Level <= levels).ToList();
        if (entries.Count == 0) return "";
        var body = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            var (h, index) = entries[i];
            body.Append($"<w:p><w:pPr><w:pStyle w:val=\"TOC{Math.Clamp(h.Level, 1, 6)}\"/><w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"{tabPosition}\"/></w:tabs></w:pPr>");
            if (i == 0) body.Append($"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> TOC \\o \"1-{levels}\" \\h \\z \\u </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>");
            body.Append($"<w:hyperlink w:anchor=\"{BookmarkName(index)}\" w:history=\"1\"><w:r><w:t xml:space=\"preserve\">{X(h.Full)}</w:t></w:r><w:r><w:tab/></w:r>"
                + $"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGEREF {BookmarkName(index)} \\h </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t></w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:hyperlink>");
            if (i == entries.Count - 1) body.Append("<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>");
            body.Append("</w:p>");
        }
        return body.ToString();
    }

    /// <summary>
    /// Numbered captions ("Figure 3: …" below a graph, "Table 5: …" above a table), each a Word caption with a SEQ
    /// field and a bookmark, so the lists of figures and tables at the beginning link to them and get page numbers.
    /// </summary>
    internal sealed class Captions
    {
        readonly Dictionary<int, (string Kind, int Number, string Text)> byBlock = [];
        public Captions(IReadOnlyList<ReportBlock> blocks)
        {
            int figures = 0, tables = 0;
            for (int i = 0; i < blocks.Count; i++)
                if (blocks[i] is ReportImage image) byBlock[i] = ("Figure", ++figures, image.Caption);
                else if (blocks[i] is ReportTable { Caption: string caption }) byBlock[i] = ("Table", ++tables, caption);
        }
        static string Bookmark(string kind, int number) => $"_TocCap{kind}{number:D4}";
        static int Id(string kind, int number) => (kind == "Figure" ? 20000 : 30000) + number;

        public string Paragraph(int block)
        {
            if (!byBlock.TryGetValue(block, out var c)) return "";
            string keep = c.Kind == "Table" ? "<w:keepNext/>" : "";
            return $"<w:p><w:pPr><w:pStyle w:val=\"Caption\"/>{keep}<w:jc w:val=\"center\"/></w:pPr><w:bookmarkStart w:id=\"{Id(c.Kind, c.Number)}\" w:name=\"{Bookmark(c.Kind, c.Number)}\"/>"
                + $"<w:r><w:t xml:space=\"preserve\">{c.Kind} </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> SEQ {c.Kind} \\* ARABIC </w:instrText></w:r>"
                + $"<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>{c.Number}</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
                + $"<w:r><w:t xml:space=\"preserve\">: {X(c.Text)}</w:t></w:r><w:bookmarkEnd w:id=\"{Id(c.Kind, c.Number)}\"/></w:p>";
        }

        /// <summary>List of figures or tables: a table-of-figures field whose shown result already lists the captions.</summary>
        public string List(string kind, string title, string headingStyle, int tabPosition)
        {
            var entries = byBlock.Values.Where(c => c.Kind == kind).OrderBy(c => c.Number).ToList();
            if (entries.Count == 0) return "";
            var body = new StringBuilder($"<w:p><w:pPr><w:pStyle w:val=\"{headingStyle}\"/></w:pPr><w:r><w:t>{title}</w:t></w:r></w:p>");
            for (int i = 0; i < entries.Count; i++)
            {
                var c = entries[i];
                body.Append($"<w:p><w:pPr><w:pStyle w:val=\"TableofFigures\"/><w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"{tabPosition}\"/></w:tabs><w:spacing w:after=\"60\"/></w:pPr>");
                if (i == 0) body.Append($"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> TOC \\h \\z \\c \"{kind}\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>");
                body.Append($"<w:hyperlink w:anchor=\"{Bookmark(kind, c.Number)}\" w:history=\"1\"><w:r><w:t xml:space=\"preserve\">{kind} {c.Number}: {X(c.Text)}</w:t></w:r><w:r><w:tab/></w:r>"
                    + $"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGEREF {Bookmark(kind, c.Number)} \\h </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t></w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:hyperlink>");
                if (i == entries.Count - 1) body.Append("<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>");
                body.Append("</w:p>");
            }
            return body.ToString();
        }
    }

    internal static string X(string text) => SecurityElement.Escape(text) ?? "";

    internal static string Paragraph(string text, string? style, string? color = null, bool bold = false)
    {
        var run = new StringBuilder("<w:r>");
        if (color != null || bold) run.Append("<w:rPr>" + (bold ? "<w:b/>" : "") + (color != null ? $"<w:color w:val=\"{color}\"/>" : "") + "</w:rPr>");
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++) { if (i > 0) run.Append("<w:br/>"); run.Append($"<w:t xml:space=\"preserve\">{X(lines[i])}</w:t>"); }
        run.Append("</w:r>");
        return "<w:p>" + (style != null ? $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>" : "") + run + "</w:p>";
    }

    internal static string PageBreak() => "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>";

    internal static string Table(ReportTable table)
    {
        // Borders inline, so the table looks the same in the basic report and in company templates.
        var xml = new StringBuilder("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>"
            + "<w:top w:val=\"single\" w:sz=\"4\" w:color=\"A0AEC0\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"A0AEC0\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"A0AEC0\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"A0AEC0\"/>"
            + "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"A0AEC0\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"A0AEC0\"/></w:tblBorders>"
            + "<w:tblCellMar><w:top w:w=\"30\" w:type=\"dxa\"/><w:left w:w=\"80\" w:type=\"dxa\"/><w:bottom w:w=\"30\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>");
        void Row(IReadOnlyList<string> cells, bool header)
        {
            xml.Append("<w:tr>" + (header ? "<w:trPr><w:tblHeader/></w:trPr>" : ""));
            for (int c = 0; c < cells.Count; c++)
                xml.Append("<w:tc><w:tcPr>" + (table.Widths is { } widths && c < widths.Count ? $"<w:tcW w:w=\"{widths[c] * 50}\" w:type=\"pct\"/>" : "") + (header ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"E2E8F0\"/>" : "") + "</w:tcPr>" + Paragraph(cells[c], "TableText", null, header) + "</w:tc>");
            xml.Append("</w:tr>");
        }
        Row(table.Header, true);
        foreach (var row in table.Rows) Row(row, false);
        return xml + "</w:tbl>" + Paragraph("", null);
    }

    internal static string Image(int index, ReportImage image, double maxWidthCm, double maxHeightCm)
    {
        double ratio = (double)image.HeightPx / Math.Max(1, image.WidthPx);
        double widthCm = Math.Min(maxWidthCm, maxHeightCm / ratio);
        long cx = (long)(widthCm * 360000), cy = (long)(cx * ratio);
        int id = 5000 + index;
        return "<w:p><w:pPr><w:keepNext/><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">"
            + $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{id}\" name=\"Graf {index}\"/>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
            + $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"graf{index}.jpeg\"/><pic:cNvPicPr/></pic:nvPicPr>"
            + $"<pic:blipFill><a:blip r:embed=\"rImg{index}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
            + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
            + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    static string HeadingStyle(int level, int size, string color) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>"
        + $"<w:pPr><w:keepNext/><w:spacing w:before=\"{(level == 1 ? 360 : 240)}\" w:after=\"120\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/></w:rPr></w:style>";

    static readonly string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
        + "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"21\"/><w:lang w:val=\"en-GB\"/></w:rPr></w:rPrDefault>"
        + "<w:pPrDefault><w:pPr><w:spacing w:after=\"80\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
        + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:before=\"2400\" w:after=\"240\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"172B45\"/><w:sz w:val=\"52\"/></w:rPr></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"Subtitle\"><w:name w:val=\"Subtitle\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:rPr><w:color w:val=\"475569\"/><w:sz w:val=\"26\"/></w:rPr></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"TOCHeading\"><w:name w:val=\"TOC Heading\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:pageBreakBefore/><w:spacing w:after=\"200\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"172B45\"/><w:sz w:val=\"32\"/></w:rPr></w:style>"
        + HeadingStyle(1, 32, "172B45") + HeadingStyle(2, 28, "1E3A5F") + HeadingStyle(3, 25, "2563EB") + HeadingStyle(4, 23, "334155") + HeadingStyle(5, 22, "334155") + HeadingStyle(6, 21, "475569")
        + string.Concat(Enumerable.Range(1, 6).Select(level => $"<w:style w:type=\"paragraph\" w:styleId=\"TOC{level}\"><w:name w:val=\"toc {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"39\"/>"
            + $"<w:pPr><w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"9628\"/></w:tabs><w:spacing w:after=\"20\"/><w:ind w:left=\"{(level - 1) * 284}\"/></w:pPr>" + (level == 1 ? "<w:rPr><w:b/></w:rPr>" : "") + "</w:style>"))
        + "<w:style w:type=\"paragraph\" w:styleId=\"Caption\"><w:name w:val=\"caption\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:jc w:val=\"center\"/><w:spacing w:after=\"240\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"475569\"/><w:sz w:val=\"19\"/></w:rPr></w:style>"
        + "<w:style w:type=\"paragraph\" w:styleId=\"TableText\"><w:name w:val=\"Table Text\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:rPr><w:sz w:val=\"19\"/></w:rPr></w:style>"
        + "<w:style w:type=\"table\" w:styleId=\"ReportTable\"><w:name w:val=\"Report Table\"/><w:tblPr><w:tblBorders>"
        + "<w:top w:val=\"single\" w:sz=\"4\" w:color=\"CBD5E1\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"CBD5E1\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"CBD5E1\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"CBD5E1\"/>"
        + "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"CBD5E1\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"CBD5E1\"/></w:tblBorders><w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"80\" w:type=\"dxa\"/><w:bottom w:w=\"40\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>"
        + "</w:styles>";
}
