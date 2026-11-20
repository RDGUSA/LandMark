using System.Text;
using LandMark.Markdown;
using LandMark.Styling;

namespace LandMark.Conversion;

/// <summary>
/// Renders Markdown as HTML with inline styles taken from a <see cref="DocumentStyle"/>.
/// Email clients (Outlook in particular) drop &lt;style&gt; blocks, so every element carries
/// its own style attribute. Output is pure ASCII: anything else is a numeric character reference.
/// </summary>
public static class EmailHtml
{
    /// <summary>An HTML fragment (no &lt;html&gt;/&lt;body&gt;) ready to paste into an email.</summary>
    /// <param name="sourceLines">Tag blocks with data-line attributes (for the preview's scroll sync, not for email).</param>
    public static string Fragment(string markdown, DocumentStyle style, bool sourceLines = false) =>
        Fragment(MarkdownParser.Parse(markdown), style, sourceLines);

    /// <summary>The same, from an already-parsed document (the preview shares its parse with the outline).</summary>
    /// <param name="localImage">
    /// Turns a local image path (anything but an http/https URL) into a URL the page can load, or null if it can't.
    /// Without it, local images become an "[Image: alt]" placeholder, since they mean nothing to an email's recipient.
    /// </param>
    public static string Fragment(IReadOnlyList<Block> blocks, DocumentStyle style, bool sourceLines = false,
                                  Func<string, string?>? localImage = null) =>
        new HtmlWriter(style, sourceLines, localImage).Write(blocks);

    /// <summary>
    /// Wraps a fragment in a full page for the preview pane, which is also what PDF export prints.
    /// setContent() lets the preview update without reloading, so the scroll position is kept while typing.
    /// </summary>
    public static string Page(string fragment) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<style>" + PageCss + "</style>" +
        "<script>" + PreviewScript + FindScript + "</script></head>" +
        $"<body>{fragment}</body></html>";

    /// <summary>
    /// On screen: 12px around the page, a thin scroll bar, and a gutter of the scroll bar's width on both sides
    /// so left and right stay equal. In print (PDF export), the page margins come from the print settings, and
    /// headings stay with what follows them while code blocks, table rows and images aren't split across pages.
    /// "Page n of N" is centered in the bottom margin, in RDG Rock Gray (the brand's caption color), set in the
    /// document's body font by setPageFont() before printing.
    /// </summary>
    private const string PageCss =
        "html{scrollbar-width:thin;scrollbar-gutter:stable both-edges;}" +
        "body{margin:12px 4px;background:#FFFFFF;}" +
        "@media print{html{scrollbar-gutter:auto;}body{margin:0;}" +
        "h1,h2,h3,h4,h5,h6{break-after:avoid;}pre,tr,img,blockquote{break-inside:avoid;}}" +
        "@page{@bottom-center{content:'Page ' counter(page) ' of ' counter(pages);font-size:9pt;color:#8C8A88;}}" +
        // Find matches (see FindScript): all of them pale yellow, the current one orange. Never printed.
        "::highlight(find){background-color:#FFF0A0;}::highlight(find-current){background-color:#FFB940;}" +
        "@media print{::highlight(find),::highlight(find-current){background-color:transparent;}}";

