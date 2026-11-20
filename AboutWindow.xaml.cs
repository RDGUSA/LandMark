using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using LandMark.Styling;

namespace LandMark;

public partial class AboutWindow : Window
{
    private readonly string _settingsFolder = Path.GetDirectoryName(StyleLibrary.FilePath)!;

    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)}";
        SettingsPath.Text = _settingsFolder;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsLink_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settingsFolder);
        Process.Start(new ProcessStartInfo(_settingsFolder) { UseShellExecute = true });
    }
}
