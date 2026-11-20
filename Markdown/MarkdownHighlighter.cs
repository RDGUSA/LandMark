using System.Text.RegularExpressions;

namespace LandMark.Markdown;

/// <summary>The groups the editor colors. Related syntax shares a group (and so a color).</summary>
public enum SyntaxKind { Heading, ListMarker, Bold, Italic, Strikethrough, Code, Link, Url, Quote, Table, Rule, Html, Escape }

/// <summary>A colored range of the editor's text, in character offsets.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, SyntaxKind Kind);

/// <summary>
/// Finds the Markdown syntax in the editor's text for coloring. Works on the raw text (offsets index it directly,
/// "\r\n" included). A quick pass over every line tracks what spans lines (code fences, comments, setext
/// headings, tables); inline syntax is only worked out for the lines being drawn, and cached until the text changes.
/// Deliberately forgiving: it colors what the syntax looks like, without needing to agree with the parser exactly.
/// </summary>
public sealed partial class MarkdownHighlighter
{
    private enum LineKind : byte { Normal, Blank, Fence, FencedCode, IndentedCode, Comment, CommentEnd, SetextText, SetextUnderline, TableRow, TableDelimiter }

    private string _text = "";
    private int[] _lineStarts = [0];
    private LineKind[] _kinds = [LineKind.Blank];
    private readonly Dictionary<int, List<SyntaxSpan>> _cache = [];

    public int LineCount => _lineStarts.Length;

