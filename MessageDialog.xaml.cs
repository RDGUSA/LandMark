using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LandMark;

/// <summary>The app's own message box, styled like the rest of LandMark (see MessageDialog.xaml).</summary>
public partial class MessageDialog : Window
{
    public enum Kind { Info, Question, Warning, Error, Reload }

    /// <summary>Which button closed the dialog. Cancel also covers the close button and Esc.</summary>
    public enum Result { Primary, Secondary, Cancel }

    private static readonly Brush AttentionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xA8, 0x00)));   // the reload button's yellow
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xDC, 0x3F, 0x35)));       // RDG Red

    private Button? _defaultButton;
    private Result _result = Result.Cancel;

    private MessageDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => _defaultButton?.Focus();   // so Enter and the arrow keys work straight away
    }

    /// <summary>
    /// Shows a message with one or two buttons. Returns true for the primary button; false for the secondary
    /// button, the close button or Esc.
    /// </summary>
    /// <param name="secondary">Omit for a message with a single button.</param>
    /// <param name="primaryIsDefault">Which button Enter presses; pass false when the primary button is the risky one.</param>
    public static bool Show(Window? owner, Kind kind, string heading, string message,
        string primary, string? secondary = null, bool primaryIsDefault = true) =>
        Ask(owner, kind, heading, message, primary, secondary, cancel: null, primaryIsDefault) == Result.Primary;

    /// <summary>Shows a message with up to three buttons, e.g. Save / Don't save / Cancel.</summary>
    /// <param name="cancel">A third button that does the same as the close button and Esc.</param>
    public static Result Ask(Window? owner, Kind kind, string heading, string message,
        string primary, string? secondary, string? cancel, bool primaryIsDefault = true)
    {
        var dialog = new MessageDialog();
        // Centered on the owner when there is one that's showing; otherwise on the screen.
        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.HeadingText.Text = heading;
        dialog.MessageText.Text = message;
        if (message.Length == 0) dialog.MessageText.Visibility = Visibility.Collapsed;

        dialog.IconGlyph.Text = kind switch
        {
            Kind.Question => "",
            Kind.Warning => "",
            Kind.Error => "",
            Kind.Reload => "",
            _ => "",
        };
        if (kind is Kind.Info or Kind.Question)
            dialog.IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");   // follows the theme
        else
            dialog.IconGlyph.Foreground = kind == Kind.Error ? ErrorBrush : AttentionBrush;

        dialog.PrimaryButton.Content = primary;
        SetButton(dialog.SecondaryButton, secondary);
        SetButton(dialog.CancelButton, cancel);

        dialog._defaultButton = primaryIsDefault ? dialog.PrimaryButton
            : secondary != null ? dialog.SecondaryButton
            : cancel != null ? dialog.CancelButton
            : dialog.PrimaryButton;
        dialog._defaultButton.IsDefault = true;

        dialog.ShowDialog();
        return dialog._result;
    }

    private static void SetButton(Button button, string? label)
    {
        if (label == null)
            button.Visibility = Visibility.Collapsed;
        else
            button.Content = label;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => CloseWith(Result.Primary);

    private void Secondary_Click(object sender, RoutedEventArgs e) => CloseWith(Result.Secondary);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWith(Result.Cancel);

    private void CloseWith(Result result)
    {
        _result = result;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseWith(Result.Cancel);
        e.Handled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
