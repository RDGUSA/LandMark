using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LandMark.Markdown;

/// <summary>
/// Parses inline Markdown: emphasis (CommonMark delimiter-run rules), code spans, links and
/// images (inline and reference), autolinks, bare URLs, entities and line breaks.
/// Inline HTML tags are dropped, except &lt;br&gt; which becomes a line break.
/// </summary>
internal sealed partial class InlineParser(IReadOnlyDictionary<string, string> references)
{
    private const string AsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>A run of *, _ or ~ that may open or close emphasis.</summary>
    private sealed class Delimiter(char character, int count, bool canOpen, bool canClose)
    {
        public char Character { get; } = character;
        public int OriginalCount { get; } = count;
        public int Count { get; set; } = count;
        public bool CanOpen { get; } = canOpen;
        public bool CanClose { get; } = canClose;
    }

    public IReadOnlyList<Inline> Parse(string text)
    {
        var inlines = Parse(text, insideLink: false);
        if (inlines.Count > 0 && inlines[^1] is Text last)
            inlines[^1] = new Text(last.Value.TrimEnd());
        return inlines;
    }

    private List<Inline> Parse(string text, bool insideLink)
    {
        var nodes = new List<object>();   // Inline or Delimiter
        var literal = new StringBuilder();
        string label, url;
        int end;
        Match match;

        void Add(object node)
        {
            if (literal.Length > 0)
            {
                nodes.Add(new Text(literal.ToString()));
                literal.Clear();
            }
            nodes.Add(node);
        }

        int pos = 0;
        while (pos < text.Length)
        {
            char c = text[pos];

            if (c == '\\' && pos + 1 < text.Length && text[pos + 1] == '\n')
            {
                Add(new LineBreak());
                pos += 2;
            }
            else if (c == '\\' && pos + 1 < text.Length && AsciiPunctuation.Contains(text[pos + 1]))
            {
                literal.Append(text[pos + 1]);
                pos += 2;
            }
            else if (c == '\n')
            {
                // Two trailing spaces make a hard break; otherwise the newline is a soft break (a space).
                int spaces = 0;
                while (spaces < literal.Length && literal[literal.Length - 1 - spaces] == ' ') spaces++;
                literal.Length -= spaces;
                if (spaces >= 2) Add(new LineBreak());
                else literal.Append(' ');

                pos++;
                while (pos < text.Length && text[pos] == ' ') pos++;
            }
            else if (c == '`')
            {
                int run = RunLength(text, pos, '`');
                int close = FindBacktickRun(text, pos + run, run);
                if (close < 0)
                {
                    literal.Append('`', run);
                    pos += run;
                }
                else
                {
                    Add(new CodeSpan(CodeSpanContent(text[(pos + run)..close])));
                    pos = close + run;
                }
            }
            else if (c == '!' && pos + 1 < text.Length && text[pos + 1] == '[' && TryParseLink(text, pos + 1, out label, out url, out end))
            {
                Add(new Image(url, Parse(label, insideLink: true).ToPlainText()));
                pos = end;
            }
            else if (c == '[' && !insideLink && TryParseLink(text, pos, out label, out url, out end))
            {
                Add(new Link(url, Parse(label, insideLink: true)));
                pos = end;
            }
            else if (c == '<' && !insideLink && (match = AngleAutolink().Match(text, pos)).Success)
            {
                bool isEmail = match.Groups["email"].Success;
                string target = isEmail ? match.Groups["email"].Value : match.Groups["url"].Value;
                Add(new Link(isEmail ? "mailto:" + target : target, [new Text(target)]));
                pos += match.Length;
            }
            else if (c == '<' && (match = HtmlTag().Match(text, pos)).Success)
            {
                if (LineBreakTag().IsMatch(match.Value)) Add(new LineBreak());
                pos += match.Length;
            }
            else if (c == '&' && (match = Entity().Match(text, pos)).Success)
            {
                literal.Append(WebUtility.HtmlDecode(match.Value));
                pos += match.Length;
            }
            else if ((c is 'h' or 'w') && !insideLink && AtWordStart(text, pos) && (match = BareUrl().Match(text, pos)).Success)
            {
                string shown = TrimUnbalancedParens(match.Value);
                Add(new Link(shown.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + shown : shown, [new Text(shown)]));
                pos += shown.Length;
            }
            else if (c is '*' or '_' or '~')
            {
                int run = RunLength(text, pos, c);
                Add(MakeDelimiter(text, pos, run));
                pos += run;
            }
            else
            {
                literal.Append(c);
                pos++;
            }
        }

        if (literal.Length > 0)
            nodes.Add(new Text(literal.ToString()));

        ProcessEmphasis(nodes);
        return ToInlines(nodes);
    }

