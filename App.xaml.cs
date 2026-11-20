using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using LandMark.Models;
using LandMark.Services;
using LandMark.Views;

namespace LandMark;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A file path argument (e.g. a double-click or "Open with") is opened straight away, unless another
        // LandMark window already has that file open: then that window comes to the front instead.
        string? file = e.Args.FirstOrDefault();
        if (file != null && RunningInstances.TryShowInRunningWindow(file))
        {
            Shutdown();
            return;
        }

        ApplyTheme(Settings.Theme);
        UseLeftAlignedMenus();
        if (IsSoftwareRendered)
            Resources.Remove("FlyoutShadow");
        FileAssociation.RepairIfMoved();
        new MainWindow(file).Show();
    }

    public static AppSettings Settings { get; } = AppSettings.Load();

#pragma warning disable WPF0001 // ThemeMode is marked "for evaluation" but is how the built-in Fluent theme is switched
    /// <summary>Switches the whole app to "Light", "Dark" or "System" (follow Windows); takes effect immediately.</summary>
    public static void ApplyTheme(string theme) =>
        Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

    /// <summary>
    /// Hover fill for drop-down menu items, which Fluent's MenuItem templates look up as
    /// MenuBarItemBackgroundSelected. Fluent's default is a ~2% tint that's barely visible; this matches the
    /// menu bar's highlight: the theme's text color at 12%. It's set from code with the current text color
    /// because a brush defined in App.xaml keeps its original color when the theme changes.
    /// </summary>
    public static void UpdateMenuHighlight(System.Windows.Media.Color textColor)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x1F, textColor.R, textColor.G, textColor.B));
        brush.Freeze();
        Current.Resources["MenuBarItemBackgroundSelected"] = brush;
    }

    /// <summary>
    /// True when WPF has no graphics hardware to draw with (render tier 0), e.g. a VM without 3D
    /// acceleration or some Remote Desktop sessions. Effects and animations are then drawn on the CPU.
    /// </summary>
    public static bool IsSoftwareRendered => (System.Windows.Media.RenderCapability.Tier >> 16) == 0;

    /// <summary>
    /// On pen/touch PCs set to "right-handed", WPF opens menus to the left of the menu bar,
    /// where they get cut off. There's no public switch, so reset the cached setting.
    /// </summary>
    private static void UseLeftAlignedMenus()
    {
        var field = typeof(SystemParameters).GetField("_menuDropAlignment", BindingFlags.NonPublic | BindingFlags.Static);
        if (SystemParameters.MenuDropAlignment && field != null)
            field.SetValue(null, false);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            MessageDialog.Show(MainWindow, MessageDialog.Kind.Error, "Something went wrong", e.Exception.Message, primary: "OK");
        }
        catch (Exception)
        {
            // The failure may be in drawing windows itself; fall back to Windows' own message box.
            MessageBox.Show(e.Exception.Message, "LandMark", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
