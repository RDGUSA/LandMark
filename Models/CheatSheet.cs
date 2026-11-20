namespace LandMark.Models;

/// <summary>
/// One cheat-sheet example. <see cref="Template"/> is inserted into the editor with {0} replaced by
/// the selected text (or <see cref="Placeholder"/>); block entries always start on a new line.
/// </summary>
public sealed record CheatSheetEntry(string Syntax, string Meaning, string Template, string Placeholder = "", bool Block = false)
{
    public bool CanInsert => Template.Length > 0;

    /// <summary>For line-start syntax ("## ", "- ", "> "...), the prefix to apply to existing lines; otherwise null.</summary>
    public string? LinePrefix => Block && Template.EndsWith("{0}") && !Template.Contains('\n') ? Template[..^3] : null;
}

public sealed record CheatSheetGroup(string Name, IReadOnlyList<CheatSheetEntry> Entries);

public static class CheatSheet
{
    public static IReadOnlyList<CheatSheetGroup> Groups { get; } =
    [
        new("Headings",
        [
            new("# Heading 1", "Largest heading, for the title. One # at the start of the line, then a space.", "# {0}", "Heading", Block: true),
            new("## Heading 2", "Section heading. Each extra # makes it one level smaller.", "## {0}", "Heading", Block: true),
            new("### Heading 3", "Subsection heading. Goes down to ###### (level 6).", "### {0}", "Heading", Block: true),
        ]),
        new("Emphasis",
        [
            new("**bold**", "Bold. Two asterisks on each side (or __two underscores__).", "**{0}**", "bold text"),
            new("*italic*", "Italic. One asterisk on each side (or _one underscore_).", "*{0}*", "italic text"),
            new("***bold italic***", "Bold and italic. Three asterisks on each side.", "***{0}***", "bold italic text"),
            new("~~strikethrough~~", "Crossed-out text. Two tildes on each side.", "~~{0}~~", "struck text"),
            new("`code`", "Inline code in a monospace font. Uses the backtick ` (the key left of 1), not the apostrophe '.", "`{0}`", "code"),
        ]),
        new("Lists",
        [
            new("- item", "Bullet list. A dash (or * or +) and a space at the start of each line.", "- {0}", "Item", Block: true),
            new("1. item", "Numbered list. The list counts up from the first number.", "1. {0}", "Item", Block: true),
            new("  - nested item", "Indent two or more spaces under an item to nest a list inside it.", "  - {0}", "Nested item", Block: true),
            new("- [ ] to do\n- [x] done", "Checklist. [ ] is an open box and [x] is checked.", "- [ ] {0}", "Task", Block: true),
        ]),
        new("Links and images",
        [
            new("[text](https://…)", "Link. The words to show in square brackets, then the address in parentheses.", "[{0}](https://)", "link text"),
            new("https://example.com", "A web address on its own becomes a link automatically (so does <https://…>).", "https://{0}", "example.com"),
            new("![description](image.png)", "Image. Like a link with ! in front; the description is shown if the image can't be.", "![{0}](image.png)", "description"),
        ]),
        new("Blocks",
        [
            new("> quoted text", "Block quote. Shown indented with a colored bar.", "> {0}", "Quoted text", Block: true),
            new("```\ncode block\n```", "Code block. Three backticks on the lines before and after.", "```\n{0}\n```", "code", Block: true),
            new("---", "Horizontal rule, on a line by itself with a blank line above. Directly under text it makes that text a Heading 2.", "\n---\n", Block: true),
        ]),
        new("Tables",
        [
            new("| A | B |\n|---|---|\n| 1 | 2 |", "Table. Pipes separate the columns; the dashes line marks the header row.",
                "| {0} | Column 2 |\n|---|---|\n| Cell | Cell |", "Column 1", Block: true),
            new("|:---|:---:|---:|", "In the dashes line, a colon on the left aligns left, both sides centers, the right side aligns right.", ""),
        ]),
        new("Line breaks and symbols",
        [
            new("(blank line)", "Starts a new paragraph. A single line break inside a paragraph is joined into one line.", ""),
            new("line ending in two spaces", "Two spaces (or a \\) at the end of a line forces a line break without a new paragraph.", "{0}  \n", "Line"),
            new("\\*", "A backslash before a symbol shows the symbol itself instead of formatting, e.g. \\* or \\#.", "\\{0}", "*"),
        ]),
    ];
}
