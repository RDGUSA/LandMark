namespace LandMark.Markdown;

/// <summary>A heading in the outline panel. <see cref="Line"/> is its zero-based source line.</summary>
public sealed record OutlineEntry(int Level, string Title, int Line);

public static class DocumentOutline
{
    /// <summary>Every heading, in document order, including those inside quotes and list items.</summary>
    public static List<OutlineEntry> Build(IEnumerable<Block> blocks)
    {
        var entries = new List<OutlineEntry>();
        Collect(blocks, entries);
        return entries;
    }

    private static void Collect(IEnumerable<Block> blocks, List<OutlineEntry> entries)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                    string title = heading.Content.ToPlainText().Replace('\n', ' ').Trim();
                    entries.Add(new(heading.Level, title.Length > 0 ? title : "(untitled)", heading.Line));
                    break;
                case BlockQuote quote:
                    Collect(quote.Children, entries);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                        Collect(item.Children, entries);
                    break;
            }
        }
    }
}
