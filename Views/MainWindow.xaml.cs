using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LandMark.Conversion;
using LandMark.Markdown;
using LandMark.Models;
using LandMark.Services;
using LandMark.Styling;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LandMark.Views;

public partial class MainWindow : Window
{
    private const string MarkdownFilter = "Markdown files (*.md;*.markdown)|*.md;*.markdown;*.mdown;*.mkd|Text files (*.txt)|*.txt|All files (*.*)|*.*";
    private const double DefaultFontSize = 13;

    private enum ViewMode { EditorOnly, Split, PreviewOnly }

    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _scrollSyncTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _findTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly StyleLibrary _styles = StyleLibrary.Load();
    private DocumentStyle _style;
    private bool _styleChosen;   // style picked for a document that hasn't been saved yet
    private string? _currentPath;
    private bool _isDirty;
    private string _savedText = "";   // the text as last opened or saved; _isDirty is whether the editor differs from it
    private bool _settingText;
    private bool _previewLoaded;
    private long _previewDrivingUntil;   // Environment.TickCount64; set when the preview scrolls or selects in the editor
    private bool _cheatSheetOpen;
    private bool _outlineOpen;
    private readonly ObservableCollection<OutlineEntry> _outline = [];
    private bool _findOpen;
    private string? _findMarkScript;   // the preview's find highlights, sent again after each setContent
    private bool _selectingFromPreview;
    private bool _editorSelectionFromPreview;   // the editor's selection mirrors a click in the preview (see HighlightedTextAsync)