    /// <summary>
    /// Preview helpers (plain ES5, from when the preview was IE11; Chromium runs it as is).
    /// scrollToLine finds the blocks tagged just before and after a source line, interpolates a position
    /// between them, and scrolls so it sits at the same fraction of the viewport as the editor's caret line.
    /// report() then tells the app (chrome.webview.postMessage) where that line's row is on screen, so it
    /// can draw the line marker beside the page; it runs again whenever the preview scrolls.
    /// The other way round: when the user scrolls the page, it posts the source line at the top of the
    /// viewport (lineAt, the inverse of scrollToLine), and on mouseup it posts the clicked block's lines plus
    /// the clicked word or selected text, so the app can scroll the editor and select the matching source.
    /// userAt is when the user last used the page (wheel, mouse, key, touch); a scroll counts as theirs only
    /// shortly after that, and the app's own scrollToLine/setContent clear it, so the panes don't chase each other.
    /// </summary>
    private const string PreviewScript =
        "var lastLine=-1,lastShow=false,userAt=0;" +
        "function setContent(html){userAt=0;document.body.innerHTML=html;}" +
        "function setPageFont(font){var s=document.getElementById('page-font');" +
        "if(!s){s=document.createElement('style');s.id='page-font';document.head.appendChild(s);}" +
        "s.textContent='@page{@bottom-center{font-family:'+font+';}}';}" +
        "function scrollY(){return window.pageYOffset||document.documentElement.scrollTop;}" +
        "function pageTop(el){return el.getBoundingClientRect().top+scrollY();}" +
        "function locate(line){var nodes=document.querySelectorAll('[data-line]'),before=null,after=null;" +
        "for(var i=0;i<nodes.length;i++){var n=+nodes[i].getAttribute('data-line');if(n<=line){before=nodes[i];}else{after=nodes[i];break;}}" +
        "return{before:before,after:after};}" +
        "function scrollToLine(line,fraction,show){userAt=0;lastLine=line;lastShow=show;" +
        "var f=locate(line),y=0;" +
        "if(f.before){var top=pageTop(f.before),from=+f.before.getAttribute('data-line');" +
        "if(f.after){var to=+f.after.getAttribute('data-line');y=top+(pageTop(f.after)-top)*(line-from)/(to-from);}else{y=top;}}" +
        "window.scrollTo(0,Math.max(0,y-document.documentElement.clientHeight*fraction));report();}" +
        // The row for a line: the block that starts on it, minus any nested list (for list items). A block
        // written over several source lines is split evenly between them. Blank lines have no row.
        "function rowBox(line){var el=locate(line).before;if(!el)return null;" +
        "var from=+el.getAttribute('data-line'),count=+(el.getAttribute('data-count')||1);" +
        "if(line>=from+count)return null;" +
        "var top=pageTop(el),h=el.offsetHeight;" +
        "if(el.tagName=='LI'){for(var c=el.firstChild;c;c=c.nextSibling){if(c.tagName=='UL'||c.tagName=='OL'){h=pageTop(c)-top;break;}}}" +
        "if(count>1){var per=h/count;top+=per*(line-from);h=per;}" +
        "return{top:top-scrollY(),height:h};}" +
        "function post(m){try{window.chrome.webview.postMessage(m);}catch(e){}}" +
        "function report(){var b=lastShow?rowBox(lastLine):null;" +
        "post({type:'marker',top:b?b.top:0,height:b?b.height:0,visible:!!b," +
        "viewport:document.documentElement.clientHeight});}" +
        // Marks a line without scrolling: the user clicked it here, so the page is already in the right place.
        "function markLine(line){lastLine=line;lastShow=true;report();}" +
        // The (fractional) source line at page position y: between the tagged blocks above and below it.
        "function lineAt(y){var nodes=document.querySelectorAll('[data-line]'),before=null,after=null;" +
        "for(var i=0;i<nodes.length;i++){if(pageTop(nodes[i])<=y){before=nodes[i];}else{after=nodes[i];break;}}" +
        "if(!before)return 0;" +
        "var top=pageTop(before),from=+before.getAttribute('data-line');" +
        "if(after){var span=pageTop(after)-top;return span>0?from+(+after.getAttribute('data-line')-from)*(y-top)/span:from;}" +
        "var h=before.offsetHeight;return h>0?from+(+(before.getAttribute('data-count')||1))*Math.min(1,(y-top)/h):from;}" +
        // Links: the page has no address of its own (it's a data: URL), so a relative href can't be resolved here.
        // The app gets the href as written and decides (FollowLink). Left and middle clicks, Ctrl or not.
        "function follow(e){if(e.type=='auxclick'&&e.button!==1)return;" +
        "var a=e.target&&e.target.closest?e.target.closest('a[href]'):null;if(!a)return;" +
        "e.preventDefault();post({type:'link',href:a.getAttribute('href')});}" +
        "document.addEventListener('click',follow,true);document.addEventListener('auxclick',follow,true);" +
        "function touched(){userAt=Date.now();}" +
        "['wheel','mousedown','pointerdown','keydown','touchstart'].forEach(function(n){window.addEventListener(n,touched,true);});" +
        "window.onscroll=function(){report();" +
        "var now=Date.now();if(now-userAt>=500)return;userAt=now;" +   // each scroll of a long drag keeps it going
        "var d=document.documentElement,y=scrollY();" +
        "post({type:'scroll',line:lineAt(y),atTop:y<=0,atEnd:y+d.clientHeight>=d.scrollHeight-1});};" +
        // A click or a finished selection. The block comes from where the selection starts (or the click);
        // text is the selection's first line or the clicked word, and nth counts its earlier occurrences in
        // the block. end is the line of the next tagged block (-1: none), which bounds the search in the source.
        "var wordChar=/[\\w\\u00C0-\\uFFFF]/;" +
        "document.addEventListener('mouseup',function(e){if(e.button!==0)return;" +
        "var t=e.target;if(!t||!t.closest||t.closest('a'))return;" +
        "var sel=window.getSelection(),range=null,text='',y=e.clientY;" +
        "if(sel&&!sel.isCollapsed&&sel.rangeCount){range=sel.getRangeAt(0);text=sel.toString().replace(/^\\s+/,'').split('\\n')[0];" +
        "t=range.startContainer;if(t.nodeType!=1)t=t.parentNode;" +
        "var rects=range.getClientRects();if(rects.length)y=rects[0].top;}" +
        "else if(document.caretRangeFromPoint){var p=document.caretRangeFromPoint(e.clientX,e.clientY);" +
        "if(p&&p.startContainer.nodeType==3){var s=p.startContainer.data,a=p.startOffset,b=a;" +
        "while(a>0&&wordChar.test(s.charAt(a-1)))a--;while(b<s.length&&wordChar.test(s.charAt(b)))b++;" +
        "if(b>a){range=document.createRange();range.setStart(p.startContainer,a);range.setEnd(p.startContainer,b);text=s.substring(a,b);}}}" +
        "var el=t&&t.closest?t.closest('[data-line]'):null;if(!el)return;" +
        "var first=+el.getAttribute('data-line'),count=+(el.getAttribute('data-count')||1);" +
        "var r=el.getBoundingClientRect(),h=r.height;" +
        "if(el.tagName=='LI'){for(var c=el.firstChild;c;c=c.nextSibling){if(c.tagName=='UL'||c.tagName=='OL'){h=c.getBoundingClientRect().top-r.top;break;}}}" +
        "var line=first;if(count>1&&h>0)line=first+Math.max(0,Math.min(count-1,Math.floor((y-r.top)*count/h)));" +
        "var nodes=document.querySelectorAll('[data-line]'),end=-1,seen=false;" +
        "for(var i=0;i<nodes.length;i++){if(nodes[i]===el){seen=true;}else if(seen&&+nodes[i].getAttribute('data-line')>first){end=+nodes[i].getAttribute('data-line');break;}}" +
        "var nth=0;text=text.substring(0,200);" +
        "if(text&&range){try{var pre=document.createRange();pre.selectNodeContents(el);pre.setEnd(range.startContainer,range.startOffset);" +
        "var lead=pre.toString(),at=-1;while((at=lead.indexOf(text,at+1))>=0)nth++;}catch(x){}}" +
        "post({type:'select',line:line,first:first,end:end,text:text,nth:nth," +
        "fraction:Math.max(0,Math.min(1,y/document.documentElement.clientHeight))});});";