    // ---- Emphasis ----

    private static Delimiter MakeDelimiter(string text, int pos, int run)
    {
        char c = text[pos];
        char before = pos > 0 ? text[pos - 1] : ' ';
        char after = pos + run < text.Length ? text[pos + run] : ' ';

        bool leftFlanking = !char.IsWhiteSpace(after) && (!IsPunctuation(after) || char.IsWhiteSpace(before) || IsPunctuation(before));
        bool rightFlanking = !char.IsWhiteSpace(before) && (!IsPunctuation(before) || char.IsWhiteSpace(after) || IsPunctuation(after));

        return c switch
        {
            // Underscores inside words (snake_case) never emphasize.
            '_' => new Delimiter(c, run, leftFlanking && (!rightFlanking || IsPunctuation(before)), rightFlanking && (!leftFlanking || IsPunctuation(after))),
            '~' when run > 2 => new Delimiter(c, run, false, false),
            _ => new Delimiter(c, run, leftFlanking, rightFlanking),
        };
    }

    /// <summary>Matches closers with the nearest compatible opener, wrapping what lies between.</summary>
    private static void ProcessEmphasis(List<object> nodes)
    {
        for (int closerIndex = 0; closerIndex < nodes.Count; closerIndex++)
        {
            if (nodes[closerIndex] is not Delimiter { CanClose: true } closer) continue;

            while (closer.Count > 0)
            {
                int openerIndex = FindOpener(nodes, closerIndex, closer);
                if (openerIndex < 0) break;

                var opener = (Delimiter)nodes[openerIndex];
                int use = closer.Character == '~' ? closer.Count : (opener.Count >= 2 && closer.Count >= 2 ? 2 : 1);
                var kind = closer.Character == '~' ? EmphasisKind.Strikethrough
                    : use == 2 ? EmphasisKind.Bold
                    : EmphasisKind.Italic;

                var inner = nodes.GetRange(openerIndex + 1, closerIndex - openerIndex - 1);
                nodes.RemoveRange(openerIndex + 1, inner.Count);
                nodes.Insert(openerIndex + 1, new Emphasis(kind, ToInlines(inner)));
                closerIndex = openerIndex + 2;

                opener.Count -= use;
                closer.Count -= use;
                if (opener.Count == 0)
                {
                    nodes.RemoveAt(openerIndex);
                    closerIndex--;
                }
            }

            if (closer.Count == 0)
            {
                nodes.RemoveAt(closerIndex);
                closerIndex--;
            }
        }
    }

    private static int FindOpener(List<object> nodes, int closerIndex, Delimiter closer)
    {
        for (int k = closerIndex - 1; k >= 0; k--)
        {
            if (nodes[k] is not Delimiter { CanOpen: true } opener || opener.Character != closer.Character || opener.Count == 0)
                continue;

            if (closer.Character == '~')
            {
                if (opener.Count == closer.Count) return k;
                continue;
            }

            // CommonMark "rule of 3": stops e.g. *foo**bar* from pairing the wrong runs.
            bool ruleOfThree = (opener.CanClose || closer.CanOpen)
                && (opener.OriginalCount + closer.OriginalCount) % 3 == 0
                && !(opener.OriginalCount % 3 == 0 && closer.OriginalCount % 3 == 0);
            if (!ruleOfThree) return k;
        }
        return -1;
    }

    /// <summary>Unmatched delimiters become literal text; adjacent text nodes are merged.</summary>
    private static List<Inline> ToInlines(IEnumerable<object> nodes)
    {
        var result = new List<Inline>();
        foreach (var node in nodes)
        {
            var inline = node switch
            {
                Delimiter { Count: > 0 } d => new Text(new string(d.Character, d.Count)),
                Delimiter => null,
                _ => (Inline)node,
            };
            if (inline == null) continue;

            if (inline is Text text && result.Count > 0 && result[^1] is Text previous)
                result[^1] = new Text(previous.Value + text.Value);
            else
                result.Add(inline);
        }
        return result;
    }

    // ---- Links ----