    public void SetText(string text)
    {
        _text = text;
        _cache.Clear();

        var starts = new List<int> { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
            starts.Add(i + 1);
        _lineStarts = [.. starts];
        _kinds = ClassifyLines();
    }

    /// <summary>The source line containing a character offset.</summary>
    public int LineFromOffset(int offset)
    {
        int index = Array.BinarySearch(_lineStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>A source line's spans, in drawing order: a later span's color wins where spans overlap.</summary>
    public IReadOnlyList<SyntaxSpan> SpansForLine(int line)
    {
        if (line < 0 || line >= _lineStarts.Length) return [];
        if (!_cache.TryGetValue(line, out var spans))
        {
            spans = [];
            HighlightLine(line, spans);
            _cache[line] = spans;
        }
        return spans;
    }

    private string LineText(int line)
    {
        int start = _lineStarts[line];
        int end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : _text.Length;   // minus the \n
        if (end > start && _text[end - 1] == '\r') end--;
        return _text[start..end];
    }

    // ---- Block pass: what each line is, given the lines around it ----

    private LineKind[] ClassifyLines()
    {
        var kinds = new LineKind[_lineStarts.Length];
        char fenceChar = '\0';
        int fenceLength = 0;
        bool inComment = false, inList = false, inTable = false;

        for (int i = 0; i < kinds.Length; i++)
        {
            string line = LineText(i);
            bool blank = string.IsNullOrWhiteSpace(line);
            LineKind previous = i > 0 ? kinds[i - 1] : LineKind.Blank;

            if (fenceChar != '\0')
            {
                // Fences inside list items are indented, so any indent closes (or opens) one here.
                var close = FenceLine().Match(line);
                if (close.Success && close.Groups["fence"].Value[0] == fenceChar && close.Groups["fence"].Length >= fenceLength
                    && close.Groups["info"].Value.Trim().Length == 0)
                {
                    kinds[i] = LineKind.Fence;
                    fenceChar = '\0';
                }
                else
                {
                    kinds[i] = LineKind.FencedCode;
                }
                continue;
            }
            if (inComment)
            {
                kinds[i] = line.Contains("-->") ? LineKind.CommentEnd : LineKind.Comment;
                inComment = kinds[i] == LineKind.Comment;
                continue;
            }
            if (blank)
            {
                kinds[i] = LineKind.Blank;
                inTable = false;
                continue;
            }

            var fence = FenceLine().Match(line);
            if (fence.Success && !(fence.Groups["fence"].Value[0] == '`' && fence.Groups["info"].Value.Contains('`')))
            {
                kinds[i] = LineKind.Fence;
                fenceChar = fence.Groups["fence"].Value[0];
                fenceLength = fence.Groups["fence"].Length;
                continue;
            }
            if (CommentStart().IsMatch(line) && !line.Contains("-->"))
            {
                kinds[i] = LineKind.Comment;
                inComment = true;
                continue;
            }

            bool listItem = ListItem().IsMatch(line);
            if (listItem) inList = true;
            else if (previous == LineKind.Blank && !line.StartsWith(' ') && !line.StartsWith('\t')) inList = false;

            // Indented code can't interrupt a paragraph, but can follow a heading or a rule directly.
            bool afterBlock = previous is LineKind.Blank or LineKind.IndentedCode or LineKind.Fence or LineKind.SetextUnderline
                || (previous == LineKind.Normal && LineText(i - 1) is var above && (AtxHeading().IsMatch(above) || ThematicBreak().IsMatch(above)));
            if (!listItem && !inList && afterBlock && IndentedCodeLine().IsMatch(line))
            {
                kinds[i] = LineKind.IndentedCode;
                continue;
            }

            // "Text" over "===" or "---" is a heading; "---" after a blank line is a rule (handled per line).
            if (previous == LineKind.Normal && SetextUnderline().IsMatch(line) && IsParagraphText(LineText(i - 1)))
            {
                kinds[i - 1] = LineKind.SetextText;
                kinds[i] = LineKind.SetextUnderline;
                continue;
            }

            if (line.Contains('|'))
            {
                if (previous == LineKind.Normal && TableDelimiterRow().IsMatch(line) && LineText(i - 1).Contains('|'))
                {
                    kinds[i - 1] = LineKind.TableRow;
                    kinds[i] = LineKind.TableDelimiter;
                    inTable = true;
                    continue;
                }
                if (inTable)
                {
                    kinds[i] = LineKind.TableRow;
                    continue;
                }
            }
            inTable = false;
            kinds[i] = LineKind.Normal;
        }
        return kinds;
    }

    /// <summary>A line that could be a paragraph, so a following "---"/"===" underlines it.</summary>
    private static bool IsParagraphText(string line) =>
        !ListItem().IsMatch(line) && QuoteMarker().Match(line) is not { Success: true, Index: 0 }
        && !AtxHeading().IsMatch(line) && !ThematicBreak().IsMatch(line);

    // ---- Line pass ----

    private void HighlightLine(int line, List<SyntaxSpan> spans)
    {
        string text = LineText(line);
        int offset = _lineStarts[line];
        if (text.Length == 0) return;

        switch (_kinds[line])
        {
            case LineKind.Fence or LineKind.FencedCode or LineKind.IndentedCode:
                spans.Add(new(offset, text.Length, SyntaxKind.Code));
                return;
            case LineKind.Comment:
                spans.Add(new(offset, text.Length, SyntaxKind.Html));
                return;
            case LineKind.CommentEnd:
                int end = text.IndexOf("-->", StringComparison.Ordinal) + 3;
                spans.Add(new(offset, end, SyntaxKind.Html));
                Inline(text, end, text.Length, offset, spans);
                return;
            case LineKind.SetextUnderline:
            case LineKind.TableDelimiter:
                spans.Add(new(offset, text.Length, _kinds[line] == LineKind.SetextUnderline ? SyntaxKind.Heading : SyntaxKind.Table));
                return;
            case LineKind.SetextText:
                spans.Add(new(offset, text.Length, SyntaxKind.Heading));
                Inline(text, 0, text.Length, offset, spans);
                return;
            case LineKind.TableRow:
                TableRow(text, offset, spans);
                return;
        }

        // Containers first: quote markers ("> > "), then a list marker and task box, then the line's own syntax.
        int pos = 0;
        bool quoted = false;
        while (QuoteMarker().Match(text, pos, text.Length - pos) is { Success: true } quote && quote.Index == pos)
        {
            spans.Add(new(offset + pos, quote.Length, SyntaxKind.Quote));
            pos += quote.Length;
            quoted = true;
        }
        if (quoted && pos < text.Length)
            spans.Add(new(offset + pos, text.Length - pos, SyntaxKind.Quote));   // the quoted text; inline syntax colors over it

        string rest = text[pos..];
        if (ThematicBreak().IsMatch(rest))
        {
            spans.Add(new(offset + pos, rest.Length, SyntaxKind.Rule));
            return;
        }

        if (ListItem().Match(rest) is { Success: true } item)
        {
            var marker = item.Groups["marker"];
            spans.Add(new(offset + pos + marker.Index, marker.Length, SyntaxKind.ListMarker));
            int content = item.Groups["content"].Index;
            if (TaskBox().Match(rest, content) is { Success: true } task && task.Index == content)
            {
                spans.Add(new(offset + pos + content, 3, SyntaxKind.ListMarker));
                content += 3;
            }
            pos += content;
            rest = text[pos..];
        }

        if (AtxHeading().Match(rest) is { Success: true } heading)
        {
            spans.Add(new(offset + pos, rest.Length, SyntaxKind.Heading));
            int textStart = heading.Groups["text"].Success ? heading.Groups["text"].Index : rest.Length;
            int textEnd = heading.Groups["text"].Success ? textStart + heading.Groups["text"].Length : rest.Length;
            Inline(text, pos + textStart, pos + textEnd, offset, spans);
            return;
        }

        if (pos == 0 && ReferenceDefinition().Match(text) is { Success: true } reference)
        {
            spans.Add(new(offset + reference.Groups["label"].Index, reference.Groups["label"].Length, SyntaxKind.Link));
            spans.Add(new(offset + reference.Groups["url"].Index, text.Length - reference.Groups["url"].Index, SyntaxKind.Url));
            return;
        }

        Inline(text, pos, text.Length, offset, spans);
    }

    private void TableRow(string text, int offset, List<SyntaxSpan> spans)
    {
        // Pipes are table syntax unless escaped or inside code; the cells between them get inline coloring.
        var masked = Inline(text, 0, text.Length, offset, spans);
        for (int i = 0; i < masked.Length; i++)
        {
            if (masked[i] == '|')
                spans.Add(new(offset + i, 1, SyntaxKind.Table));
        }
    }

    // ---- Inline syntax ----

    private const char Masked = '\u0001';

    /// <summary>
    /// Colors inline syntax in text[from..to]. Code, HTML, URLs, escapes and link punctuation are found first,
    /// left to right; emphasis is then matched on a copy with those blanked out, so a "*" inside code or a URL
    /// isn't taken for emphasis. Returns that copy.
    /// </summary>
    private static char[] Inline(string text, int from, int to, int offset, List<SyntaxSpan> spans)
    {
        var masked = text.ToCharArray();
        var atoms = new List<SyntaxSpan>();
        Atoms(text, from, to, masked, atoms);

        string scan = new(masked, from, to - from);
        Emphasis(TripleEmphasis(), scan, from, offset, spans, SyntaxKind.Bold, SyntaxKind.Italic);
        Emphasis(StrongStars(), scan, from, offset, spans, SyntaxKind.Bold);
        Emphasis(StrongUnderscores(), scan, from, offset, spans, SyntaxKind.Bold);
        Emphasis(EmphasisStars(), scan, from, offset, spans, SyntaxKind.Italic);
        Emphasis(EmphasisUnderscores(), scan, from, offset, spans, SyntaxKind.Italic);
        Emphasis(Strikethrough(), scan, from, offset, spans, SyntaxKind.Strikethrough);

        foreach (var atom in atoms)
            spans.Add(atom with { Start = atom.Start + offset });
        return masked;
    }

    private static void Emphasis(Regex pattern, string scan, int from, int offset, List<SyntaxSpan> spans, params SyntaxKind[] kinds)
    {
        foreach (Match match in pattern.Matches(scan))
        {
            foreach (var kind in kinds)
                spans.Add(new(offset + from + match.Index, match.Length, kind));
        }
    }

    /// <summary>Finds code spans, HTML, autolinks, links, bare URLs and escapes in text[from..to] (line-relative spans).</summary>
    private static void Atoms(string text, int from, int to, char[] masked, List<SyntaxSpan> atoms)
    {
        int i = from;
        while (i < to)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < to && text[i + 1] < 128 && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1])))
            {
                Add(i, 2, SyntaxKind.Escape);
                i += 2;
            }
            else if (c == '`')
            {
                int run = 1;
                while (i + run < to && text[i + run] == '`') run++;
                int close = FindBacktickRun(text, i + run, to, run);
                if (close < 0)
                {
                    i += run;   // no closing run: literal backticks
                    continue;
                }
                Add(i, close + run - i, SyntaxKind.Code);
                i = close + run;
            }
            else if (c == '<' && Tag(text, i, to) is { } tag)
            {
                Add(i, tag.Length, tag.Kind);
                i += tag.Length;
            }
            else if ((c == '[' || (c == '!' && i + 1 < to && text[i + 1] == '[')) && Link(text, i, to, masked, atoms) is int next)
            {
                i = next;
            }
            else if ((c == 'h' || c == 'w') && (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                     && BareUrl().Match(text, i, to - i) is { Success: true } url && url.Index == i)
            {
                int length = url.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '\'', '"').Length;
                Add(i, length, SyntaxKind.Url);
                i += length;
            }
            else
            {
                i++;
            }
        }

        void Add(int start, int length, SyntaxKind kind)
        {
            atoms.Add(new(start, length, kind));
            Array.Fill(masked, Masked, start, length);
        }
    }

    private static int FindBacktickRun(string text, int start, int to, int run)
    {
        for (int i = start; i < to; i++)
        {
            if (text[i] != '`') continue;
            int length = 1;
            while (i + length < to && text[i + length] == '`') length++;
            if (length == run) return i;
            i += length - 1;
        }
        return -1;
    }

    private static (int Length, SyntaxKind Kind)? Tag(string text, int i, int to)
    {
        if (i + 4 <= to && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
        {
            int end = text.IndexOf("-->", i + 4, to - i - 4, StringComparison.Ordinal);
            return (end < 0 ? to - i : end + 3 - i, SyntaxKind.Html);
        }
        if (AutoLink().Match(text, i, to - i) is { Success: true } link && link.Index == i)
            return (link.Length, SyntaxKind.Url);
        if (HtmlTag().Match(text, i, to - i) is { Success: true } tag && tag.Index == i)
            return (tag.Length, SyntaxKind.Html);
        return null;
    }

    /// <summary>
    /// [text](url), ![alt](url), [text][ref] and [text][]: the brackets and text are Link, the target Url. The text
    /// inside is scanned too, so code or an escape in a link is still colored. Returns the index after the link.
    /// </summary>
    private static int? Link(string text, int i, int to, char[] masked, List<SyntaxSpan> atoms)
    {
        int open = text[i] == '!' ? i + 1 : i;
        int close = MatchingBracket(text, open, to);
        if (close < 0 || close + 1 >= to) return null;

        int targetEnd;
        if (text[close + 1] == '(')
        {
            int depth = 0;
            targetEnd = -1;
            for (int j = close + 1; j < to; j++)
            {
                if (text[j] == '\\') { j++; continue; }
                if (text[j] == '(') depth++;
                else if (text[j] == ')' && --depth == 0) { targetEnd = j + 1; break; }
            }
        }
        else if (text[close + 1] == '[')
        {
            int end = text.IndexOf(']', close + 2, to - close - 2);
            targetEnd = end < 0 ? -1 : end + 1;
        }
        else
        {
            return null;   // "[text]" alone: a shortcut reference or just brackets; can't tell
        }
        if (targetEnd < 0) return null;

        atoms.Add(new(i, close + 1 - i, SyntaxKind.Link));
        Array.Fill(masked, Masked, i, open + 1 - i);   // "[" or "!["
        masked[close] = Masked;
        Atoms(text, open + 1, close, masked, atoms);   // the link text keeps its own code/escapes (emphasis is matched later)
        atoms.Add(new(close + 1, targetEnd - close - 1, SyntaxKind.Url));
        Array.Fill(masked, Masked, close + 1, targetEnd - close - 1);
        return targetEnd;
    }

    private static int MatchingBracket(string text, int open, int to)
    {
        int depth = 0;
        for (int j = open; j < to; j++)
        {
            char c = text[j];
            if (c == '\\') { j++; continue; }
            if (c == '`')
            {
                int run = 1;
                while (j + run < to && text[j + run] == '`') run++;
                int close = FindBacktickRun(text, j + run, to, run);
                j = close < 0 ? j + run - 1 : close + run - 1;
                continue;
            }
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return j;
        }
        return -1;
    }

    // ---- Patterns ----

    [GeneratedRegex(@"^\s*(?<fence>`{3,}|~{3,})(?<info>.*)$")]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^ {0,3}<!--")]
    private static partial Regex CommentStart();

    [GeneratedRegex(@"^(?: {4}|\t)")]
    private static partial Regex IndentedCodeLine();

    [GeneratedRegex(@"^ {0,3}(?:=+|-+) *$")]
    private static partial Regex SetextUnderline();

    [GeneratedRegex(@"^ {0,3}\|? *:?-+:? *(?:\| *:?-+:? *)*\|? *$")]
    private static partial Regex TableDelimiterRow();

    [GeneratedRegex(@"^ {0,3}(?:(?:\* *){3,}|(?:- *){3,}|(?:_ *){3,})$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@" {0,3}> ?")]
    private static partial Regex QuoteMarker();

    [GeneratedRegex(@"^(?<indent>\s*)(?<marker>[-*+]|\d{1,9}[.)])(?: +|$)(?<content>)")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\[[ xX]\](?= |$)")]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"^ {0,3}#{1,6}(?: +(?<text>.*?))?(?: +#+)? *$")]
    private static partial Regex AtxHeading();

    [GeneratedRegex(@"^ {0,3}(?<label>\[[^\]^][^\]]*\]):\s*(?<url>\S.*)$")]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"<(?:[A-Za-z][A-Za-z0-9+.-]{1,31}:[^<>\s]*|[^<>\s@]+@[^<>\s@]+\.[^<>\s@]+)>")]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(?:\s+[^<>]*)?/?>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>]+")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"(?<![*\\])\*\*\*(?![\s*])(.+?)(?<![\s*\\])\*\*\*(?!\*)")]
    private static partial Regex TripleEmphasis();

    [GeneratedRegex(@"(?<![*\\])\*\*(?![\s*])(.+?)(?<![\s\\])\*\*")]
    private static partial Regex StrongStars();

    [GeneratedRegex(@"(?<![\w\\])__(?![\s_])(.+?)(?<![\s\\])__(?!\w)")]
    private static partial Regex StrongUnderscores();

    [GeneratedRegex(@"(?<![*\\])\*(?![\s*])(.+?)(?<![\s*\\])\*(?!\*)")]
    private static partial Regex EmphasisStars();

    [GeneratedRegex(@"(?<![\w\\_])_(?![\s_])(.+?)(?<![\s\\_])_(?![\w_])")]
    private static partial Regex EmphasisUnderscores();

    [GeneratedRegex(@"(?<![~\\])~~(?![\s~])(.+?)(?<![\s\\])~~(?!~)")]
    private static partial Regex Strikethrough();
}