    // Changes to the open file made by other programs (see "File on disk" below).
    private readonly DispatcherTimer _diskCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private FileSystemWatcher? _watcher;
    private string _diskText = "";      // the file's text as last opened, saved or reloaded here
    private bool _diskChanged;          // another program changed the file and it hasn't been reloaded or overwritten
    private bool _fileMissing;          // another program deleted, moved or renamed the file
    private int _diskReadRetries;
    private Action? _pendingNotice;     // a notice about the file waiting for this window to be active
    private bool _noticeShowing;

    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            var blocks = MarkdownParser.Parse(Editor.Text);
            RefreshPreview(blocks);
            if (_outlineOpen) UpdateOutline(blocks);
            UpdateWordCount();
        };
        _scrollSyncTimer.Tick += (_, _) =>
        {
            _scrollSyncTimer.Stop();
            // While the preview is driving the editor, don't send its own position back to it.
            if (Environment.TickCount64 >= _previewDrivingUntil) SyncPreviewScroll();
            HighlightCurrentHeading();
        };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ShowDocumentStatus();
        };
        // Keeps "3 of 12" and the preview's highlights current as the caret moves and the text changes.
        _findTimer.Tick += (_, _) =>
        {
            _findTimer.Stop();
            if (_findOpen) UpdateFindResults(reveal: false);
        };
        _diskCheckTimer.Tick += (_, _) =>
        {
            _diskCheckTimer.Stop();
            CheckFileOnDisk();
        };
        // A notice that arrived while another program (or one of this window's dialogs) was in front.
        Activated += (_, _) => Dispatcher.BeginInvoke(ShowPendingNotice);
        Editor.SelectionChanged += (_, _) =>
        {
            _editorSelectionFromPreview = _selectingFromPreview;
            ScheduleScrollSync();
            if (_findOpen) { _findTimer.Stop(); _findTimer.Start(); }
        };
        Editor.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new System.Windows.Controls.ScrollChangedEventHandler((_, _) => ScheduleScrollSync()));
        Highlighter.Attach(Editor);
        SetSyntaxColors(App.Settings.SyntaxColors);
        OutlineList.ItemsSource = _outline;
        Loaded += async (_, _) => await StartPreviewAsync();

        _style = _styles.Default;
        UpdateStyleDisplay();
        UpdateThemeChecks();
        UpdateMarginChecks();

        // Track the theme's text color (it flips between light and dark) to keep the menu hover fill visible,
        // whether the theme changes from Format › App Theme or from Windows while set to System.
        SetResourceReference(TagProperty, "TextFillColorPrimary");
        DependencyPropertyDescriptor.FromProperty(TagProperty, typeof(MainWindow)).AddValueChanged(this, (_, _) => SyncMenuHighlight());
        SyncMenuHighlight();
        UpdateWordCount();
        UpdateTitle();
        if (startupFile != null)
            LoadFile(startupFile);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RunningInstances.Listen(this, ShowIfOpen);
    }

    /// <summary>Brings this window to the front if it has the file open (asked by a newly started LandMark).</summary>
    private bool ShowIfOpen(string path)
    {
        if (!IsCurrentFile(path)) return false;

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return true;
    }

    private bool IsCurrentFile(string path)
    {
        if (_currentPath == null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(_currentPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a file chosen in this window (File › Open, drag and drop, or a link in the preview), unless it's already
    /// open: if it's this window's file nothing changes, and if another LandMark window has it, that window comes to
    /// the front. Otherwise it replaces this window's document, after asking about unsaved changes.
    /// </summary>
    private void OpenFile(string path)
    {
        if (IsCurrentFile(path))
        {
            FlashStatus($"{Path.GetFileName(path)} is already open");
            return;
        }
        if (RunningInstances.TryShowInRunningWindow(path)) return;
        if (ConfirmDiscardChanges()) LoadFile(path);
    }

    // ---- File menu ----

    private void New_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        SetDocument("", path: null);
    }

    private void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = MarkdownFilter };
        if (dialog.ShowDialog(this) == true)
            OpenFile(dialog.FileName);
    }

    /// <summary>Rebuilds File › Open Recent, and disables it when there's nothing to list.</summary>
    private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != FileMenu) return;

        while (RecentMenu.Items[0] != RecentListSeparator)
            RecentMenu.Items.RemoveAt(0);

        App.Settings.RefreshRecentFiles();   // another LandMark window may have opened files since
        var files = App.Settings.RecentFiles;
        RecentMenu.IsEnabled = files.Count > 0;

        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            var item = new System.Windows.Controls.MenuItem
            {
                // The number is the access key; a single underscore in the name would become one too.
                Header = $"_{i + 1}  {Path.GetFileName(path).Replace("_", "__")}",
                ToolTip = path,
            };
            item.Click += (_, _) => OpenRecentFile(path);
            RecentMenu.Items.Insert(i, item);
        }
    }

    private void OpenRecentFile(string path)
    {
        if (File.Exists(path))
        {
            OpenFile(path);
            return;
        }

        App.Settings.RemoveRecentFile(path);
        ShowWarning("File not found", $"This file no longer exists, so it was removed from the recent list:\n\n{path}");
    }

    private void ClearRecentFiles_Click(object sender, RoutedEventArgs e) => App.Settings.ClearRecentFiles();

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e) => Save();

    private void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (ConfirmConflictsSettled()) SaveAs();
    }

    private void ExportDocx_Executed(object sender, ExecutedRoutedEventArgs e) =>
        Export("Word document (*.docx)|*.docx", ".docx", path => DocxConverter.Convert(Editor.Text, path, _style, App.Settings.MarginInches));

    private void ExportText_Executed(object sender, ExecutedRoutedEventArgs e) =>
        Export("Text file (*.txt)|*.txt", ".txt", path => File.WriteAllText(path, PlainTextConverter.Convert(Editor.Text)));

    /// <summary>
    /// Prints the preview page to a PDF with Chromium (WebView2), so the PDF looks exactly like the preview, and
    /// links stay clickable. US Letter, the current page margins, with page numbers in the bottom margin.
    /// </summary>
    private async void ExportPdf_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!_previewLoaded || Preview.CoreWebView2 is not { } core)
        {
            ShowWarning("Preview not available", "PDF export prints the preview, which isn't available. " + PreviewUnavailableText.Text);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PDF document (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = Path.GetFileNameWithoutExtension(_currentPath ?? "document") + ".pdf",
            InitialDirectory = Path.GetDirectoryName(_currentPath),
        };
        if (dialog.ShowDialog(this) != true) return;

        var settings = core.Environment.CreatePrintSettings();
        settings.Orientation = CoreWebView2PrintOrientation.Portrait;
        settings.PageWidth = 8.5;
        settings.PageHeight = 11;
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = App.Settings.MarginInches;
        settings.ShouldPrintBackgrounds = true;       // code and table header shading
        settings.ShouldPrintHeaderAndFooter = false;  // the page's own CSS puts "Page n of N" in the bottom margin

        try
        {
            // The preview may be up to 300 ms behind the editor; print what's in the editor now.
            _previewTimer.Stop();
            await SetPreviewContentAsync(MarkdownParser.Parse(Editor.Text));
            await core.ExecuteScriptAsync($"setPageFont({JsonSerializer.Serialize(InstalledFonts.CssStack(_style.BodyFont, "sans-serif"))})");
            // setContent made new <img> elements; give them up to 3 s to load (usually instant, from the cache).
            for (int wait = 0; wait < 30 && await core.ExecuteScriptAsync("Array.prototype.every.call(document.images,function(i){return i.complete;})") != "true"; wait++)
                await Task.Delay(100);
            bool printed = await core.PrintToPdfAsync(dialog.FileName, settings);
            if (_findMarkScript != null) RunPreviewScript(_findMarkScript);   // setContent dropped the find highlights
            if (printed)
                FlashStatus($"Exported {Path.GetFileName(dialog.FileName)}");
            else
                ShowWarning("Couldn't create the PDF", "If it's open in another program, close it and try again.");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            ShowError("Couldn't create the PDF", ex);
        }
    }

    private void Exit_Executed(object sender, ExecutedRoutedEventArgs e) => Close();

    /// <summary>Only for documents that have a file; new or pasted documents have nowhere to show.</summary>
    private void ShowInExplorer_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _currentPath != null;

    private void ShowInExplorer_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_currentPath == null) return;

        if (File.Exists(_currentPath))
        {
            // Opens the folder with the file selected.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentPath}\"") { UseShellExecute = true });
        }
        else if (Path.GetDirectoryName(_currentPath) is { } folder && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });   // the file was moved or deleted
        }
        else
        {
            ShowWarning("Folder not found", $"The folder for this document no longer exists:\n\n{_currentPath}");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        _watcher = null;
        _diskCheckTimer.Stop();
        base.OnClosed(e);
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;   // stop the TextBox refusing file drops
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        OpenFile(files[0]);
    }

    private void LoadFile(string path)
    {
        try
        {
            SetDocument(File.ReadAllText(path), path);
            App.Settings.AddRecentFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't open the file", ex);
        }
    }

    private void SetDocument(string text, string? path)
    {
        _settingText = true;
        Editor.Text = text;
        _settingText = false;
        _savedText = Editor.Text;
        _diskText = text;
        _currentPath = path;
        WatchFile(path);
        _isDirty = false;
        UpdateTitle();
        UpdateReloadButtons();

        _style = _styles.StyleFor(path);
        _styleChosen = false;
        UpdateStyleDisplay();
    }

    private bool Save()
    {
        if (!ConfirmConflictsSettled()) return false;
        if (_currentPath == null) return SaveAs();
        if (ChangedOnDisk() && !ConfirmOverwrite()) return false;
        return WriteMarkdown(_currentPath);
    }

    /// <summary>Whether another program has changed the file since it was opened, saved or reloaded here.</summary>
    private bool ChangedOnDisk()
    {
        if (_diskChanged) return true;
        // Also read it now: the watcher waits a moment before checking, and may miss changes on network drives.
        try
        {
            return _currentPath != null && File.Exists(_currentPath) && File.ReadAllText(_currentPath) != _diskText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Before a save: if conflict markers from a merge are still in the text, offers to go to the first one.
    /// Returns false to stop the save.
    /// </summary>
    private bool ConfirmConflictsSettled()
    {
        var (count, start, length) = TextMerge.FindConflicts(Editor.Text);
        if (count == 0) return true;

        string places = count == 1 ? "1 place" : $"{count} places";
        var choice = MessageDialog.Ask(this, MessageDialog.Kind.Warning, "Save with merge markers in it?",
            $"{DocumentName} still has merge markers in {places}, where Merge kept both your lines and the other " +
            $"program's. Saved now, the marker lines and both versions become part of the document.",
            primary: "Show me", secondary: "Save anyway", cancel: "Cancel");
        if (choice == MessageDialog.Result.Primary)
        {
            if (EditorCard.Visibility != Visibility.Visible) SetView(ViewMode.Split);
            Editor.Focus();
            Editor.Select(start, length);
            RevealInEditor(start);
        }
        return choice == MessageDialog.Result.Secondary;
    }

    private bool ConfirmOverwrite() =>
        MessageDialog.Show(this, MessageDialog.Kind.Warning, "Replace the changes on disk?",
            $"{DocumentName} was changed outside LandMark since you opened it. Saving now replaces those changes with your version.",
            primary: "Save anyway", secondary: "Cancel", primaryIsDefault: false);

    private bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = MarkdownFilter,
            DefaultExt = ".md",
            FileName = Path.GetFileName(_currentPath) ?? "document.md",
            InitialDirectory = Path.GetDirectoryName(_currentPath),
        };
        return dialog.ShowDialog(this) == true && WriteMarkdown(dialog.FileName);
    }

    private bool WriteMarkdown(string path)
    {
        try
        {
            string text = Editor.Text;
            File.WriteAllText(path, text);
            _savedText = text;
            _diskText = text;   // so the watcher's report of this write isn't taken for someone else's change
            bool moved = !IsCurrentFile(path);
            _currentPath = path;
            if (moved)
            {
                RefreshPreview(MarkdownParser.Parse(Editor.Text));   // relative image paths now point somewhere else
                WatchFile(path);
            }
            _diskChanged = false;
            _fileMissing = false;
            _isDirty = false;
            UpdateTitle();
            UpdateReloadButtons();
            App.Settings.AddRecentFile(path);

            // Keep the document's style with it, including after Save As to a new name.
            if (_styleChosen || _style.Id != _styles.DefaultStyleId)
            {
                _styles.Remember(path, _style);
                SaveStyles();
            }
            _styleChosen = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't save the file", ex);
            return false;
        }
    }

    private void Export(string filter, string extension, Action<string> export)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = extension,
            FileName = Path.GetFileNameWithoutExtension(_currentPath ?? "document") + extension,
            InitialDirectory = Path.GetDirectoryName(_currentPath),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            export(dialog.FileName);
            FlashStatus($"Exported {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't export the file", ex, "If it's open in Word or another program, close it and try again.");
        }
    }

    /// <summary>Asks to save unsaved changes. Returns false if the user cancels.</summary>
    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;

        string message = _fileMissing
            ? $"{DocumentName} was deleted or moved outside LandMark, so its text is only open here. If you don't save it, it's gone."
            : "If you don't save, your changes will be lost.";
        return MessageDialog.Ask(this, MessageDialog.Kind.Question, $"Save changes to {DocumentName}?", message,
                primary: "Save", secondary: "Don't save", cancel: "Cancel") switch
        {
            MessageDialog.Result.Primary => Save(),
            MessageDialog.Result.Secondary => true,
            _ => false,
        };
    }

    // ---- Edit menu ----

    private void CopyForEmail_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, EmailHtml.ToClipboardFormat(EmailHtml.Fragment(Editor.Text, _style)));
        data.SetData(DataFormats.UnicodeText, PlainTextConverter.Convert(Editor.Text));
        SetClipboard(data, "Copied for email. Paste it into your message.");
    }

    private void CopyPlainText_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, PlainTextConverter.Convert(Editor.Text));
        SetClipboard(data, "Copied as plain text.");
    }

    private void SetClipboard(DataObject data, string confirmation)
    {
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            FlashStatus(confirmation);
        }
        catch (COMException)
        {
            ShowWarning("Clipboard busy", "The clipboard is in use by another program. Try again.");
        }
    }

    // ---- Find and replace ----
    // The search always runs on the Markdown, whichever pane is showing: the current match is selected in the
    // editor (so Replace and Ctrl+Z work the same in every view), and every match is highlighted in the preview,
    // which scrolls to the current one when it's the only pane showing.

    private async void Find_Executed(object sender, ExecutedRoutedEventArgs e) => await OpenFind(showReplace: false);

    private async void Replace_Executed(object sender, ExecutedRoutedEventArgs e) => await OpenFind(showReplace: true);

    private void FindNext_Executed(object sender, ExecutedRoutedEventArgs e) => FindAgain(forward: true);

    private void FindPrevious_Executed(object sender, ExecutedRoutedEventArgs e) => FindAgain(forward: false);

    /// <summary>
    /// Opens the find bar, focuses it and goes straight to a match. The search starts as the text highlighted in
    /// the editor or the preview, if it's on one line; with nothing highlighted, a newly opened bar starts empty
    /// and an open one keeps its text.
    /// </summary>
    /// <param name="showReplace">Ctrl+H shows the Replace row; Ctrl+F leaves it as it was (hidden when the bar opens).</param>
    private async Task OpenFind(bool showReplace)
    {
        string highlighted = await HighlightedTextAsync();
        bool wasOpen = _findOpen;
        if (!wasOpen) SetReplaceRowVisible(false);
        if (showReplace) SetReplaceRowVisible(true);
        _findOpen = true;
        FindBar.Visibility = Visibility.Visible;

        string? search = highlighted.Length > 0 && highlighted.IndexOfAny(['\r', '\n']) < 0 ? highlighted
            : wasOpen ? null
            : "";
        if (search != null && search != FindBox.Text)
            FindBox.Text = search;   // FindBox_TextChanged searches
        else
            SearchAgainFromSelection();

        // After layout, so the bar is focusable; this also takes focus back from the preview's web view.
        var box = showReplace && FindBox.Text.Length > 0 ? ReplaceBox : FindBox;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private void CloseFind()
    {
        if (!_findOpen) return;
        _findOpen = false;
        _findTimer.Stop();
        FindBar.Visibility = Visibility.Collapsed;
        ClearPreviewFind();
        if (EditorCard.Visibility == Visibility.Visible) Editor.Focus();
        else Preview.Focus();
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e) => CloseFind();

    /// <summary>
    /// What the user has highlighted: the preview's own selection when the preview is the only pane, or when the
    /// editor's selection only mirrors a click in the preview (a click there selects the clicked word in the
    /// editor, which isn't something they highlighted); otherwise the editor's selection.
    /// </summary>
    private async Task<string> HighlightedTextAsync()
    {
        bool editorShown = EditorCard.Visibility == Visibility.Visible;
        if (editorShown && !_editorSelectionFromPreview) return Editor.SelectedText;
        if (!_previewLoaded || PreviewCard.Visibility != Visibility.Visible || Preview.CoreWebView2 is not { } core) return "";

        try
        {
            string json = await core.ExecuteScriptAsync("window.getSelection().toString()");
            return JsonSerializer.Deserialize<string>(json)?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException)
        {
            return "";
        }
    }

    /// <summary>F3 / Shift+F3. Opens the bar first if it's closed (searching for the highlighted text, if any).</summary>
    private async void FindAgain(bool forward)
    {
        if (!_findOpen) await OpenFind(showReplace: false);
        if (FindBox.Text.Length > 0) SelectMatch(forward);
    }

    private void ReplaceToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = ReplaceRow.Visibility != Visibility.Visible;
        SetReplaceRowVisible(show);
        (show ? ReplaceBox : FindBox).Focus();
    }

    private void SetReplaceRowVisible(bool visible)
    {
        ReplaceRow.Visibility = ReplaceButtons.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceToggleIcon.Text = visible ? "" : "";   // chevron down / right
    }

    /// <summary>Enter: next match (Shift: previous); in the Replace box, replace (Ctrl+Alt: all). Esc closes.</summary>
    private void FindBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (key == Key.Escape)
            CloseFind();
        else if (key == Key.Enter && ReplaceBox.IsKeyboardFocusWithin)
        {
            if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) ReplaceAll();
            else ReplaceOne();
        }
        else if (key == Key.Enter)
            SelectMatch(forward: modifiers != ModifierKeys.Shift);
        else if (modifiers == ModifierKeys.Alt && key is Key.C or Key.W)
        {
            var toggle = key == Key.C ? MatchCaseToggle : WholeWordToggle;
            toggle.IsChecked = toggle.IsChecked != true;
            SearchAgainFromSelection();
        }
        else
            return;
        e.Handled = true;
    }

    /// <summary>Find as you type: the first match from the start of the selection, so typing more keeps the same one.</summary>
    private void FindBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_findOpen) SearchAgainFromSelection();
    }

    private void FindOption_Click(object sender, RoutedEventArgs e) => SearchAgainFromSelection();

    private void SearchAgainFromSelection()
    {
        if (FindBox.Text.Length == 0) UpdateFindResults(reveal: false);
        else SelectMatchFrom(Editor.SelectionStart, forward: true);
    }

    private void ReplaceOne_Click(object sender, RoutedEventArgs e) => ReplaceOne();

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();

    /// <summary>The search as a regex: the text taken literally, ignoring case unless Match case is on.</summary>
    private Regex? FindRegex()
    {
        string query = FindBox.Text;
        if (query.Length == 0) return null;

        string pattern = Regex.Escape(query);
        if (WholeWordToggle.IsChecked == true) pattern = $@"(?<!\w){pattern}(?!\w)";
        var options = RegexOptions.CultureInvariant | (MatchCaseToggle.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(pattern, options);
    }

    /// <summary>The next match after the selection, or the previous one before it.</summary>
    private void SelectMatch(bool forward) =>
        SelectMatchFrom(forward ? Editor.SelectionStart + Editor.SelectionLength : Editor.SelectionStart, forward);

    /// <summary>Selects the first match at or after a position (or the last one before it), wrapping around.</summary>
    private void SelectMatchFrom(int position, bool forward)
    {
        var regex = FindRegex();
        if (regex == null) return;

        string text = Editor.Text;
        Match? match;
        bool wrapped = false;
        if (forward)
        {
            match = regex.Match(text, Math.Min(position, text.Length));
            if (!match.Success)
            {
                match = regex.Match(text);
                wrapped = match.Success && position > 0;
            }
        }
        else
        {
            var matches = regex.Matches(text);
            match = matches.LastOrDefault(m => m.Index < position);
            if (match == null)
            {
                match = matches.LastOrDefault();
                wrapped = match != null;
            }
        }

        if (match is { Success: true })
        {
            Editor.Select(match.Index, match.Length);
            RevealInEditor(match.Index);
            if (wrapped) FlashStatus(forward ? "Reached the end; continued from the top" : "Reached the start; continued from the end");
        }
        else
        {
            Editor.Select(Editor.SelectionStart, 0);   // don't leave an earlier match selected
        }
        UpdateFindResults(reveal: true);
    }

    /// <summary>Replaces the selected match, if the selection is one, then goes to the next match.</summary>
    private void ReplaceOne()
    {
        var regex = FindRegex();
        if (regex == null) return;

        int start = Editor.SelectionStart;
        var match = regex.Match(Editor.Text, start);
        if (match.Success && match.Index == start && match.Length == Editor.SelectionLength)
        {
            Editor.SelectedText = ReplaceBox.Text;   // undoable with Ctrl+Z
            Editor.Select(start + ReplaceBox.Text.Length, 0);
        }
        SelectMatch(forward: true);
    }

    /// <summary>Replaces every match as one change, so a single Ctrl+Z undoes it. The caret keeps its place.</summary>
    private void ReplaceAll()
    {
        var regex = FindRegex();
        if (regex == null) return;

        var matches = regex.Matches(Editor.Text);
        if (matches.Count == 0)
        {
            FlashStatus($"Nothing to replace: \"{FindBox.Text}\" wasn't found");
            return;
        }

        string replacement = ReplaceBox.Text;
        int caret = Editor.SelectionStart, delta = 0, newCaret = -1;
        foreach (Match m in matches)
        {
            if (m.Index >= caret) break;
            if (m.Index + m.Length > caret) { newCaret = m.Index + delta; break; }   // caret was inside this match
            delta += replacement.Length - m.Length;
        }
        if (newCaret < 0) newCaret = caret + delta;

        Editor.BeginChange();
        try
        {
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                Editor.Select(matches[i].Index, matches[i].Length);
                Editor.SelectedText = replacement;
            }
        }
        finally
        {
            Editor.EndChange();
        }
        Editor.Select(newCaret, 0);
        FlashStatus(matches.Count == 1 ? "Replaced 1 match" : $"Replaced {matches.Count:N0} matches");
        UpdateFindResults(reveal: false);
    }

    /// <summary>Scrolls the editor to an offset if it's out of view, putting it a third of the way down.</summary>
    private void RevealInEditor(int offset)
    {
        if (EditorCard.Visibility != Visibility.Visible) return;

        Editor.UpdateLayout();
        Rect rect = Editor.GetRectFromCharacterIndex(offset);
        if (rect.IsEmpty) return;
        if (rect.Top < 0 || rect.Bottom > Editor.ActualHeight)
            Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + rect.Top - Editor.ActualHeight / 3));
        if (rect.Left < 0 || rect.Right > Editor.ActualWidth)
            Editor.ScrollToHorizontalOffset(Math.Max(0, Editor.HorizontalOffset + rect.Left - Editor.ActualWidth / 3));
    }

    /// <summary>
    /// Updates the match count ("3 of 12" when a match is selected) and the preview's highlights.
    /// </summary>
    /// <param name="reveal">Scroll the preview to the current match, when it's the only pane showing.</param>
    private void UpdateFindResults(bool reveal)
    {
        var regex = FindRegex();
        if (regex == null)
        {
            FindCountText.Text = "";
            ClearPreviewFind();
            return;
        }

        string text = Editor.Text;
        var matches = regex.Matches(text);
        int selectionStart = Editor.SelectionStart, selectionLength = Editor.SelectionLength;
        int current = -1, line = 0, scanned = 0;
        var lines = new int[matches.Count];   // each match's source line, for the preview
        for (int i = 0; i < matches.Count; i++)
        {
            line += text.AsSpan(scanned, matches[i].Index - scanned).Count('\n');
            scanned = matches[i].Index;
            lines[i] = line;
            if (matches[i].Index == selectionStart && matches[i].Length == selectionLength) current = i;
        }

        // "?" once the caret has moved off the match; F3 goes on from there.
        FindCountText.Text = matches.Count == 0 ? "No results"
            : current >= 0 ? $"{current + 1:N0} of {matches.Count:N0}"
            : $"? of {matches.Count:N0}";

        // The page finds the current match from its line and the lines of the matches before it (see FindScript).
        int[] prior = current > 0 ? lines[Math.Max(0, current - 200)..current] : [];
        string args = string.Join(",", JsonSerializer.Serialize(FindBox.Text),
            MatchCaseToggle.IsChecked == true ? "true" : "false", WholeWordToggle.IsChecked == true ? "true" : "false",
            current >= 0 ? lines[current].ToString(CultureInfo.InvariantCulture) : "-1", JsonSerializer.Serialize(prior));
        _findMarkScript = $"findMark({args},false)";
        bool previewOnly = EditorCard.Visibility != Visibility.Visible;
        RunPreviewScript(reveal && previewOnly ? $"findMark({args},true)" : _findMarkScript);
    }

    private void ClearPreviewFind()
    {
        if (_findMarkScript == null) return;
        _findMarkScript = null;
        RunPreviewScript("findMark('',false,false,-1,[],false)");
    }

    // ---- View menu ----

    private void EditorOnlyView_Executed(object sender, ExecutedRoutedEventArgs e) => SetView(ViewMode.EditorOnly);

    private void SplitView_Executed(object sender, ExecutedRoutedEventArgs e) => SetView(ViewMode.Split);

    private void PreviewOnlyView_Executed(object sender, ExecutedRoutedEventArgs e) => SetView(ViewMode.PreviewOnly);

    /// <summary>Double-clicking a card's title shows only that card; double-clicking it again returns to Split.</summary>
    private void CardTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;

        bool isEditor = ReferenceEquals(sender, EditorTitle);
        bool alreadyExpanded = isEditor ? EditorOnlyItem.IsChecked : PreviewOnlyItem.IsChecked;
        SetView(alreadyExpanded ? ViewMode.Split : isEditor ? ViewMode.EditorOnly : ViewMode.PreviewOnly);
        e.Handled = true;
    }

    private void SetView(ViewMode mode)
    {
        bool showEditor = mode != ViewMode.PreviewOnly;
        bool showPreview = mode != ViewMode.EditorOnly;

        EditorColumn.Width = showEditor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PreviewColumn.Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitterColumn.Width = new GridLength(showEditor && showPreview ? 8 : 0);

        EditorCard.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        PreviewCard.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = showEditor && showPreview ? Visibility.Visible : Visibility.Collapsed;

        // The find bar sits above the editor, beside the full-height preview; in Preview Only it moves above the
        // preview, which then gives up the height instead.
        System.Windows.Controls.Grid.SetColumn(FindBar, showEditor ? 1 : 3);
        System.Windows.Controls.Grid.SetRow(PreviewCard, showEditor ? 0 : 1);
        System.Windows.Controls.Grid.SetRowSpan(PreviewCard, showEditor ? 2 : 1);

        EditorOnlyItem.IsChecked = mode == ViewMode.EditorOnly;
        SplitItem.IsChecked = mode == ViewMode.Split;
        PreviewOnlyItem.IsChecked = mode == ViewMode.PreviewOnly;
        UpdateReloadButtons();
    }

    private void ToggleSyntaxColors_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        SetSyntaxColors(!App.Settings.SyntaxColors);
        try
        {
            App.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't save the syntax color setting", ex);
        }
    }

    /// <summary>
    /// With colors on, the editor's own text is transparent and the highlight layer behind it draws the text;
    /// the caret, selection and editing all stay the TextBox's.
    /// </summary>
    private void SetSyntaxColors(bool on)
    {
        App.Settings.SyntaxColors = on;
        SyntaxColorsItem.IsChecked = on;
        Highlighter.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
            Editor.Foreground = System.Windows.Media.Brushes.Transparent;
        else
            Editor.ClearValue(ForegroundProperty);   // back to the style's theme text color
    }

    private void ToggleWordWrap_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        bool wrap = Editor.TextWrapping == TextWrapping.NoWrap;
        Editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        WordWrapItem.IsChecked = wrap;
    }

    private void ZoomIn_Executed(object sender, ExecutedRoutedEventArgs e) => SetEditorZoom(Editor.FontSize + 1);

    private void ZoomOut_Executed(object sender, ExecutedRoutedEventArgs e) => SetEditorZoom(Editor.FontSize - 1);

    private void ZoomReset_Executed(object sender, ExecutedRoutedEventArgs e) => SetEditorZoom(DefaultFontSize);

    private void SetEditorZoom(double fontSize) => Editor.FontSize = Math.Clamp(fontSize, 8, 36);

    // ---- Format menu ----

    private void FormatMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != FormatMenu) return;

        while (FormatMenu.Items[0] != StyleListSeparator)
            FormatMenu.Items.RemoveAt(0);

        int index = 0;
        foreach (var style in _styles.All)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = style.Name.Replace("_", "__"),   // a single underscore would become an access key
                IsCheckable = true,                       // reserves room for the check mark (see MenuLeafTemplate)
                IsChecked = style.Id == _style.Id,
                InputGestureText = style.Id == _styles.DefaultStyleId ? "Default" : "",
            };
            item.Click += (_, _) => ApplyStyle(style);
            FormatMenu.Items.Insert(index++, item);
        }
    }

    private void CustomizeStyles_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var editor = new StyleEditorWindow(_styles, _style) { Owner = this };
        if (editor.ShowDialog() != true) return;

        SaveStyles();
        if (editor.UseForDocument && _styles.Find(editor.SelectedStyleId) is { } chosen)
        {
            ApplyStyle(chosen);
        }
        else
        {
            // Pick up edits to the current style, or fall back to the default if it was deleted.
            _style = _styles.Find(_style.Id) ?? _styles.Default;
            UpdateStyleDisplay();
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string theme) return;

        App.ApplyTheme(theme);
        App.Settings.Theme = theme;
        UpdateThemeChecks();
        try
        {
            App.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't save the theme setting", ex);
        }
    }

    private void FileAssociation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Register();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            ShowError("Couldn't register LandMark with Windows", ex);
            return;
        }

        const string moveNote = "If you move LandMark.exe later, run it once from its new folder to keep double-clicking working.";
        if (FileAssociation.IsDefault())
        {
            MessageDialog.Show(this, MessageDialog.Kind.Info, ".md files now open in LandMark",
                $"Double-clicking a Markdown file now opens it in LandMark.\n\n{moveNote}", primary: "OK");
            return;
        }

        bool openSettings = MessageDialog.Show(this, MessageDialog.Kind.Info, "One more step in Windows",
            "LandMark now appears in \"Open with\" for .md, .markdown, .mdown and .mkd files, but another app is still " +
            "the default.\n\nWindows only lets you change the default yourself. Select Open Default Apps, then select " +
            ".md (and any other Markdown types) and choose LandMark. Or right-click a .md file, choose Open with › " +
            $"Choose another app, select LandMark and then Always.\n\n{moveNote}",
            primary: "Open Default Apps", secondary: "Close");
        if (!openSettings) return;

        try
        {
            FileAssociation.OpenDefaultAppsSettings();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError("Couldn't open Default apps", ex, "Open Settings › Apps › Default apps and search for LandMark.");
        }
    }

    private void Margins_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string margins) return;

        App.Settings.PageMargins = margins;
        UpdateMarginChecks();
        try
        {
            App.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't save the page margin setting", ex);
        }
    }

    private void UpdateMarginChecks()
    {
        bool narrow = App.Settings.PageMargins == "Narrow";
        MarginsNarrowItem.IsChecked = narrow;
        MarginsWideItem.IsChecked = !narrow;
    }

    private void SyncMenuHighlight()
    {
        if (Tag is System.Windows.Media.Color textColor)
            App.UpdateMenuHighlight(textColor);
    }

    private void UpdateThemeChecks()
    {
        string theme = App.Settings.Theme;
        ThemeLightItem.IsChecked = theme == "Light";
        ThemeDarkItem.IsChecked = theme == "Dark";
        ThemeSystemItem.IsChecked = theme is not ("Light" or "Dark");
    }

    private void SetDefaultStyle_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _styles.DefaultStyleId = _style.Id;
        SaveStyles();
    }

    /// <summary>Uses a style for the current document and remembers the choice for that file.</summary>
    private void ApplyStyle(DocumentStyle style)
    {
        _style = style;
        if (_currentPath != null)
        {
            _styles.Remember(_currentPath, style);
            SaveStyles();
        }
        else
        {
            _styleChosen = true;   // remembered when the document is first saved
        }
        UpdateStyleDisplay();
    }

    private void UpdateStyleDisplay()
    {
        PreviewStyleText.Text = $"Style: {_style.Name}";
        SchedulePreview();
    }

    private void SaveStyles()
    {
        try
        {
            _styles.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't save style settings", ex);
        }
    }

    // ---- Help menu ----

    private void ToggleCheatSheet_Executed(object sender, ExecutedRoutedEventArgs e) => SetCheatSheetOpen(!_cheatSheetOpen);

    private void SetCheatSheetOpen(bool open)
    {
        _cheatSheetOpen = open;
        CheatSheetItem.IsChecked = open;
        SlidePanel(CheatSheetPanel, open, 338, () => _cheatSheetOpen);   // card is 330 + 8 margin
    }

    /// <summary>Slides a docked side panel in or out by animating its clipping border's width.</summary>
    /// <param name="isOpen">Read when a close animation ends, in case the panel was reopened meanwhile.</param>
    private static void SlidePanel(System.Windows.Controls.Border panel, bool open, double width, Func<bool> isOpen)
    {
        if (open) panel.Visibility = Visibility.Visible;

        // Like the menus, follow Windows' animation setting, and skip it without graphics hardware
        // (each frame would also resize the preview's web view).
        if (!SystemParameters.ClientAreaAnimation || App.IsSoftwareRendered)
        {
            panel.BeginAnimation(WidthProperty, null);
            panel.Width = open ? width : 0;
            if (!open) panel.Visibility = Visibility.Collapsed;
            return;
        }

        var slide = new DoubleAnimation(open ? width : 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (!open)
            slide.Completed += (_, _) => { if (!isOpen()) panel.Visibility = Visibility.Collapsed; };
        panel.BeginAnimation(WidthProperty, slide);
    }

    // ---- Outline ----

    private void ToggleOutline_Executed(object sender, ExecutedRoutedEventArgs e) => SetOutlineOpen(!_outlineOpen);

    private void SetOutlineOpen(bool open)
    {
        _outlineOpen = open;
        OutlineItem.IsChecked = open;
        if (open)
        {
            UpdateOutline(MarkdownParser.Parse(Editor.Text));
            HighlightCurrentHeading();
        }
        SlidePanel(OutlinePanel, open, 288, () => _outlineOpen);   // card is 280 + 8 margin
    }

    /// <summary>Updates the list in place, so its scroll position and the current heading survive typing.</summary>
    private void UpdateOutline(IReadOnlyList<Block> blocks)
    {
        var entries = DocumentOutline.Build(blocks);
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == _outline.Count) _outline.Add(entries[i]);
            else if (_outline[i] != entries[i]) _outline[i] = entries[i];
        }
        while (_outline.Count > entries.Count)
            _outline.RemoveAt(_outline.Count - 1);
        OutlineEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HighlightCurrentHeading();
    }

    /// <summary>Selects the heading the caret is under. Selection only highlights; clicking or Enter navigates.</summary>
    private void HighlightCurrentHeading()
    {
        if (!_outlineOpen) return;

        int caretLine = Highlighter.LineFromOffset(Editor.CaretIndex);
        var current = _outline.LastOrDefault(entry => entry.Line <= caretLine);
        if (ReferenceEquals(current, OutlineList.SelectedItem)) return;
        OutlineList.SelectedItem = current;
        if (current != null) OutlineList.ScrollIntoView(current);
    }

    private void OutlineList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && System.Windows.Controls.ItemsControl.ContainerFromElement(OutlineList, source) is System.Windows.Controls.ListBoxItem { DataContext: OutlineEntry entry })
        {
            GoToLine(entry.Line);
        }
    }

    private void OutlineList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || OutlineList.SelectedItem is not OutlineEntry entry) return;
        e.Handled = true;
        GoToLine(entry.Line);
    }

    /// <summary>Puts the caret at the start of a source line and scrolls it near the top of the editor.</summary>
    private void GoToLine(int line)
    {
        if (EditorCard.Visibility != Visibility.Visible)
        {
            // Preview Only: scroll the preview instead.
            RunPreviewScript($"scrollToLine({line},0.05,false)");
            return;
        }

        int offset = OffsetOfLine(Editor.Text, line);
        Editor.Select(offset, 0);
        Editor.Focus();
        Editor.UpdateLayout();
        var rect = Editor.GetRectFromCharacterIndex(offset);
        if (!rect.IsEmpty)
            Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + rect.Top - 8));
    }

    /// <summary>
    /// Inserts the example at the caret, wrapping any selected text, and selects the part to type over.
    /// Line-start syntax (headings, lists, quotes) is applied to the current or selected lines instead.
    /// </summary>
    private void CheatSheetEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CheatSheetEntry { CanInsert: true } entry) return;
        if (EditorCard.Visibility != Visibility.Visible) SetView(ViewMode.Split);

        if (entry.LinePrefix is { } prefix && !(Editor.SelectionLength == 0 && CaretLineIsBlank()))
        {
            ApplyLinePrefix(prefix);
            return;
        }

        string content = Editor.SelectionLength > 0 ? Editor.SelectedText : entry.Placeholder;
        string template = entry.Template.Replace("\n", Environment.NewLine);
        int contentOffset = template.IndexOf("{0}", StringComparison.Ordinal);
        string text = template.Replace("{0}", content);

        int start = Editor.SelectionStart;
        if (entry.Block && start > 0 && Editor.Text[start - 1] != '\n')
        {
            text = Environment.NewLine + text;   // block syntax only works at the start of a line
            contentOffset += Environment.NewLine.Length;
        }

        Editor.SelectedText = text;   // replaces the selection; stays undoable with Ctrl+Z
        if (contentOffset >= 0)
            Editor.Select(start + contentOffset, content.Length);
        else
            Editor.Select(start + text.Length, 0);
        Editor.Focus();
    }

    private bool CaretLineIsBlank()
    {
        string text = Editor.Text;
        int start = LineStart(text, Editor.SelectionStart);
        int end = text.IndexOf('\n', Editor.SelectionStart);
        return string.IsNullOrWhiteSpace(text[start..(end < 0 ? text.Length : end)]);
    }

    /// <summary>Puts a prefix such as "## " or "- " at the start of each current/selected line.</summary>
    private void ApplyLinePrefix(string prefix)
    {
        string text = Editor.Text;
        int start = LineStart(text, Editor.SelectionStart);
        int selectionEnd = Editor.SelectionStart + Editor.SelectionLength;
        if (Editor.SelectionLength > 0 && text[selectionEnd - 1] == '\n')
            selectionEnd--;   // a selection ending at a line break doesn't include the next line
        int end = text.IndexOf('\n', selectionEnd);
        end = end < 0 ? text.Length : end;
        if (end > start && text[end - 1] == '\r') end--;

        bool heading = prefix.StartsWith('#');
        var lines = text[start..end].Split('\n').Select(line =>
        {
            string body = line.TrimEnd('\r');
            if (body.Trim().Length == 0) return line;                     // leave blank lines alone
            if (heading) body = ExistingHeadingMarker().Replace(body, ""); // switching levels replaces the old #s
            return prefix + body + (line.EndsWith('\r') ? "\r" : "");
        });
        string replacement = string.Join("\n", lines);

        Editor.Select(start, end - start);
        Editor.SelectedText = replacement;   // one undoable change
        Editor.Select(start + replacement.Length, 0);
        Editor.Focus();
    }

    private static int LineStart(string text, int position) =>
        position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;

    /// <summary>Where a source line starts; the end of the text for a line past the last one.</summary>
    private static int OffsetOfLine(string text, int line)
    {
        int offset = 0;
        for (int i = 0; i < line; i++)
        {
            int next = text.IndexOf('\n', offset);
            if (next < 0) return text.Length;
            offset = next + 1;
        }
        return offset;
    }

    [GeneratedRegex(@"^ {0,3}#{1,6}(?: +|$)")]
    private static partial Regex ExistingHeadingMarker();

    private void About_Executed(object sender, ExecutedRoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // ---- Preview ----

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Compared with the saved text rather than just set, so undoing (or retyping) back to it clears the dot.
        // Comparing a document of a few hundred KB per keystroke is negligible next to re-rendering the preview.
        if (!_settingText) UpdateDirty();
        SchedulePreview();
    }

    /// <summary>
    /// Unsaved means the text differs from the file as last opened or saved, or the file is gone from disk (so
    /// closing asks to save it rather than losing it).
    /// </summary>
    private void UpdateDirty()
    {
        bool dirty = _fileMissing || !string.Equals(Editor.Text, _savedText, StringComparison.Ordinal);
        if (dirty == _isDirty) return;
        _isDirty = dirty;
        UpdateTitle();
        UpdateReloadButtons();   // Merge shows only with unsaved changes
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    // ---- Scroll sync and line marker ----

    /// <summary>Called with the caret line's row on screen, which the preview page posts from report().</summary>
    private void MoveLineMarker(double top, double height, bool visible, double viewportHeight)
    {
        const double inset = 2;   // the page's border + padding above the web view
        double scale = viewportHeight > 0 ? Preview.ActualHeight / viewportHeight : 1;   // page pixels → WPF units (differs at high DPI)
        double start = Math.Max(0, top * scale);
        double end = Math.Min(Preview.ActualHeight, (top + height) * scale);

        if (!visible || end - start < 2)
        {
            LineMarker.Visibility = Visibility.Collapsed;
            return;
        }
        System.Windows.Controls.Canvas.SetTop(LineMarker, inset + start);
        LineMarker.Height = end - start;
        LineMarker.Visibility = Visibility.Visible;
    }

    /// <summary>Throttled (not debounced), so the preview follows along while the editor scrolls.</summary>
    private void ScheduleScrollSync()
    {
        if (!_scrollSyncTimer.IsEnabled) _scrollSyncTimer.Start();
    }

    /// <summary>
    /// Scrolls the preview so the editor's caret line (or, if the caret is off screen, the top visible
    /// line) appears at the same height in both panes.
    /// </summary>
    private void SyncPreviewScroll()
    {
        if (!_previewLoaded || Editor.ActualHeight <= 0
            || EditorCard.Visibility != Visibility.Visible || PreviewCard.Visibility != Visibility.Visible)
        {
            LineMarker.Visibility = Visibility.Collapsed;   // nothing to mark without both panes
            return;
        }

        int anchor = Editor.CaretIndex;
        Rect rect = Editor.GetRectFromCharacterIndex(anchor);
        bool caretVisible = !rect.IsEmpty && rect.Top >= 0 && rect.Bottom <= Editor.ActualHeight;
        if (!caretVisible)
        {
            anchor = Editor.GetCharacterIndexFromPoint(new Point(8, 8), snapToText: true);
            if (anchor < 0) return;
            rect = Editor.GetRectFromCharacterIndex(anchor);
        }

        string text = Editor.Text;
        int line = text.AsSpan(0, Math.Min(anchor, text.Length)).Count('\n');
        double fraction = rect.IsEmpty ? 0 : Math.Clamp(rect.Top / Editor.ActualHeight, 0, 1);
        // The line marker only shows while the caret is on screen; otherwise it would mark an arbitrary line.
        // The page answers with a message (Preview_WebMessageReceived).
        RunPreviewScript(string.Create(CultureInfo.InvariantCulture, $"scrollToLine({line},{fraction},{(caretVisible ? "true" : "false")})"));
    }

    /// <summary>
    /// Starts the preview's browser (the WebView2 Runtime, part of Windows 11) and loads the empty page.
    /// Its profile lives in %AppData%\LandMark\WebView2, since the exe's own folder may not be writable.
    /// </summary>
    private async Task StartPreviewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(AppDataFolder.Path, "WebView2"));
            await Preview.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowPreviewUnavailable("The preview needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11 " +
                                   "and Microsoft Edge. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703, or ask IT.");
            return;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowPreviewUnavailable($"The preview couldn't start: {ex.Message}");
            return;
        }

        var core = Preview.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;   // no reload, find or print; copy and select all still work
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += Preview_NavigationStarting;
        core.NewWindowRequested += Preview_NewWindowRequested;
        core.WebMessageReceived += Preview_WebMessageReceived;
        core.NavigationCompleted += Preview_NavigationCompleted;
        core.AddWebResourceRequestedFilter(LocalImageHost + "*", CoreWebView2WebResourceContext.Image);
        core.WebResourceRequested += Preview_WebResourceRequested;
        core.NavigateToString(EmailHtml.Page(""));
    }

    /// <summary>
    /// The preview is a data: page, so it has no folder for relative image paths and can't load file: URLs.
    /// Local images get a URL on this made-up host instead (see PreviewImageUrl), which Preview_WebResourceRequested
    /// answers from disk. The .example domain is reserved, so it can never be a real site.
    /// </summary>
    private const string LocalImageHost = "https://local-image.landmark.example/";

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
    };

    /// <summary>
    /// A local image's preview URL: relative paths are relative to the document's folder (so an unsaved document
    /// can only show absolute paths). The file's last-write time is in the URL, so an image that's been
    /// replaced on disk shows the new one on the next refresh, while unchanged ones come from the browser's cache.
    /// </summary>
    private string? PreviewImageUrl(string url)
    {
        try
        {
            string path;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile) return null;   // some other scheme (data:, mailto:...)
                path = uri.LocalPath;
            }
            else
            {
                if (Path.GetDirectoryName(_currentPath) is not { Length: > 0 } folder) return null;
                path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(url)));   // %20 for spaces, as on GitHub
            }

            if (!ImageTypes.ContainsKey(Path.GetExtension(path))) return null;
            long version = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;   // a missing file shows as a broken image
            return string.Create(CultureInfo.InvariantCulture, $"{LocalImageHost}{Uri.EscapeDataString(path)}?v={version}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Preview_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (Preview.CoreWebView2 is not { } core) return;

        string address = e.Request.Uri;
        if (!address.StartsWith(LocalImageHost, StringComparison.OrdinalIgnoreCase)) return;
        int query = address.IndexOf('?');
        string path = Uri.UnescapeDataString(address[LocalImageHost.Length..(query < 0 ? address.Length : query)]);

        try
        {
            // Only image files, and only full paths: the page can't use this to read anything else.
            if (Path.IsPathFullyQualified(path) && ImageTypes.TryGetValue(Path.GetExtension(path), out var type) && File.Exists(path))
            {
                // Read now, so the file isn't held open while the browser gets round to the stream.
                var content = new MemoryStream(File.ReadAllBytes(path));
                e.Response = core.Environment.CreateWebResourceResponse(content, 200, "OK",
                    $"Content-Type: {type}\r\nCache-Control: max-age=31536000");   // the URL changes when the file does
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Fall through to Not Found.
        }
        e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private void ShowPreviewUnavailable(string message)
    {
        Preview.Visibility = Visibility.Collapsed;
        PreviewUnavailableText.Text = message;
        PreviewUnavailableText.Visibility = Visibility.Visible;
    }

    private void Preview_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_previewLoaded) return;
        _previewLoaded = true;
        RefreshPreview(MarkdownParser.Parse(Editor.Text));
    }

    private void RefreshPreview(IReadOnlyList<Block> blocks)
    {
        if (!_previewLoaded) return;
        _ = SetPreviewContentAsync(blocks);
        if (_findMarkScript != null) RunPreviewScript(_findMarkScript);   // setContent dropped the highlights
        SyncPreviewScroll();   // runs after setContent: scripts run in the order they're sent
    }

    /// <summary>Swaps the page's body (rather than reloading), which keeps the scroll position and avoids a flash.</summary>
    private Task SetPreviewContentAsync(IReadOnlyList<Block> blocks)
    {
        string fragment = EmailHtml.Fragment(blocks, _style, sourceLines: true, localImage: PreviewImageUrl);
        return Preview.CoreWebView2?.ExecuteScriptAsync($"setContent({JsonSerializer.Serialize(fragment)})") ?? Task.CompletedTask;
    }

    /// <summary>Runs a preview helper without waiting; a script sent while the page is busy is simply dropped.</summary>
    private void RunPreviewScript(string script)
    {
        if (!_previewLoaded || Preview.CoreWebView2 is not { } core) return;
        try
        {
            _ = core.ExecuteScriptAsync(script);
        }
        catch (InvalidOperationException)
        {
            // The browser is closing; the next caret move or scroll will sync it.
        }
    }

    private void Preview_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "marker":
                    MoveLineMarker(root.GetProperty("top").GetDouble(), root.GetProperty("height").GetDouble(),
                        root.GetProperty("visible").GetBoolean(), root.GetProperty("viewport").GetDouble());
                    break;
                case "scroll":
                    ScrollEditorFromPreview(root.GetProperty("line").GetDouble(),
                        root.GetProperty("atTop").GetBoolean(), root.GetProperty("atEnd").GetBoolean());
                    break;
                case "select":
                    SelectEditorFromPreview(root.GetProperty("line").GetInt32(), root.GetProperty("first").GetInt32(),
                        root.GetProperty("end").GetInt32(), root.GetProperty("text").GetString() ?? "",
                        root.GetProperty("nth").GetInt32(), root.GetProperty("fraction").GetDouble());
                    break;
                case "link":
                    // Dispatched: a dialog (save changes?) shouldn't open inside the browser's message callback.
                    string href = root.GetProperty("href").GetString() ?? "";
                    Dispatcher.BeginInvoke(() => FollowLink(href));
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Not a message from the preview script.
        }
    }

    private bool BothPanesVisible =>
        EditorCard.Visibility == Visibility.Visible && PreviewCard.Visibility == Visibility.Visible;

    /// <summary>The user scrolled the preview: put the source line at its top at the top of the editor. The caret stays.</summary>
    private void ScrollEditorFromPreview(double line, bool atTop, bool atEnd)
    {
        if (!BothPanesVisible || double.IsNaN(line)) return;

        _previewDrivingUntil = Environment.TickCount64 + 200;
        if (atTop) { Editor.ScrollToHome(); return; }
        if (atEnd) { Editor.ScrollToEnd(); return; }

        string text = Editor.Text;
        int whole = (int)Math.Max(0, Math.Floor(line));
        Rect rect = Editor.GetRectFromCharacterIndex(OffsetOfLine(text, whole));
        if (rect.IsEmpty) return;

        // Part-way through a line: interpolate to the next line's top, which allows for a wrapped line's height.
        double y = rect.Top;
        Rect next = Editor.GetRectFromCharacterIndex(OffsetOfLine(text, whole + 1));
        y += (line - whole) * (!next.IsEmpty && next.Top > rect.Top ? next.Top - rect.Top : rect.Height);
        Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + y));
    }

    /// <summary>
    /// The user clicked or selected in the preview: select the same text in the source and bring it to the same
    /// height. The page only knows which block it was (source lines first up to end), so the text is searched for
    /// there, taking the nth occurrence; if it isn't in the source as written (it spans formatting, say), the
    /// clicked line is selected instead. Focus stays in the preview, so its own selection can still be copied.
    /// </summary>
    private void SelectEditorFromPreview(int line, int first, int end, string text, int nth, double fraction)
    {
        if (!BothPanesVisible || line < 0 || first < 0) return;

        string source = Editor.Text;
        int start = -1, length = 0;
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            int from = OffsetOfLine(source, first);
            int to = end > first ? OffsetOfLine(source, end) : source.Length;
            // Any run of white space matches any other, so a selection can cross a line break in the source.
            var matches = Regex.Matches(source[from..to], string.Join(@"\s+", words.Select(Regex.Escape)));
            if (matches.Count > 0)
            {
                var match = matches[nth >= 0 && nth < matches.Count ? nth : 0];
                start = from + match.Index;
                length = match.Length;
            }
        }
        if (start < 0)
        {
            start = OffsetOfLine(source, line);
            int lineEnd = source.IndexOf('\n', start);
            if (lineEnd < 0) lineEnd = source.Length;
            if (lineEnd > start && source[lineEnd - 1] == '\r') lineEnd--;
            length = lineEnd - start;
        }

        _previewDrivingUntil = Environment.TickCount64 + 200;
        _selectingFromPreview = true;
        Editor.Select(start, length);
        _selectingFromPreview = false;
        _editorSelectionFromPreview = true;   // also when the selection didn't change
        Rect rect = Editor.GetRectFromCharacterIndex(start);
        if (!rect.IsEmpty)
            Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + rect.Top - Math.Clamp(fraction, 0, 1) * Editor.ActualHeight));

        // The page hasn't moved, so only the marker needs to follow.
        RunPreviewScript($"markLine({source.AsSpan(0, start).Count('\n')})");
    }

    /// <summary>
    /// The page itself is loaded with NavigateToString (a data: URL); nothing else may replace it. Clicked links
    /// normally never get here (the page sends them to FollowLink), so this is a file dropped on the preview, or a
    /// link that got past the page. A relative link the page couldn't resolve arrives as about:blank#blocked,
    /// which OpenLink ignores.
    /// </summary>
    private void Preview_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        OpenLink(e.Uri);
    }

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".markdown", ".mdown", ".mkd" };

    /// <summary>Files a link won't start: a document shouldn't be able to run a program with one click.</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".scr", ".pif", ".cpl", ".lnk", ".url", ".reg", ".hta",
        ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".jar", ".appref-ms", ".application",
    };

    /// <summary>
    /// A link clicked in the preview, with its href as written in the Markdown. Web and mail links open in the
    /// default browser or mail app. Anything else is a file: relative to the document's folder unless it's a full
    /// path or a file: URL. Markdown files open here; other files and folders open with their Windows program.
    /// A "#heading" part is ignored for now.
    /// </summary>
    private void FollowLink(string href)
    {
        href = href.Trim();
        if (href.Length == 0 || href.StartsWith('#')) return;

        string path;
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            if (uri.Scheme is "http" or "https" or "mailto")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else
                FlashStatus($"LandMark doesn't open {uri.Scheme}: links");
            return;
        }

        try
        {
            if (uri != null)
            {
                path = uri.LocalPath;
            }
            else
            {
                if (Path.GetDirectoryName(_currentPath) is not { Length: > 0 } folder)
                {
                    FlashStatus("Save the document first: relative links start from its folder");
                    return;
                }
                int cut = href.IndexOfAny(['#', '?']);
                if (cut >= 0) href = href[..cut];
                path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(href)));   // %20 for spaces
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            FlashStatus($"Not a valid link: {href}");
            return;
        }

        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        else if (!File.Exists(path))
        {
            FlashStatus($"Link target not found: {path}");
        }
        else if (MarkdownExtensions.Contains(Path.GetExtension(path)))
        {
            OpenFile(path);
        }
        else if (BlockedExtensions.Contains(Path.GetExtension(path)))
        {
            FlashStatus($"LandMark doesn't run programs from links: {Path.GetFileName(path)}");
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                ShowError($"Couldn't open {Path.GetFileName(path)}", ex);
            }
        }
    }

    /// <summary>Links with target="_blank" or Ctrl+clicked, which would open a browser window.</summary>
    private void Preview_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenLink(e.Uri);
    }

    private void OpenLink(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return;

        if (uri.IsFile)
        {
            // Dispatched, so the browser finishes cancelling its navigation before a dialog can appear.
            Dispatcher.BeginInvoke(() => OpenFile(uri.LocalPath));
        }
        else if (uri.Scheme is "http" or "https" or "mailto")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    // ---- File on disk ----
    // LandMark doesn't lock the open file: it reads it whole and closes it, and only opens it again to save. So
    // other programs can change it at any time. A watcher notices, and the user chooses whether to reload; if they
    // don't, a yellow refresh button stays in the header until they do, and saving asks before overwriting.

    /// <summary>Watches the open file for changes by other programs (or stops watching, for a new document).</summary>
    private void WatchFile(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        _diskCheckTimer.Stop();
        _diskChanged = false;
        _fileMissing = false;
        _diskReadRetries = 0;
        if (path == null) return;

        try
        {
            string fullPath = Path.GetFullPath(path);
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            // Events arrive on a worker thread, often several per save; each one restarts a short wait, then the file
            // is read once and compared with the text LandMark last had from it.
            watcher.Changed += (_, _) => Dispatcher.BeginInvoke(ScheduleDiskCheck);
            watcher.Created += (_, _) => Dispatcher.BeginInvoke(ScheduleDiskCheck);
            watcher.Deleted += (_, _) => Dispatcher.BeginInvoke(ScheduleDiskCheck);
            watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleDiskCheck);   // includes "safe saves" that rename a temp file into place
            watcher.Error += (_, _) => Dispatcher.BeginInvoke(ScheduleDiskCheck);     // too many events at once; just look
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Can't watch this location (a missing folder, or a drive that doesn't support it). Saving still checks.
        }
    }

    private void ScheduleDiskCheck()
    {
        if (_watcher == null) return;   // a late event from a file that's no longer open
        _diskCheckTimer.Stop();
        _diskCheckTimer.Start();
    }

    private void CheckFileOnDisk()
    {
        if (_currentPath == null) return;

        string? text;
        try
        {
            text = File.Exists(_currentPath) ? File.ReadAllText(_currentPath) : null;
        }
        catch (IOException) when (_diskReadRetries++ < 10)
        {
            _diskCheckTimer.Start();   // the other program is probably still writing it; try again shortly
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        _diskReadRetries = 0;

        if (text == null)
        {
            if (_fileMissing) return;
            _fileMissing = true;
            _diskChanged = false;   // nothing to reload
            UpdateDirty();
            UpdateTitle();
            UpdateReloadButtons();
            NotifyWhenActive(NotifyFileMissing);
            return;
        }

        bool wasMissing = _fileMissing;
        _fileMissing = false;
        if (text == _diskText)
        {
            // Our own save, a change that left the text the same, or the file put back as it was.
            if (wasMissing)
            {
                UpdateDirty();
                UpdateTitle();
            }
            return;
        }

        if (wasMissing) UpdateDirty();
        if (_diskChanged) return;   // already asked; the refresh button is showing
        _diskChanged = true;
        UpdateTitle();
        UpdateReloadButtons();
        NotifyWhenActive(AskToReload);
    }

    /// <summary>
    /// Shows a notice now if this window is in front; otherwise when it's next activated, so a message box doesn't
    /// pop up over another program, or stack on one of this window's own dialogs.
    /// </summary>
    private void NotifyWhenActive(Action notice)
    {
        if (!IsActive || _noticeShowing)
        {
            _pendingNotice = notice;
            return;
        }

        _pendingNotice = null;
        _noticeShowing = true;
        try
        {
            notice();
        }
        finally
        {
            _noticeShowing = false;
        }
        // Another notice that arrived meanwhile (activation while the message box closed came too early for it).
        if (_pendingNotice != null) Dispatcher.BeginInvoke(ShowPendingNotice);
    }

    private void ShowPendingNotice()
    {
        if (_pendingNotice is not { } notice) return;
        _pendingNotice = null;
        NotifyWhenActive(notice);
    }

    private void AskToReload()
    {
        if (!_diskChanged) return;   // resolved meanwhile

        if (!_isDirty)
        {
            if (MessageDialog.Show(this, MessageDialog.Kind.Reload, "File changed outside LandMark",
                    $"{DocumentName} was changed by another program.\n\n" +
                    "Not now keeps the version shown here; the yellow refresh button reloads it whenever you're ready.",
                    primary: "Reload", secondary: "Not now"))
                ReloadFromDisk();
            return;
        }

        // With unsaved changes here too, Merge keeps both sets of changes, so it's the highlighted choice.
        var choice = MessageDialog.Ask(this, MessageDialog.Kind.Reload, "File changed outside LandMark",
            $"{DocumentName} was changed by another program, and you have unsaved changes here.\n\n" +
            "Merge keeps both: their changes and yours. Where you both changed the same lines, both versions are kept, " +
            "marked for you to choose. Reload replaces your changes with the file on disk. Ctrl+Z undoes either.\n\n" +
            "Not now keeps your version; the yellow buttons beside the file name merge or reload whenever you're ready.",
            primary: "Merge", secondary: "Reload", cancel: "Not now");
        if (choice == MessageDialog.Result.Primary) MergeWithDisk();
        else if (choice == MessageDialog.Result.Secondary) ReloadFromDisk();
    }

    private void NotifyFileMissing()
    {
        if (!_fileMissing) return;
        MessageDialog.Show(this, MessageDialog.Kind.Warning, "File deleted or moved",
            $"{DocumentName} was deleted, moved or renamed outside LandMark. Its text is still open here.\n\n" +
            "Save (Ctrl+S) to put the file back, or use Save As to keep it somewhere else.",
            primary: "OK");
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadFromDisk();

    private void Merge_Click(object sender, RoutedEventArgs e) => MergeWithDisk();

    /// <summary>The file's current text, or null (after saying why) if it can't be read.</summary>
    private string? ReadFileOnDisk(string failure)
    {
        if (_currentPath == null) return null;
        try
        {
            return File.ReadAllText(_currentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(failure, ex);
            return null;
        }
    }

    /// <summary>Replaces the editor's text with the file's. Ctrl+Z brings back what was there.</summary>
    private void ReloadFromDisk()
    {
        if (ReadFileOnDisk("Couldn't reload the file") is not { } text) return;

        bool hadChanges = _isDirty;
        ReplaceEditorText(text);
        _savedText = Editor.Text;
        MarkInSyncWithDisk(text);
        FlashStatus(hadChanges ? $"Reloaded {DocumentName}. Ctrl+Z brings back your unsaved version." : $"Reloaded {DocumentName}");
    }

    /// <summary>
    /// Combines the changes made on disk with the unsaved changes here (see TextMerge), measured from the text as
    /// last opened, saved or reloaded. The result stays unsaved, for review. Like a reload, it's one undoable change.
    /// </summary>
    private void MergeWithDisk()
    {
        if (ReadFileOnDisk("Couldn't merge the file") is not { } theirs) return;

        var merge = TextMerge.Merge(original: _diskText, mine: Editor.Text, theirs: theirs);
        ReplaceEditorText(merge.Text);
        _savedText = theirs;   // the file on disk is now their version; the merged text differs from it until saved
        MarkInSyncWithDisk(theirs);

        if (EditorCard.Visibility == Visibility.Visible) Editor.Focus();   // so Ctrl+Z undoes the merge straight away
        if (merge.Conflicts == 0)
        {
            FlashStatus($"Merged the changes to {DocumentName} with yours. Ctrl+Z undoes the merge.");
            return;
        }

        // Select the first conflict so it's in view, and say how to settle them.
        Editor.Select(merge.FirstConflictStart, merge.FirstConflictLength);
        RevealInEditor(merge.FirstConflictStart);
        string places = merge.Conflicts == 1 ? "1 place" : $"{merge.Conflicts} places";
        MessageDialog.Show(this, MessageDialog.Kind.Info, "Merged, with lines to choose",
            $"You and the other program changed the same lines in {places}. Both versions are kept there, " +
            "between marker lines; the first one is selected:\n\n" +
            $"{TextMerge.MineMarker}\n(your lines)\n{TextMerge.SeparatorMarker}\n(their lines)\n{TextMerge.TheirsMarker}\n\n" +
            "Keep the lines you want and delete the rest, including the three marker lines, then save. " +
            "Find (Ctrl+F) \"<<<<<<<\" goes to each one. Ctrl+Z undoes the whole merge.",
            primary: "OK");
    }

    /// <summary>
    /// Replaces all of the editor's text as one undoable change, keeping the caret and scroll position as close
    /// as the new text allows. The caller sets _savedText; until then the unsaved-changes dot isn't updated.
    /// </summary>
    private void ReplaceEditorText(string text)
    {
        int caret = Editor.CaretIndex;
        double verticalOffset = Editor.VerticalOffset, horizontalOffset = Editor.HorizontalOffset;

        _settingText = true;
        Editor.BeginChange();
        try
        {
            Editor.Select(0, Editor.Text.Length);
            Editor.SelectedText = text;
        }
        finally
        {
            Editor.EndChange();
            _settingText = false;
        }
        Editor.Select(Math.Min(caret, Editor.Text.Length), 0);
        Editor.ScrollToVerticalOffset(verticalOffset);
        Editor.ScrollToHorizontalOffset(horizontalOffset);
    }

    /// <summary>After a reload or merge: the file on disk is known again, so the yellow buttons go.</summary>
    private void MarkInSyncWithDisk(string diskText)
    {
        _diskText = diskText;
        _diskChanged = false;
        _fileMissing = false;
        UpdateDirty();
        UpdateTitle();
        UpdateReloadButtons();
    }

    /// <summary>
    /// The yellow buttons go in the editor header, or in the preview header when that's the only pane. Merge only
    /// shows with unsaved changes; without any, merging would be the same as reloading.
    /// </summary>
    private void UpdateReloadButtons()
    {
        bool editorShown = EditorCard.Visibility == Visibility.Visible;
        bool canMerge = _diskChanged && _isDirty;

        EditorReloadButton.ToolTip = PreviewReloadButton.ToolTip = _isDirty
            ? $"{DocumentName} was changed outside LandMark. Click to reload it, replacing your unsaved changes (Ctrl+Z brings them back)."
            : $"{DocumentName} was changed outside LandMark. Click to reload it.";
        EditorMergeButton.ToolTip = PreviewMergeButton.ToolTip =
            $"{DocumentName} was changed outside LandMark. Click to merge those changes with your unsaved ones (Ctrl+Z undoes it).";

        EditorReloadButton.Visibility = _diskChanged && editorShown ? Visibility.Visible : Visibility.Collapsed;
        PreviewReloadButton.Visibility = _diskChanged && !editorShown ? Visibility.Visible : Visibility.Collapsed;
        EditorMergeButton.Visibility = canMerge && editorShown ? Visibility.Visible : Visibility.Collapsed;
        PreviewMergeButton.Visibility = canMerge && !editorShown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Right-clicking the path in the footer copies it.</summary>
    private void StatusText_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_currentPath == null) return;
        e.Handled = true;

        string path = _currentPath;
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, path);
        SetClipboard(data, "Copied the file path");
    }

    // ---- Helpers ----

    private string DocumentName => _currentPath != null ? Path.GetFileName(_currentPath) : "Untitled";

    private void UpdateTitle()
    {
        // The title bar stays "LandMark"; the editor header shows the file name, with a yellow dot while there are
        // unsaved changes. The footer repeats the dot beside the path, so it shows in Preview Only too.
        var dotVisibility = _isDirty ? Visibility.Visible : Visibility.Collapsed;
        string dotTip = _fileMissing
            ? "The file was deleted or moved outside LandMark. Press Ctrl+S to save it again."
            : "Unsaved changes. Press Ctrl+S to save.";
        UnsavedDot.Visibility = FooterUnsavedDot.Visibility = dotVisibility;
        UnsavedDot.ToolTip = FooterUnsavedDot.ToolTip = dotTip;
        FileNameText.Text = DocumentName;
        FileNameText.ToolTip = _currentPath;

        if (!_statusTimer.IsEnabled) ShowDocumentStatus();   // don't cut a confirmation short
    }

    /// <summary>The status bar's normal content: where the document lives.</summary>
    private void ShowDocumentStatus()
    {
        StatusText.Text = _currentPath == null ? "New document"
            : _fileMissing ? $"{_currentPath}  (deleted or moved)"
            : _currentPath;
        StatusText.ToolTip = _currentPath == null ? null : $"{_currentPath}\nRight-click to copy the path";
    }

    /// <summary>Shows a short confirmation for actions with no visible result, then returns to the document status.</summary>
    private void FlashStatus(string message)
    {
        StatusText.Text = message;
        StatusText.ToolTip = null;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void UpdateWordCount()
    {
        int words = Regex.Count(Editor.Text, @"\S+");
        WordCountText.Text = $"Words: {words:N0}";
    }

    private void ShowWarning(string heading, string message) =>
        MessageDialog.Show(this, MessageDialog.Kind.Warning, heading, message, primary: "OK");

    /// <summary>Something failed: the heading says what, then any advice, then Windows' own explanation.</summary>
    private void ShowError(string heading, Exception ex, string? advice = null) =>
        MessageDialog.Show(this, MessageDialog.Kind.Error, heading,
            advice == null ? ex.Message : $"{advice}\n\n{ex.Message}", primary: "OK");
}
