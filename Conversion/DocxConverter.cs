using System.IO;
using System.IO.Compression;
using System.Text;
using LandMark.Markdown;
using LandMark.Styling;

namespace LandMark.Conversion;

/// <summary>
/// Converts Markdown to a .docx using only the framework: a .docx is a zip of XML parts.
/// It uses real Word styles (Heading 1-6, List Paragraph, Quote, Hyperlink...) so the
/// result stays editable and picks up style changes.
/// </summary>
public static class DocxConverter
{
    /// <param name="marginInches">Page margin on all four sides of the US Letter page.</param>
    public static void Convert(string markdown, string outputPath, DocumentStyle style, double marginInches = 1)
    {
        var writer = new DocxWriter(style, marginInches);
        writer.WriteBlocks(MarkdownParser.Parse(markdown));

        using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        writer.Save(file);
    }
}

internal sealed class DocxWriter(DocumentStyle style, double marginInches)
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int IndentStep = 720;   // twentieths of a point: 0.5"
    private const int QuoteIndent = 360;
    private const int BulletNumId = 1;
    private const int PageWidth = 12240;   // US Letter, 8.5" x 11"
    private const int PageHeight = 15840;

    private readonly int _margin = (int)Math.Round(marginInches * 1440);
    private int TextWidth => PageWidth - 2 * _margin;   // e.g. 6.5" between 1" margins

    private readonly StringBuilder _body = new();
    private readonly List<string> _hyperlinks = [];
    private readonly List<(int NumId, int Level, int Start)> _orderedLists = [];
    private int _nextNumId = BulletNumId + 1;

    private (int NumId, int Level)? _pendingNumbering;
    private int _listDepth = -1;
    private int _quoteDepth;

    private readonly record struct Fmt(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, bool Link = false);

    public void Save(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        AddPart(zip, "[Content_Types].xml", ContentTypesXml);
        AddPart(zip, "_rels/.rels", PackageRelationshipsXml);
        AddPart(zip, "word/document.xml", DocumentXml());
        AddPart(zip, "word/_rels/document.xml.rels", DocumentRelationshipsXml());
        AddPart(zip, "word/styles.xml", StylesXml());
        AddPart(zip, "word/numbering.xml", NumberingXml());
    }

    private static void AddPart(ZipArchive zip, string path, string xml)
    {
        using var part = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        part.Write(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + xml));
    }

    // ---- Blocks ----

    public void WriteBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
            WriteBlock(block);
    }

    private void WriteBlock(Block block)
    {
        switch (block)
        {
            case Heading heading:
                StartParagraph($"Heading{heading.Level}");
                WriteInlines(heading.Content, default);
                _body.Append("</w:p>");
                break;
            case Paragraph paragraph:
                StartParagraph();
                WriteInlines(paragraph.Content, default);
                _body.Append("</w:p>");
                break;
            case CodeBlock code:
                WriteCode(code);
                break;
            case BlockQuote quote:
                _quoteDepth++;
                WriteBlocks(quote.Children);
                _quoteDepth--;
                break;
            case ListBlock list:
                WriteList(list);
                break;
            case Table table:
                WriteTable(table);
                break;
            case ThematicBreak:
                StartParagraph(bottomBorder: true);
                _body.Append("</w:p>");
                break;
        }
    }

    /// <summary>Opens a &lt;w:p&gt; with its properties; the caller writes runs and closes it.</summary>
    private void StartParagraph(string? styleId = null, bool bottomBorder = false, string? justification = null, bool inTable = false)
    {
        styleId ??= _quoteDepth > 0 ? "Quote"
            : _listDepth >= 0 ? "ListParagraph"
            : null;

        _body.Append("<w:p><w:pPr>");
        if (styleId != null)
            _body.Append($"<w:pStyle w:val=\"{styleId}\"/>");

        string? indent = null;
        if (!inTable && _pendingNumbering is { } numbering)
        {
            // The first paragraph of a list item carries the bullet/number.
            _body.Append($"<w:numPr><w:ilvl w:val=\"{numbering.Level}\"/><w:numId w:val=\"{numbering.NumId}\"/></w:numPr>");
            _pendingNumbering = null;
        }
        else if (!inTable)
        {
            // Later paragraphs in a list item, and nested quotes, line up by indentation.
            int left = (_listDepth + 1) * IndentStep + _quoteDepth * QuoteIndent;
            if (left > 0) indent = $"<w:ind w:left=\"{left}\"/>";
        }

        if (bottomBorder)
            _body.Append($"<w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"1\" w:color=\"{DocumentStyle.Hex(style.AccentColor, "BFBFBF")}\"/></w:pBdr>");
        _body.Append(indent);
        if (justification != null)
            _body.Append($"<w:jc w:val=\"{justification}\"/>");
        _body.Append("</w:pPr>");
    }

    private void WriteCode(CodeBlock code)
    {
        StartParagraph("CodeBlock");
        for (int i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0) _body.Append("<w:r><w:br/></w:r>");
            WriteRun(code.Lines[i], default);
        }
        _body.Append("</w:p>");
    }

    private void WriteList(ListBlock list)
    {
        _listDepth++;
        int level = Math.Min(_listDepth, 8);
        int numId = BulletNumId;

        if (list.Ordered)
        {
            // Each ordered list gets its own numbering instance so it restarts at its start value.
            numId = _nextNumId++;
            _orderedLists.Add((numId, level, list.Start));
        }

        foreach (var item in list.Items)
        {
            _pendingNumbering = (numId, level);
            if (item.Checked is bool done)
            {
                // Put the task box in the item's first paragraph, or on its own if it has none.
                var children = item.Children.ToList();
                var box = new Text(done ? "☑ " : "☐ ");
                if (children.Count > 0 && children[0] is Paragraph first)
                    children[0] = new Paragraph([box, .. first.Content]);
                else
                    children.Insert(0, new Paragraph([box]));
                WriteBlocks(children);
            }
            else if (item.Children.Count == 0)
            {
                StartParagraph();
                _body.Append("</w:p>");
            }
            else
            {
                WriteBlocks(item.Children);
            }
            _pendingNumbering = null;
        }

        _listDepth--;
    }

    private void WriteTable(Table table)
    {
        int columns = table.Alignments.Count;
        _pendingNumbering = null;

        _body.Append("<w:tbl>").Append(TablePropertiesXml()).Append("<w:tblGrid>");
        for (int c = 0; c < columns; c++)
            _body.Append($"<w:gridCol w:w=\"{TextWidth / columns}\"/>");
        _body.Append("</w:tblGrid>");

        WriteTableRow(table.Header, table.Alignments, header: true);
        foreach (var row in table.Rows)
            WriteTableRow(row, table.Alignments, header: false);
        _body.Append("</w:tbl>");

        // Spacing after the table; also stops Word merging two adjacent tables.
        StartParagraph();
        _body.Append("</w:p>");
    }

    private void WriteTableRow(TableRow row, IReadOnlyList<ColumnAlign> alignments, bool header)
    {
        _body.Append("<w:tr>");
        if (header) _body.Append("<w:trPr><w:tblHeader/></w:trPr>");

        for (int c = 0; c < row.Cells.Count; c++)
        {
            _body.Append("<w:tc>");
            if (header) _body.Append($"<w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{DocumentStyle.Hex(style.TableHeaderBackground, "F2F2F2")}\"/></w:tcPr>");

            string? justification = alignments[c] switch
            {
                ColumnAlign.Center => "center",
                ColumnAlign.Right => "right",
                _ => null,
            };
            StartParagraph("TableText", justification: justification, inTable: true);
            WriteInlines(row.Cells[c], new Fmt(Bold: header));
            _body.Append("</w:p></w:tc>");
        }
        _body.Append("</w:tr>");
    }

    // ---- Inlines ----

    private void WriteInlines(IEnumerable<Inline> inlines, Fmt fmt)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Text text:
                    WriteRun(text.Value, fmt);
                    break;
                case CodeSpan code:
                    WriteRun(code.Code, fmt with { Code = true });
                    break;
                case LineBreak:
                    _body.Append("<w:r><w:br/></w:r>");
                    break;
                case Emphasis emphasis:
                    WriteInlines(emphasis.Children, emphasis.Kind switch
                    {
                        EmphasisKind.Bold => fmt with { Bold = true },
                        EmphasisKind.Italic => fmt with { Italic = true },
                        _ => fmt with { Strike = true },
                    });
                    break;
                case Image image:
                    WriteRun($"[Image: {image.Alt}]", fmt with { Italic = true });
                    break;
                case Link link when Uri.TryCreate(link.Url, UriKind.Absolute, out _):
                    _hyperlinks.Add(link.Url);
                    _body.Append($"<w:hyperlink r:id=\"rIdLink{_hyperlinks.Count}\" w:history=\"1\">");
                    WriteInlines(link.Children, fmt with { Link = true });
                    _body.Append("</w:hyperlink>");
                    break;
                case Link link:
                    // Relative links (other .md files, #anchors) have nowhere to point in a .docx.
                    WriteInlines(link.Children, fmt);
                    break;
            }
        }
    }

    private void WriteRun(string text, Fmt fmt)
    {
        _body.Append("<w:r>");
        if (fmt != default)
        {
            _body.Append("<w:rPr>");
            if (fmt.Code) _body.Append("<w:rStyle w:val=\"InlineCode\"/>");
            else if (fmt.Link) _body.Append("<w:rStyle w:val=\"Hyperlink\"/>");
            if (fmt.Bold) _body.Append("<w:b/>");
            if (fmt.Italic) _body.Append("<w:i/>");
            if (fmt.Strike) _body.Append("<w:strike/>");
            if (fmt.Code && fmt.Link) _body.Append($"<w:color w:val=\"{DocumentStyle.Hex(style.LinkColor)}\"/><w:u w:val=\"single\"/>");
            _body.Append("</w:rPr>");
        }
        _body.Append("<w:t xml:space=\"preserve\">").Append(XmlEscape(text)).Append("</w:t></w:r>");
    }

    private static string XmlEscape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '"': escaped.Append("&quot;"); break;
                case < ' ' and not ('\t' or '\n' or '\r'): break;   // not allowed in XML
                default: escaped.Append(c); break;
            }
        }
        return escaped.ToString();
    }

    // ---- Package parts ----

    private string DocumentXml()
    {
        int headerFooter = Math.Min(720, _margin / 2);   // header/footer distance stays inside the margin
        return $"<w:document xmlns:w=\"{WordNamespace}\" xmlns:r=\"{RelationshipNamespace}\"><w:body>{_body}" +
            $"<w:sectPr><w:pgSz w:w=\"{PageWidth}\" w:h=\"{PageHeight}\"/>" +
            $"<w:pgMar w:top=\"{_margin}\" w:right=\"{_margin}\" w:bottom=\"{_margin}\" w:left=\"{_margin}\" " +
            $"w:header=\"{headerFooter}\" w:footer=\"{headerFooter}\" w:gutter=\"0\"/>" +
            "</w:sectPr></w:body></w:document>";
    }

    private string DocumentRelationshipsXml()
    {
        var xml = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        xml.Append($"<Relationship Id=\"rIdStyles\" Type=\"{RelationshipNamespace}/styles\" Target=\"styles.xml\"/>");
        xml.Append($"<Relationship Id=\"rIdNumbering\" Type=\"{RelationshipNamespace}/numbering\" Target=\"numbering.xml\"/>");
        for (int i = 0; i < _hyperlinks.Count; i++)
            xml.Append($"<Relationship Id=\"rIdLink{i + 1}\" Type=\"{RelationshipNamespace}/hyperlink\" Target=\"{XmlEscape(_hyperlinks[i])}\" TargetMode=\"External\"/>");
        return xml.Append("</Relationships>").ToString();
    }

    private const string ContentTypesXml =
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
        "</Types>";

    private const string PackageRelationshipsXml =
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        $"<Relationship Id=\"rId1\" Type=\"{RelationshipNamespace}/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    private string NumberingXml()
    {
        var xml = new StringBuilder($"<w:numbering xmlns:w=\"{WordNamespace}\">");

        xml.Append(AbstractNum(0, level => (level % 3) switch
        {
            0 => ("bullet", "&#xF0B7;", "Symbol"),
            1 => ("bullet", "o", "Courier New"),
            _ => ("bullet", "&#xF0A7;", "Wingdings"),
        }));
        xml.Append(AbstractNum(1, level => ((level % 3) switch
        {
            0 => "decimal",
            1 => "lowerLetter",
            _ => "lowerRoman",
        }, $"%{level + 1}.", null)));

        xml.Append($"<w:num w:numId=\"{BulletNumId}\"><w:abstractNumId w:val=\"0\"/></w:num>");
        foreach (var (numId, level, start) in _orderedLists)
            xml.Append($"<w:num w:numId=\"{numId}\"><w:abstractNumId w:val=\"1\"/>" +
                       $"<w:lvlOverride w:ilvl=\"{level}\"><w:startOverride w:val=\"{start}\"/></w:lvlOverride></w:num>");

        return xml.Append("</w:numbering>").ToString();
    }

    private static string AbstractNum(int id, Func<int, (string Format, string Text, string? Font)> describeLevel)
    {
        var xml = new StringBuilder($"<w:abstractNum w:abstractNumId=\"{id}\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        for (int level = 0; level < 9; level++)
        {
            var (format, text, font) = describeLevel(level);
            xml.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{format}\"/>" +
                       $"<w:lvlText w:val=\"{text}\"/><w:lvlJc w:val=\"left\"/>" +
                       $"<w:pPr><w:ind w:left=\"{IndentStep * (level + 1)}\" w:hanging=\"360\"/></w:pPr>");
            if (font != null)
                xml.Append($"<w:rPr><w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:hint=\"default\"/></w:rPr>");
            xml.Append("</w:lvl>");
        }
        return xml.Append("</w:abstractNum>").ToString();
    }

    // ---- Styles (from the DocumentStyle) ----

    /// <summary>The first installed font from a priority list, as a Word rFonts element.</summary>
    private static string Fonts(string fontList, string fallback = "Arial")
    {
        string font = XmlEscape(InstalledFonts.Resolve(fontList, fallback));
        return $"<w:rFonts w:ascii=\"{font}\" w:eastAsia=\"{font}\" w:hAnsi=\"{font}\" w:cs=\"{font}\"/>";
    }

    private static string Size(int halfPoints) => $"<w:sz w:val=\"{halfPoints}\"/><w:szCs w:val=\"{halfPoints}\"/>";

    private string HeadingStyle(int level, string fontList, string color, double size, bool italic = false) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/>" +
        "<w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>" +
        $"<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"{(level <= 2 ? 360 : 240)}\" w:after=\"80\"/>" +
        $"<w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
        $"<w:rPr>{Fonts(fontList)}{(style.BoldHeadings ? "<w:b/><w:bCs/>" : "")}{(italic ? "<w:i/>" : "")}" +
        $"<w:color w:val=\"{DocumentStyle.Hex(color)}\"/>{Size(DocumentStyle.HalfPoints(size))}</w:rPr></w:style>";

    private string StylesXml()
    {
        int body = DocumentStyle.HalfPoints(style.BodySize);
        int code = DocumentStyle.HalfPoints(style.BodySize - 1);
        string codeFonts = Fonts(style.CodeFont, "Consolas");
        string codeShading = $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{DocumentStyle.Hex(style.CodeBackground, "F3F3F3")}\"/>";

        return
            $"<w:styles xmlns:w=\"{WordNamespace}\">" +
            "<w:docDefaults>" +
            $"<w:rPrDefault><w:rPr>{Fonts(style.BodyFont, "Calibri")}<w:color w:val=\"{DocumentStyle.Hex(style.TextColor)}\"/>" +
            $"{Size(body)}<w:lang w:val=\"en-US\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>" +
            "</w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
            HeadingStyle(1, style.HeadingFont, style.HeadingColor, style.Heading1Size) +
            HeadingStyle(2, style.HeadingFont, style.HeadingColor, style.Heading2Size) +
            HeadingStyle(3, style.SubheadingFont, style.SubheadingColor, style.Heading3Size) +
            HeadingStyle(4, style.SubheadingFont, style.SubheadingColor, style.BodySize + 1) +
            HeadingStyle(5, style.SubheadingFont, style.SubheadingColor, style.BodySize) +
            HeadingStyle(6, style.SubheadingFont, style.SubheadingColor, style.BodySize, italic: true) +
            "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/>" +
            "<w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"34\"/><w:qFormat/>" +
            "<w:pPr><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/>" +
            "<w:next w:val=\"Normal\"/><w:uiPriority w:val=\"29\"/><w:qFormat/>" +
            $"<w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"{DocumentStyle.Hex(style.AccentColor, "BFBFBF")}\"/></w:pBdr>" +
            $"<w:ind w:left=\"360\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"{DocumentStyle.Hex(style.QuoteTextColor)}\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:customStyle=\"1\" w:styleId=\"CodeBlock\"><w:name w:val=\"Code Block\"/>" +
            "<w:basedOn w:val=\"Normal\"/><w:qFormat/>" +
            $"<w:pPr>{codeShading}<w:spacing w:after=\"160\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>" +
            $"<w:rPr>{codeFonts}<w:noProof/>{Size(code)}</w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:customStyle=\"1\" w:styleId=\"TableText\"><w:name w:val=\"Table Text\"/>" +
            "<w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:style>" +
            "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/>" +
            "<w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>" +
            "<w:style w:type=\"character\" w:customStyle=\"1\" w:styleId=\"InlineCode\"><w:name w:val=\"Inline Code\"/>" +
            $"<w:basedOn w:val=\"DefaultParagraphFont\"/><w:rPr>{codeFonts}<w:noProof/>{Size(code)}{codeShading}</w:rPr></w:style>" +
            "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/>" +
            "<w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/>" +
            $"<w:rPr><w:color w:val=\"{DocumentStyle.Hex(style.LinkColor)}\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
            "</w:styles>";
    }

    private string TablePropertiesXml()
    {
        string border = DocumentStyle.Hex(style.BorderColor, "BFBFBF");
        return "<w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>" +
            string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }
                .Select(side => $"<w:{side} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{border}\"/>")) +
            "</w:tblBorders><w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"100\" w:type=\"dxa\"/>" +
            "<w:bottom w:w=\"40\" w:type=\"dxa\"/><w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>";
    }
}