    /// <summary>
    /// Find and Replace in the preview. The app searches the Markdown; findMark shows the same search in the page.
    /// Every match in the rendered text is highlighted (CSS Custom Highlight API, so the page isn't changed), and
    /// so is the current one: the source line of the editor's current match gives the block, and prior (the source
    /// lines of the matches before it) says how many earlier matches are in that block, so which one in the block
    /// it is. reveal scrolls it into view, for Preview Only. A match the page can't see (inside a link's URL,
    /// or across formatting) isn't highlighted; reveal then scrolls to its line instead.
    /// Word characters match .NET's \w, so whole-word search agrees with the app's.
    /// </summary>
    private const string FindScript = """
        function findMark(query,matchCase,wholeWord,line,prior,reveal){
        var hl=window.CSS&&CSS.highlights&&window.Highlight;
        if(hl){CSS.highlights.delete('find');CSS.highlights.delete('find-current');}
        if(!query)return;
        var w='[\\p{L}\\p{Mn}\\p{Nd}\\p{Pc}]',p=query.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
        if(wholeWord)p='(?<!'+w+')'+p+'(?!'+w+')';
        var re=new RegExp(p,matchCase?'gu':'giu'),all=[],walk=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT),n,m;
        while((n=walk.nextNode())){re.lastIndex=0;
        while((m=re.exec(n.data))){var r=document.createRange();r.setStart(n,m.index);r.setEnd(n,m.index+m[0].length);all.push(r);}}
        var current=null,el=line>=0?locate(line).before:null;
        if(el){var first=+el.getAttribute('data-line'),nth=0,k=0;
        for(var i=0;i<prior.length;i++)if(prior[i]>=first)nth++;
        for(var j=0;j<all.length;j++){if(el.contains(all[j].startContainer)){if(k==nth){current=all[j];break;}k++;}}}
        if(hl){var h=new Highlight();all.forEach(function(r){h.add(r);});CSS.highlights.set('find',h);
        if(current)CSS.highlights.set('find-current',new Highlight(current));}
        if(!reveal||line<0)return;
        if(!current){scrollToLine(line,0.33,false);return;}
        var b=current.getBoundingClientRect(),vh=document.documentElement.clientHeight;
        if(b.top<0||b.bottom>vh){userAt=0;window.scrollTo(0,Math.max(0,scrollY()+b.top-vh/3));}}
        """;

