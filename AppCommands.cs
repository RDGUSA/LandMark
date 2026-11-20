using System.Windows.Input;

namespace LandMark;

/// <summary>App-specific menu commands. File/Edit basics use the built-in ApplicationCommands.</summary>
public static class AppCommands
{
    public static readonly RoutedUICommand ExportPdf = Create("Export PDF", nameof(ExportPdf), new KeyGesture(Key.E, ModifierKeys.Control));
    public static readonly RoutedUICommand ExportDocx = Create("Export DOCX", nameof(ExportDocx));
    public static readonly RoutedUICommand ExportText = Create("Export TXT", nameof(ExportText));
    public static readonly RoutedUICommand Exit = Create("Exit", nameof(Exit));
    public static readonly RoutedUICommand ShowInExplorer = Create("Show in File Explorer", nameof(ShowInExplorer));

    public static readonly RoutedUICommand CopyForEmail = Create("Copy for Email", nameof(CopyForEmail),
        new KeyGesture(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand CopyPlainText = Create("Copy as Plain Text", nameof(CopyPlainText),
        new KeyGesture(Key.C, ModifierKeys.Control | ModifierKeys.Alt));

    public static readonly RoutedUICommand Find = Create("Find", nameof(Find), new KeyGesture(Key.F, ModifierKeys.Control));
    public static readonly RoutedUICommand FindNext = Create("Find Next", nameof(FindNext), new KeyGesture(Key.F3));
    public static readonly RoutedUICommand FindPrevious = Create("Find Previous", nameof(FindPrevious),
        new KeyGesture(Key.F3, ModifierKeys.Shift));
    public static readonly RoutedUICommand Replace = Create("Replace", nameof(Replace), new KeyGesture(Key.H, ModifierKeys.Control));

    public static readonly RoutedUICommand EditorOnlyView = Create("Editor Only", nameof(EditorOnlyView), new KeyGesture(Key.D1, ModifierKeys.Control, "Ctrl+1"));
    public static readonly RoutedUICommand SplitView = Create("Split", nameof(SplitView), new KeyGesture(Key.D2, ModifierKeys.Control, "Ctrl+2"));
    public static readonly RoutedUICommand PreviewOnlyView = Create("Preview Only", nameof(PreviewOnlyView), new KeyGesture(Key.D3, ModifierKeys.Control, "Ctrl+3"));
    public static readonly RoutedUICommand ToggleOutline = Create("Outline", nameof(ToggleOutline),
        new KeyGesture(Key.O, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand ToggleWordWrap = Create("Word Wrap", nameof(ToggleWordWrap), new KeyGesture(Key.Z, ModifierKeys.Alt));
    public static readonly RoutedUICommand ToggleSyntaxColors = Create("Syntax Colors", nameof(ToggleSyntaxColors));
    public static readonly RoutedUICommand ZoomIn = Create("Zoom In", nameof(ZoomIn), new KeyGesture(Key.OemPlus, ModifierKeys.Control, "Ctrl++"));
    public static readonly RoutedUICommand ZoomOut = Create("Zoom Out", nameof(ZoomOut), new KeyGesture(Key.OemMinus, ModifierKeys.Control, "Ctrl+-"));
    public static readonly RoutedUICommand ZoomReset = Create("Reset Zoom", nameof(ZoomReset), new KeyGesture(Key.D0, ModifierKeys.Control, "Ctrl+0"));

    public static readonly RoutedUICommand CustomizeStyles = Create("Customize Styles", nameof(CustomizeStyles));
    public static readonly RoutedUICommand SetDefaultStyle = Create("Set Current Style as Default", nameof(SetDefaultStyle));

    public static readonly RoutedUICommand ToggleCheatSheet = Create("Markdown Cheat Sheet", nameof(ToggleCheatSheet), new KeyGesture(Key.F1));
    public static readonly RoutedUICommand About = Create("About LandMark", nameof(About));

    private static RoutedUICommand Create(string text, string name, params InputGesture[] gestures) =>
        new(text, name, typeof(AppCommands), new InputGestureCollection(gestures));
}
