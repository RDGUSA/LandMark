using System.Text;
using System.Text.RegularExpressions;

namespace LandMark.Markdown;

/// <summary>
/// A small Markdown parser covering the CommonMark/GitHub features people actually use:
/// headings, paragraphs, nested and task lists, block quotes, fenced and indented code,
/// pipe tables, horizontal rules and reference links. It is deliberately forgiving rather
/// than spec-complete (e.g. nested lists indented by only two spaces under "1." still nest).
/// </summary>
public static partial class MarkdownParser
{
    public static IReadOnlyList<Block> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(ExpandTabs).ToList();
        var references = ExtractReferenceDefinitions(lines);
        return new BlockParser(new InlineParser(references)).ParseBlocks(lines, lineOffset: 0);
    }

    internal static string NormalizeLabel(string label) =>
        string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t')) return line;

        var expanded = new StringBuilder();
        foreach (char c in line)
        {
            if (c == '\t')
                expanded.Append(' ', 4 - expanded.Length % 4);
            else
                expanded.Append(c);
        }
        return expanded.ToString();
    }

    /// <summary>Collects "[label]: url" lines (outside code fences) and blanks them out.</summary>
    private static Dictionary<string, string> ExtractReferenceDefinitions(List<string> lines)
    {
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? openFence = null;

        for (int i = 0; i < lines.Count; i++)
        {
            if (openFence != null)
            {
                if (BlockParser.ClosesFence(lines[i], openFence)) openFence = null;
                continue;
            }
            if (BlockParser.TryMatchFence(lines[i], out var fence, out _))
            {
                openFence = fence;
                continue;
            }

            var definition = ReferenceDefinition().Match(lines[i]);
            if (definition.Success)
            {
                references.TryAdd(NormalizeLabel(definition.Groups["label"].Value), definition.Groups["url"].Value.Trim('<', '>'));
                lines[i] = "";
            }
        }
        return references;
    }

    [GeneratedRegex(@"^ {0,3}\[(?<label>[^\]^][^\]]*)\]:\s*(?<url>\S+)(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*$")]
    private static partial Regex ReferenceDefinition();
}

internal sealed partial class BlockParser(InlineParser inlines)
{
    private sealed record ListMarker(int Indent, bool Ordered, char Delimiter, int Number, int ContentIndent, string Content)
    {
        public bool SameListAs(ListMarker other) => Ordered == other.Ordered && Delimiter == other.Delimiter;
    }

    /// <param name="lineOffset">Source line of lines[0]. Quote and list item contents keep one entry per
    /// source line, so nested blocks get correct line numbers too.</param>
    public List<Block> ParseBlocks(IReadOnlyList<string> lines, int lineOffset)
    {
        var blocks = new List<Block>();
        int i = 0;

        while (i < lines.Count)
        {
            if (IsBlank(lines[i]))
            {
                i++;
                continue;
            }
            if (SkipHtmlComment(lines, ref i))
                continue;

            int start = i;
            var block = ParseFencedCode(lines, ref i)
                ?? ParseAtxHeading(lines, ref i)
                ?? ParseThematicBreak(lines, ref i)
                ?? ParseQuote(lines, ref i, lineOffset)
                ?? ParseList(lines, ref i, lineOffset)
                ?? ParseIndentedCode(lines, ref i)
                ?? ParseTable(lines, ref i)
                ?? ParseParagraph(lines, ref i);
            blocks.Add(block with { Line = lineOffset + start, LineCount = Math.Max(1, i - start) });
        }
        return blocks;
    }

    // ---- Code ----

    internal static bool TryMatchFence(string line, out string fence, out string info)
    {
        var match = FenceOpen().Match(line);
        fence = match.Groups["fence"].Value;
        info = match.Groups["info"].Value.Trim();
        // A backtick fence's info string may not itself contain backticks.
        return match.Success && !(fence[0] == '`' && info.Contains('`'));
    }

    internal static bool ClosesFence(string line, string fence)
    {
        var match = FenceClose().Match(line);
        return match.Success && match.Groups["fence"].Value[0] == fence[0] && match.Groups["fence"].Length >= fence.Length;
    }

    private static Block? ParseFencedCode(IReadOnlyList<string> lines, ref int i)
    {
        if (!TryMatchFence(lines[i], out var fence, out var info)) return null;

        int fenceIndent = LeadingSpaces(lines[i]);
        var code = new List<string>();
        i++;
        while (i < lines.Count && !ClosesFence(lines[i], fence))
        {
            code.Add(StripIndent(lines[i], fenceIndent));
            i++;
        }
        i++;   // closing fence (or end of document)

        string language = info.Split(' ', 2)[0];
        return new CodeBlock(language, code);
    }