    /// <summary>Builds the Windows "HTML Format" clipboard payload, whose header holds byte offsets.</summary>
    public static string ToClipboardFormat(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string prefix = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";

        // The fragment is ASCII-only, so character counts equal byte counts.
        int startHtml = string.Format(header, 0, 0, 0, 0).Length;
        int startFragment = startHtml + prefix.Length;
        int endFragment = startFragment + fragment.Length;
        int endHtml = endFragment + suffix.Length;

        return string.Format(header, startHtml, endHtml, startFragment, endFragment) + prefix + fragment + suffix;
    }

    private sealed class HtmlWriter(DocumentStyle style, bool sourceLines, Func<string, string?>? localImage)
    {
        private readonly StringBuilder _html = new();
        private readonly Dictionary<string, string> _tagStyles = BuildTagStyles(style);

        public string Write(IEnumerable<Block> blocks)
        {
            string body = $"font-family:{InstalledFonts.CssStack(style.BodyFont, "sans-serif")};" +
                          $"font-size:{DocumentStyle.Points(style.BodySize)}pt;line-height:1.4;" +
                          $"color:#{DocumentStyle.Hex(style.TextColor)};";
            _html.Append($"<div style=\"{body}\">");
            WriteBlocks(blocks, tight: false);
            return _html.Append("</div>").ToString();
        }