    private bool TryParseLink(string text, int open, out string label, out string url, out int end)
    {
        label = url = "";
        end = 0;

        int close = FindClosingBracket(text, open);
        if (close < 0) return false;

        label = text[(open + 1)..close];
        int pos = close + 1;

        if (pos < text.Length && text[pos] == '(' && TryParseDestination(text, pos, out url, out end))
            return true;

        // Reference forms: [text][ref], [text][] and [text]
        string key = label;
        if (pos < text.Length && text[pos] == '[')
        {
            int refClose = FindClosingBracket(text, pos);
            if (refClose > 0)
            {
                if (refClose > pos + 1) key = text[(pos + 1)..refClose];
                pos = refClose + 1;
            }
        }

        if (!references.TryGetValue(MarkdownParser.NormalizeLabel(key), out var target))
            return false;

        url = target;
        end = pos;
        return true;
    }

    private static int FindClosingBracket(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\\':
                    i++;
                    break;
                case '`':
                    int run = RunLength(text, i, '`');
                    int close = FindBacktickRun(text, i + run, run);
                    i = (close >= 0 ? close + run : i + run) - 1;
                    break;
                case '[':
                    depth++;
                    break;
                case ']':
                    if (--depth == 0) return i;
                    break;
            }
        }
        return -1;
    }

    /// <summary>Parses "(url "optional title")" starting at the opening parenthesis.</summary>
    private static bool TryParseDestination(string text, int open, out string url, out int end)
    {
        url = "";
        end = 0;
        int pos = SkipWhitespace(text, open + 1);

        if (pos < text.Length && text[pos] == '<')
        {
            int close = text.IndexOf('>', pos);
            if (close < 0) return false;
            url = text[(pos + 1)..close];
            pos = close + 1;
        }
        else
        {
            int start = pos, depth = 0;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]))
            {
                char c = text[pos];
                if (c == '\\' && pos + 1 < text.Length)
                {
                    pos += 2;
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')' && depth-- == 0) break;
                pos++;
            }
            url = Unescape(text[start..pos]);
        }

        pos = SkipWhitespace(text, pos);
        if (pos < text.Length && text[pos] is '"' or '\'' or '(')
        {
            char closing = text[pos] == '(' ? ')' : text[pos];
            int close = text.IndexOf(closing, pos + 1);
            if (close < 0) return false;
            pos = SkipWhitespace(text, close + 1);
        }

        if (pos >= text.Length || text[pos] != ')') return false;
        end = pos + 1;
        return true;
    }

    // ---- Helpers ----

    private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool AtWordStart(string text, int pos) =>
        pos == 0 || char.IsWhiteSpace(text[pos - 1]) || text[pos - 1] is '(' or '*' or '_' or '~' or '"' or '\'';

    private static int RunLength(string text, int pos, char c)
    {
        int end = pos;
        while (end < text.Length && text[end] == c) end++;
        return end - pos;
    }

    private static int FindBacktickRun(string text, int from, int length)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != '`') continue;
            int run = RunLength(text, i, '`');
            if (run == length) return i;
            i += run - 1;
        }
        return -1;
    }

    private static string CodeSpanContent(string content)
    {
        content = content.Replace('\n', ' ');
        bool padded = content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length > 0;
        return padded ? content[1..^1] : content;
    }

    private static int SkipWhitespace(string text, int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        return pos;
    }

    private static string Unescape(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && AsciiPunctuation.Contains(text[i + 1])) i++;
            result.Append(text[i]);
        }
        return result.ToString();
    }

    private static string TrimUnbalancedParens(string url)
    {
        while (url.EndsWith(')') && url.Count(c => c == '(') < url.Count(c => c == ')'))
            url = url[..^1];
        return url;
    }

    [GeneratedRegex(@"\G<(?:(?<url>[A-Za-z][A-Za-z0-9+.\-]{1,31}:[^\s<>]*)|(?<email>[A-Za-z0-9.!#$%&'*+/=?^_`{|}~\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+))>")]
    private static partial Regex AngleAutolink();

    [GeneratedRegex(@"\G(?:<!--[\s\S]*?-->|</?[A-Za-z][A-Za-z0-9\-]*(?:\s[^<>]*)?/?>)")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"\G&(?:#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex Entity();

    [GeneratedRegex(@"\G(?:https?://|www\.)[^\s<]*[^\s<?!.,:;*_~'""]", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