    private static Block? ParseIndentedCode(IReadOnlyList<string> lines, ref int i)
    {
        if (LeadingSpaces(lines[i]) < 4) return null;

        var code = new List<string>();
        while (i < lines.Count && (IsBlank(lines[i]) || LeadingSpaces(lines[i]) >= 4))
        {
            code.Add(StripIndent(lines[i], 4));
            i++;
        }
        while (code.Count > 0 && IsBlank(code[^1]))
            code.RemoveAt(code.Count - 1);

        return new CodeBlock("", code);
    }

    // ---- Headings, rules, comments ----

    private Block? ParseAtxHeading(IReadOnlyList<string> lines, ref int i)
    {
        var match = AtxHeading().Match(lines[i]);
        if (!match.Success) return null;

        i++;
        return new Heading(match.Groups["level"].Length, inlines.Parse(match.Groups["text"].Value));
    }

    private static Block? ParseThematicBreak(IReadOnlyList<string> lines, ref int i)
    {
        if (!ThematicBreakLine().IsMatch(lines[i])) return null;

        i++;
        return new ThematicBreak();
    }

    private static bool SkipHtmlComment(IReadOnlyList<string> lines, ref int i)
    {
        if (!lines[i].TrimStart().StartsWith("<!--")) return false;

        while (i < lines.Count && !lines[i].Contains("-->"))
            i++;
        i++;
        return true;
    }

    // ---- Quotes and lists ----

    private Block? ParseQuote(IReadOnlyList<string> lines, ref int i, int lineOffset)
    {
        if (!QuoteMarker().IsMatch(lines[i])) return null;

        int first = i;
        var inner = new List<string>();
        while (i < lines.Count)
        {
            var marker = QuoteMarker().Match(lines[i]);
            if (marker.Success)
                inner.Add(lines[i][marker.Length..]);
            else if (!IsBlank(lines[i]) && inner.Count > 0 && !IsBlank(inner[^1]) && !StartsBlock(lines[i]))
                inner.Add(lines[i]);   // lazy continuation of a quoted paragraph
            else
                break;
            i++;
        }
        return new BlockQuote(ParseBlocks(inner, lineOffset + first));
    }

    private Block? ParseList(IReadOnlyList<string> lines, ref int i, int lineOffset)
    {
        var first = MatchListMarker(lines[i]);
        if (first == null || first.Indent > 3) return null;

        var items = new List<ListItem>();
        bool loose = false;

        while (i < lines.Count && MatchListMarker(lines[i]) is { } marker && marker.SameListAs(first) && !ThematicBreakLine().IsMatch(lines[i]))
        {
            int itemStart = i;
            var itemLines = new List<string> { marker.Content };
            i++;

            while (i < lines.Count)
            {
                string line = lines[i];
                if (IsBlank(line))
                {
                    itemLines.Add("");
                    i++;
                    continue;
                }

                int indent = LeadingSpaces(line);
                if (indent >= marker.ContentIndent)
                {
                    itemLines.Add(line[marker.ContentIndent..]);
                    i++;
                    continue;
                }

                var nested = MatchListMarker(line);
                if (nested != null && indent > marker.Indent)
                {
                    // Forgiving: a marker indented past the parent's marker nests, even if short of the content column.
                    itemLines.Add(line[indent..]);
                    i++;
                    continue;
                }

                bool afterBlank = itemLines[^1].Length == 0;
                if (afterBlank || nested != null || StartsBlock(line))
                    break;

                itemLines.Add(line.TrimStart());   // lazy paragraph continuation
                i++;
            }

            int trailingBlanks = 0;
            while (itemLines.Count > 0 && itemLines[^1].Length == 0)
            {
                itemLines.RemoveAt(itemLines.Count - 1);
                trailingBlanks++;
            }

            bool? isChecked = null;
            if (itemLines.Count > 0 && TaskMarker().Match(itemLines[0]) is { Success: true } task)
            {
                isChecked = task.Groups["state"].Value != " ";
                itemLines[0] = itemLines[0][task.Length..];
            }

            var children = ParseBlocks(itemLines, lineOffset + itemStart);
            items.Add(new ListItem(isChecked, children));

            bool nextIsSibling = i < lines.Count && MatchListMarker(lines[i]) is { } next && next.SameListAs(first);
            if ((trailingBlanks > 0 && nextIsSibling) || children.Count(child => child is Paragraph) > 1)
                loose = true;
        }

        return new ListBlock(first.Ordered, first.Number, loose, items);
    }