        private static Dictionary<string, string> BuildTagStyles(DocumentStyle s)
        {
            string headingFont = InstalledFonts.CssStack(s.HeadingFont, "sans-serif");
            string subheadingFont = InstalledFonts.CssStack(s.SubheadingFont, "sans-serif");
            string mono = $"font-family:{InstalledFonts.CssStack(s.CodeFont, "monospace")};font-size:{DocumentStyle.Points(s.BodySize - 1)}pt;";
            string weight = s.BoldHeadings ? "bold" : "normal";
            string accent = DocumentStyle.Hex(s.AccentColor, "BFBFBF");
            string border = DocumentStyle.Hex(s.BorderColor, "BFBFBF");
            string codeBackground = DocumentStyle.Hex(s.CodeBackground, "F3F3F3");

            string Heading(string font, string color, double size, string margin, bool italic = false) =>
                $"font-family:{font};font-size:{DocumentStyle.Points(size)}pt;font-weight:{weight};" +
                $"color:#{DocumentStyle.Hex(color)};margin:{margin};" + (italic ? "font-style:italic;" : "");

            return new Dictionary<string, string>
            {
                ["h1"] = Heading(headingFont, s.HeadingColor, s.Heading1Size, "16px 0 8px"),
                ["h2"] = Heading(headingFont, s.HeadingColor, s.Heading2Size, "16px 0 6px"),
                ["h3"] = Heading(subheadingFont, s.SubheadingColor, s.Heading3Size, "14px 0 6px"),
                ["h4"] = Heading(subheadingFont, s.SubheadingColor, s.BodySize + 1, "12px 0 4px"),
                ["h5"] = Heading(subheadingFont, s.SubheadingColor, s.BodySize, "12px 0 4px"),
                ["h6"] = Heading(subheadingFont, s.SubheadingColor, s.BodySize, "12px 0 4px", italic: true),
                ["p"] = "margin:0 0 10px;",
                ["ul"] = "margin:0 0 10px;padding-left:28px;",
                ["ol"] = "margin:0 0 10px;padding-left:28px;",
                ["li"] = "margin:0 0 4px;",
                ["table"] = "border-collapse:collapse;margin:0 0 10px;",
                ["th"] = $"border:1px solid #{border};padding:4px 8px;background:#{DocumentStyle.Hex(s.TableHeaderBackground, "F2F2F2")};text-align:left;",
                ["td"] = $"border:1px solid #{border};padding:4px 8px;",
                ["pre"] = mono + $"background:#{codeBackground};padding:8px 10px;margin:0 0 10px;white-space:pre-wrap;",
                ["code"] = mono + $"background:#{codeBackground};",
                ["precode"] = mono,   // <code> inside <pre>: browsers would otherwise reset it to their default monospace
                ["blockquote"] = $"margin:0 0 10px;padding:0 0 0 12px;border-left:3px solid #{accent};color:#{DocumentStyle.Hex(s.QuoteTextColor)};",
                ["hr"] = $"border:none;border-top:2px solid #{accent};margin:12px 0;",
                ["a"] = $"color:#{DocumentStyle.Hex(s.LinkColor)};text-decoration:underline;",
            };
        }

        // ---- Blocks ----

        private void WriteBlocks(IEnumerable<Block> blocks, bool tight)
        {
            foreach (var block in blocks)
                WriteBlock(block, tight);
        }

        private void WriteBlock(Block block, bool tight)
        {
            switch (block)
            {
                case Heading heading:
                    Open($"h{heading.Level}", line: heading.Line, count: heading.LineCount);
                    WriteInlines(heading.Content);
                    _html.Append($"</h{heading.Level}>");
                    break;
                case Paragraph paragraph when tight:
                    WriteInlines(paragraph.Content);
                    break;
                case Paragraph paragraph:
                    Open("p", line: paragraph.Line, count: paragraph.LineCount);
                    WriteInlines(paragraph.Content);
                    _html.Append("</p>");
                    break;
                case CodeBlock code:
                    Open("pre", line: code.Line, count: code.LineCount);
                    _html.Append($"<code style=\"{_tagStyles["precode"]}\">").Append(Escape(string.Join("\n", code.Lines))).Append("</code></pre>");
                    break;
                case BlockQuote quote:
                    Open("blockquote", line: quote.Line);
                    WriteBlocks(quote.Children, tight: false);
                    _html.Append("</blockquote>");
                    break;
                case ListBlock list:
                    WriteList(list);
                    break;
                case Table table:
                    WriteTable(table);
                    break;
                case ThematicBreak:
                    Open("hr", line: block.Line);
                    break;
            }
        }

