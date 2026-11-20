using System.Text;

namespace LandMark.Markdown;

// ---- Blocks ----

public abstract record Block
{
    /// <summary>Zero-based line in the source where the block starts (-1 if unknown); used to sync the preview's scrolling.</summary>
    public int Line { get; init; } = -1;

    /// <summary>Number of source lines the block was parsed from (e.g. a paragraph written over three lines).</summary>
    public int LineCount { get; init; } = 1;
}

public sealed record Heading(int Level, IReadOnlyList<Inline> Content) : Block;

public sealed record Paragraph(IReadOnlyList<Inline> Content) : Block;

public sealed record CodeBlock(string Language, IReadOnlyList<string> Lines) : Block;

public sealed record BlockQuote(IReadOnlyList<Block> Children) : Block;

public sealed record ListBlock(bool Ordered, int Start, bool Loose, IReadOnlyList<ListItem> Items) : Block;

/// <summary>A list item. <see cref="Checked"/> is null for ordinary items, true/false for task items.</summary>
public sealed record ListItem(bool? Checked, IReadOnlyList<Block> Children);

public sealed record Table(IReadOnlyList<ColumnAlign> Alignments, TableRow Header, IReadOnlyList<TableRow> Rows) : Block;

/// <summary>A table row; always has exactly one cell per column.</summary>
public sealed record TableRow(IReadOnlyList<IReadOnlyList<Inline>> Cells);

public sealed record ThematicBreak : Block;

public enum ColumnAlign { None, Left, Center, Right }

// ---- Inlines ----

public abstract record Inline;

public sealed record Text(string Value) : Inline;

public sealed record Emphasis(EmphasisKind Kind, IReadOnlyList<Inline> Children) : Inline;

public sealed record CodeSpan(string Code) : Inline;

public sealed record Link(string Url, IReadOnlyList<Inline> Children) : Inline;

public sealed record Image(string Url, string Alt) : Inline;

public sealed record LineBreak : Inline;

public enum EmphasisKind { Italic, Bold, Strikethrough }

public static class InlineExtensions
{
    /// <summary>Flattens inline content to plain text. Links become "text (url)".</summary>
    public static string ToPlainText(this IEnumerable<Inline> inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Text t:
                    text.Append(t.Value);
                    break;
                case CodeSpan code:
                    text.Append(code.Code);
                    break;
                case LineBreak:
                    text.Append('\n');
                    break;
                case Emphasis emphasis:
                    text.Append(emphasis.Children.ToPlainText());
                    break;
                case Image image:
                    text.Append("[Image: ").Append(image.Alt).Append(']');
                    break;
                case Link link:
                    var label = link.Children.ToPlainText();
                    text.Append(label);
                    if (ShouldShowUrl(link.Url, label))
                        text.Append(" (").Append(link.Url).Append(')');
                    break;
            }
        }
        return text.ToString();
    }

    private static bool ShouldShowUrl(string url, string label) =>
        url.Length > 0
        && !url.StartsWith('#')
        && url != label
        && url != "mailto:" + label
        && url != "http://" + label;
}