    private static ListMarker? MatchListMarker(string line)
    {
        var match = ListItemLine().Match(line);
        if (!match.Success) return null;

        string marker = match.Groups["marker"].Value;
        int indent = match.Groups["indent"].Length;
        int spaces = match.Groups["space"].Length;
        bool ordered = char.IsDigit(marker[0]);
        int contentIndent = indent + marker.Length + (spaces is 0 or > 4 ? 1 : spaces);

        return new ListMarker(
            indent,
            ordered,
            ordered ? marker[^1] : marker[0],
            ordered && int.TryParse(marker[..^1], out var number) ? number : 1,
            contentIndent,
            match.Groups["content"].Value);
    }

    // ---- Tables ----

    private Block? ParseTable(IReadOnlyList<string> lines, ref int i)
    {
        if (!IsTableStart(lines, i)) return null;

        var alignments = SplitRow(lines[i + 1]).Select(ParseAlignment).ToList();
        var header = ToRow(SplitRow(lines[i]), alignments.Count);
        i += 2;

        var rows = new List<TableRow>();
        while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|') && !StartsBlock(lines[i]))
        {
            rows.Add(ToRow(SplitRow(lines[i]), alignments.Count));
            i++;
        }
        return new Table(alignments, header, rows);
    }

    private static bool IsTableStart(IReadOnlyList<string> lines, int i) =>
        i + 1 < lines.Count
        && lines[i].Contains('|')
        && TableDelimiterRow().IsMatch(lines[i + 1])
        && SplitRow(lines[i]).Count == SplitRow(lines[i + 1]).Count;

    private TableRow ToRow(List<string> cells, int columns) =>
        new(Enumerable.Range(0, columns).Select(c => inlines.Parse(c < cells.Count ? cells[c] : "")).ToList());

    private static List<string> SplitRow(string line)
    {
        string row = line.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|")) row = row[..^1];

        var cells = new List<string>();
        var cell = new StringBuilder();
        for (int k = 0; k < row.Length; k++)
        {
            if (row[k] == '\\' && k + 1 < row.Length && row[k + 1] == '|')
            {
                cell.Append('|');
                k++;
            }
            else if (row[k] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(row[k]);
            }
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private static ColumnAlign ParseAlignment(string delimiter) =>
        (delimiter.StartsWith(':'), delimiter.EndsWith(':')) switch
        {
            (true, true) => ColumnAlign.Center,
            (false, true) => ColumnAlign.Right,
            (true, false) => ColumnAlign.Left,
            _ => ColumnAlign.None,
        };

    // ---- Paragraphs ----

    private Block ParseParagraph(IReadOnlyList<string> lines, ref int i)
    {
        var text = new List<string> { lines[i].TrimStart() };
        i++;

        while (i < lines.Count && !IsBlank(lines[i]))
        {
            var underline = SetextUnderline().Match(lines[i]);
            if (underline.Success)
            {
                i++;
                return new Heading(underline.Groups["char"].Value == "=" ? 1 : 2, inlines.Parse(string.Join("\n", text)));
            }
            if (StartsBlock(lines[i]) || IsTableStart(lines, i))
                break;

            text.Add(lines[i].TrimStart());
            i++;
        }
        return new Paragraph(inlines.Parse(string.Join("\n", text)));
    }

    /// <summary>True if the line starts a block that may interrupt a paragraph.</summary>
    private static bool StartsBlock(string line) =>
        TryMatchFence(line, out _, out _)
        || AtxHeading().IsMatch(line)
        || ThematicBreakLine().IsMatch(line)
        || QuoteMarker().IsMatch(line)
        || line.TrimStart().StartsWith("<!--")
        || (MatchListMarker(line) is { Indent: <= 3 } marker && marker.Content.Length > 0);

    // ---- Helpers ----

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static int LeadingSpaces(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }

    private static string StripIndent(string line, int maxSpaces) => line[Math.Min(maxSpaces, LeadingSpaces(line))..];

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"^ {0,3}(?<fence>`{3,}|~{3,}) *$")]
    private static partial Regex FenceClose();

    [GeneratedRegex(@"^ {0,3}(?<level>#{1,6})(?: +(?<text>.*?))?(?: +#+)? *$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(?:(?:\* *){3,}|(?:- *){3,}|(?:_ *){3,})$")]
    private static partial Regex ThematicBreakLine();

    [GeneratedRegex(@"^ {0,3}(?<char>=|-)\k<char>* *$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex(@"^ {0,3}> ?")]
    private static partial Regex QuoteMarker();

    [GeneratedRegex(@"^(?<indent> *)(?<marker>[-*+]|\d{1,9}[.)])(?<space> +|$)(?<content>.*)$")]
    private static partial Regex ListItemLine();

    [GeneratedRegex(@"^\[(?<state>[ xX])\](?: +|$)")]
    private static partial Regex TaskMarker();

    [GeneratedRegex(@"^ {0,3}\|? *:?-+:? *(?:\| *:?-+:? *)*\|? *$")]
    private static partial Regex TableDelimiterRow();
}
