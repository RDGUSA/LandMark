using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LandMark.Markdown;
using LandMark.Styling;

namespace LandMark.Controls;

/// <summary>
/// Draws the editor's text in syntax colors. It sits behind a TextBox whose own text is transparent, so the
/// TextBox keeps everything (caret, selection, undo, IME) and this only paints. Each visible line is drawn
/// where the TextBox itself laid it out, so wrapping, scrolling and zoom always line up.
/// </summary>
public sealed class MarkdownHighlightLayer : FrameworkElement
{
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(MarkdownHighlightLayer),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((MarkdownHighlightLayer)d).UpdatePalette()));

    /// <summary>Plain text color; also decides between the light and dark palettes.</summary>
    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    private readonly MarkdownHighlighter _highlighter = new();
    private SyntaxPalette _palette = SyntaxPalette.Light;
    private TextBox? _editor;
    private ScrollContentPresenter? _viewport;
    private bool _textChanged = true;
    private bool _needsRender = true;

    public MarkdownHighlightLayer()
    {
        IsHitTestVisible = false;
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        IsVisibleChanged += (_, _) => _needsRender = true;
    }

    /// <summary>The source line (zero-based) containing a character offset of the editor's text.</summary>
    public int LineFromOffset(int offset)
    {
        SyncText();
        return _highlighter.LineFromOffset(offset);
    }

    public void Attach(TextBox editor)
    {
        _editor = editor;
        editor.TextChanged += (_, _) => { _textChanged = true; _needsRender = true; };
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => _needsRender = true));
        editor.SizeChanged += (_, _) => _needsRender = true;
        editor.Loaded += (_, _) => InvalidateVisual();
        foreach (var property in new[] { Control.FontSizeProperty, Control.FontFamilyProperty, TextBox.TextWrappingProperty })
            DependencyPropertyDescriptor.FromProperty(property, typeof(TextBox)).AddValueChanged(editor, (_, _) => _needsRender = true);

        // Redraw after the TextBox's layout pass, in the same frame, so new text never shows a frame late.
        editor.LayoutUpdated += (_, _) =>
        {
            if (!_needsRender || !IsVisible) return;
            _needsRender = false;
            InvalidateVisual();
        };
    }

    private void UpdatePalette()
    {
        if (TextBrush is SolidColorBrush solid)
            _palette = SyntaxPalette.ForTextColor(solid.Color);
    }

    private void SyncText()
    {
        if (!_textChanged || _editor == null) return;
        _highlighter.SetText(_editor.Text);
        _textChanged = false;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (_editor == null) return;
        SyncText();

        // While the TextBox re-lays out (e.g. just after Word Wrap changes) it can report no lines yet.
        int lineCount = _editor.IsLoaded ? _editor.LineCount : 0;
        int first = lineCount > 0 ? _editor.GetFirstVisibleLineIndex() : -1;
        int last = lineCount > 0 ? Math.Min(_editor.GetLastVisibleLineIndex(), lineCount - 1) : -1;
        if (first < 0 || last < first)
        {
            _needsRender = _editor.Text.Length > 0;   // the editor isn't laid out yet; try again after its next layout
            return;
        }

        // Clip to the scrolled area, inside the editor's padding, like the TextBox's own text.
        // Found again after a theme change, which re-applies the editor's template (and replaces its viewport).
        if (_viewport == null || !_viewport.IsDescendantOf(_editor))
            _viewport = FindViewport(_editor);
        if (_viewport == null || !_viewport.IsDescendantOf(_editor) || !IsDescendantOf(VisualTreeHelper.GetParent(_editor)))
        {
            _needsRender = true;   // mid template change; draw after the next layout
            return;
        }
        drawing.PushClip(new RectangleGeometry(_viewport.TransformToVisual(this).TransformBounds(new Rect(_viewport.RenderSize))));

        var typeface = new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight, _editor.FontStretch);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mode = TextOptions.GetTextFormattingMode(_editor);
        var toLayer = _editor.TransformToVisual(this);
        string text = _editor.Text;

        for (int line = first; line <= last; line++)
        {
            int start = _editor.GetCharacterIndexFromLineIndex(line);
            int length = _editor.GetLineLength(line);
            if (start < 0 || length <= 0) continue;
            int end = Math.Min(start + length, text.Length);
            while (end > start && text[end - 1] is '\n' or '\r') end--;
            if (end == start) continue;

            var spans = _highlighter.SpansForLine(_highlighter.LineFromOffset(start));

            // Tabs are split out and each piece placed where the TextBox put it, so tab widths can't drift.
            for (int piece = start; piece < end;)
            {
                int tab = text.IndexOf('\t', piece, end - piece);
                int pieceEnd = tab < 0 ? end : tab;
                if (pieceEnd > piece)
                {
                    var rect = _editor.GetRectFromCharacterIndex(piece);
                    if (!rect.IsEmpty)
                    {
                        var formatted = new FormattedText(text[piece..pieceEnd], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                            typeface, _editor.FontSize, TextBrush, null, mode, pixelsPerDip);
                        Colorize(formatted, spans, piece, pieceEnd);
                        drawing.DrawText(formatted, toLayer.Transform(rect.TopLeft));
                    }
                }
                piece = pieceEnd + 1;
            }
        }

        drawing.Pop();
    }

    private void Colorize(FormattedText formatted, IReadOnlyList<SyntaxSpan> spans, int from, int to)
    {
        foreach (var span in spans)
        {
            int start = Math.Max(span.Start, from);
            int end = Math.Min(span.Start + span.Length, to);
            if (end <= start) continue;

            int index = start - from, count = end - start;
            formatted.SetForegroundBrush(_palette.BrushFor(span.Kind), index, count);
            if (SyntaxPalette.WeightFor(span.Kind) is { } weight) formatted.SetFontWeight(weight, index, count);
            if (SyntaxPalette.StyleFor(span.Kind) is { } style) formatted.SetFontStyle(style, index, count);
            if (SyntaxPalette.DecorationsFor(span.Kind) is { } decorations) formatted.SetTextDecorations(decorations, index, count);
        }
    }

    private static ScrollContentPresenter? FindViewport(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollContentPresenter presenter) return presenter;
            if (FindViewport(child) is { } found) return found;
        }
        return null;
    }
}
