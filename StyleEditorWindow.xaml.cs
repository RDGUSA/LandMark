using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LandMark.Styling;

namespace LandMark;

/// <summary>
/// Edits working copies of the styles; nothing changes in the library until Save or
/// Use for This Document, so Cancel discards every edit.
/// </summary>
public partial class StyleEditorWindow : Window
{
    private readonly StyleLibrary _library;
    private readonly ObservableCollection<DocumentStyle> _styles;
    private string _defaultId;

    public StyleEditorWindow(StyleLibrary library, DocumentStyle current)
    {
        InitializeComponent();
        _library = library;
        _styles = new(library.All.Select(style => style.Clone()));
        _defaultId = library.DefaultStyleId;

        StyleList.ItemsSource = _styles;
        StyleList.SelectedItem = _styles.FirstOrDefault(style => style.Id == current.Id) ?? _styles[0];
    }

    /// <summary>True when the selected style should be applied to the current document.</summary>
    public bool UseForDocument { get; private set; }

    public string? SelectedStyleId => Selected?.Id;

    private DocumentStyle? Selected => StyleList.SelectedItem as DocumentStyle;

    private void StyleList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private void New_Click(object sender, RoutedEventArgs e) =>
        Add(new DocumentStyle { Name = UniqueName("New style") });   // starts from the RDG style's settings

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } style)
            Add(style.Duplicate(UniqueName($"{style.Name} copy")));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsBuiltIn: false } style) return;

        // Cancel is the default: Enter shouldn't delete. Cancel in this window still brings the style back.
        if (!MessageDialog.Show(this, MessageDialog.Kind.Warning, $"Delete the style \"{style.Name}\"?",
                "Changed your mind later? Cancel in the Styles window brings it back.",
                primary: "Delete", secondary: "Cancel", primaryIsDefault: false))
            return;

        int index = StyleList.SelectedIndex;
        _styles.Remove(style);
        if (_defaultId == style.Id) _defaultId = StyleLibrary.BuiltIn[0].Id;
        StyleList.SelectedIndex = Math.Min(index, _styles.Count - 1);
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } style) _defaultId = style.Id;
        UpdateControls();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Commit(useForDocument: false);

    private void UseForDocument_Click(object sender, RoutedEventArgs e) => Commit(useForDocument: true);

    private void Commit(bool useForDocument)
    {
        foreach (var style in _styles.Where(style => string.IsNullOrWhiteSpace(style.Name)))
            style.Name = "Untitled style";

        _library.CustomStyles = _styles.Where(style => !style.IsBuiltIn).ToList();
        _library.DefaultStyleId = _styles.Any(style => style.Id == _defaultId) ? _defaultId : StyleLibrary.BuiltIn[0].Id;
        UseForDocument = useForDocument;
        DialogResult = true;
    }

    private void Add(DocumentStyle style)
    {
        _styles.Add(style);
        StyleList.SelectedItem = style;
        StyleList.ScrollIntoView(style);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int n = 2; _styles.Any(style => style.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} {n}";
        return name;
    }

    private void UpdateControls()
    {
        var style = Selected;
        DeleteButton.IsEnabled = style is { IsBuiltIn: false };
        SetDefaultButton.IsEnabled = style != null && style.Id != _defaultId;
        BuiltInNotice.Visibility = style is { IsBuiltIn: true } ? Visibility.Visible : Visibility.Collapsed;

        var defaultStyle = _styles.FirstOrDefault(s => s.Id == _defaultId);
        DefaultText.Text = $"Default for new documents: {defaultStyle?.Name ?? StyleLibrary.BuiltIn[0].Name}";
    }
}
