using LandMark.Markdown;

namespace LandMark.Conversion;

/// <summary>
/// Renders Markdown as readable plain text: markup is removed, headings are underlined,
/// lists keep their bullets/numbers, and link URLs are kept in parentheses.
/// </summary>
public static class PlainTextConverter
{
    public static string Convert(string markdown) =>
        string.Join(Environment.NewLine, RenderBlocks(MarkdownParser.Parse(markdown), tight: false));

    private static List<string> RenderBlocks(IEnumerable<Block> blocks, bool tight)
    {
        var output = new List<string>();
        foreach (var block in blocks)
        {
            var lines = RenderBlock(block);
            if (lines.Count == 0) continue;
            if (output.Count > 0 && !tight) output.Add("");
            output.AddRange(lines);
        }
        return output;
    }

    private static List<string> RenderBlock(Block block) => block switch
    {
        Heading heading => RenderHeading(heading),
        Paragraph paragraph => paragraph.Content.ToPlainText().Split('\n').ToList(),
        CodeBlock code => code.Lines.Select(line => "    " + line).ToList(),
        BlockQuote quote => RenderBlocks(quote.Children, tight: false).Select(line => line.Length == 0 ? ">" : "> " + line).ToList(),
        ListBlock list => RenderList(list),
        Table table => RenderTable(table),
        ThematicBreak => [new string('-', 40)],
        _ => [],
    };

    private static List<string> RenderHeading(Heading heading)
    {
        var text = heading.Content.ToPlainText();
        return heading.Level switch
        {
            1 => [text, new string('=', Math.Max(3, text.Length))],
            2 => [text, new string('-', Math.Max(3, text.Length))],
            _ => [text],
        };
    }

    private static List<string> RenderList(ListBlock list)
    {
        var output = new List<string>();
        int number = list.Start;

        foreach (var item in list.Items)
        {
            string marker = list.Ordered ? $"{number++}." : "•";
            string indent = new(' ', marker.Length + 1);

            var lines = RenderBlocks(item.Children, tight: !list.Loose);
            if (lines.Count == 0) lines.Add("");
            if (item.Checked is bool done) lines[0] = (done ? "[x] " : "[ ] ") + lines[0];

            if (list.Loose && output.Count > 0) output.Add("");
            output.Add(marker + " " + lines[0]);
            output.AddRange(lines.Skip(1).Select(line => line.Length == 0 ? "" : indent + line));
        }
        return output;
    }

    private static List<string> RenderTable(Table table)
    {
        var rows = new[] { table.Header }.Concat(table.Rows)
            .Select(row => row.Cells.Select(cell => cell.ToPlainText().Replace('\n', ' ')).ToList())
            .ToList();
        var widths = Enumerable.Range(0, table.Alignments.Count)
            .Select(c => rows.Max(row => row[c].Length))
            .ToArray();

        var output = new List<string>();
        foreach (var row in rows)
        {
            output.Add(string.Join(" | ", row.Select((text, c) => Pad(text, widths[c], table.Alignments[c]))).TrimEnd());
            if (output.Count == 1)
                output.Add(string.Join("-+-", widths.Select(width => new string('-', width))));
        }
        return output;
    }

    private static string Pad(string text, int width, ColumnAlign align) => align switch
    {
        ColumnAlign.Right => text.PadLeft(width),
        ColumnAlign.Center => text.PadLeft((width + text.Length) / 2).PadRight(width),
        _ => text.PadRight(width),
    };
}