        private void WriteList(ListBlock list)
        {
            string tag = list.Ordered ? "ol" : "ul";
            _html.Append($"<{tag}{LineAttribute(list.Line)} style=\"{_tagStyles[tag]}\"");
            if (list.Ordered && list.Start != 1) _html.Append($" start=\"{list.Start}\"");
            _html.Append('>');

            foreach (var item in list.Items)
            {
                Open("li", line: item.Children.FirstOrDefault()?.Line ?? -1);
                // Email clients strip <input>, so task boxes are ballot-box characters.
                if (item.Checked is bool done) _html.Append(done ? "&#9745; " : "&#9744; ");
                WriteBlocks(item.Children, tight: !list.Loose);
                _html.Append("</li>");
            }
            _html.Append($"</{tag}>");
        }

        private void WriteTable(Table table)
        {
            Open("table", line: table.Line);
            _html.Append("<thead>");
            WriteRow(table.Header, table.Alignments, "th", table.Line);
            _html.Append("</thead><tbody>");
            for (int r = 0; r < table.Rows.Count; r++)
                WriteRow(table.Rows[r], table.Alignments, "td", table.Line < 0 ? -1 : table.Line + 2 + r);   // after header and dashes lines
            _html.Append("</tbody></table>");
        }

        private void WriteRow(TableRow row, IReadOnlyList<ColumnAlign> alignments, string cellTag, int line)
        {
            _html.Append($"<tr{LineAttribute(line)}>");
            for (int c = 0; c < row.Cells.Count; c++)
            {
                string align = alignments[c] switch
                {
                    ColumnAlign.Center => "text-align:center;",
                    ColumnAlign.Right => "text-align:right;",
                    ColumnAlign.Left => "text-align:left;",
                    _ => "",
                };
                Open(cellTag, align);
                WriteInlines(row.Cells[c]);
                _html.Append($"</{cellTag}>");
            }
            _html.Append("</tr>");
        }

        // ---- Inlines ----

        private void WriteInlines(IEnumerable<Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Text text:
                        _html.Append(Escape(text.Value));
                        break;
                    case CodeSpan code:
                        Open("code");
                        _html.Append(Escape(code.Code)).Append("</code>");
                        break;
                    case LineBreak:
                        _html.Append("<br>");
                        break;
                    case Emphasis emphasis:
                        string tag = emphasis.Kind switch
                        {
                            EmphasisKind.Bold => "strong",
                            EmphasisKind.Italic => "em",
                            _ => "del",
                        };
                        _html.Append($"<{tag}>");
                        WriteInlines(emphasis.Children);
                        _html.Append($"</{tag}>");
                        break;
                    case Link link:
                        _html.Append($"<a href=\"{Escape(link.Url)}\" style=\"{_tagStyles["a"]}\">");
                        WriteInlines(link.Children);
                        _html.Append("</a>");
                        break;
                    case Image image when IsWebUrl(image.Url):
                        WriteImage(image.Url, image.Alt);
                        break;
                    case Image image when localImage?.Invoke(image.Url) is { } source:
                        WriteImage(source, image.Alt);
                        break;
                    case Image image:
                        // Local image paths mean nothing to the email's recipient.
                        _html.Append("<em>[Image: ").Append(Escape(image.Alt)).Append("]</em>");
                        break;
                }
            }
        }

        private void WriteImage(string source, string alt) =>
            _html.Append($"<img src=\"{Escape(source)}\" alt=\"{Escape(alt)}\" style=\"max-width:100%;\">");

        private void Open(string tag, string extraStyle = "", int line = -1, int count = 1) =>
            _html.Append($"<{tag}{LineAttribute(line, count)} style=\"{_tagStyles[tag]}{extraStyle}\">");

        private string LineAttribute(int line, int count = 1) =>
            !sourceLines || line < 0 ? ""
            : count > 1 ? $" data-line=\"{line}\" data-count=\"{count}\""
            : $" data-line=\"{line}\"";
    }

    // ---- Helpers ----

    private static bool IsWebUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '"': escaped.Append("&quot;"); break;
                case < (char)128: escaped.Append(c); break;
                default:
                    int codePoint = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                        ? char.ConvertToUtf32(c, text[++i])
                        : c;
                    escaped.Append("&#").Append(codePoint).Append(';');
                    break;
            }
        }
        return escaped.ToString();
    }
}
