using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LandMark.Services;

/// <summary>
/// Registers LandMark with Windows as an app that opens Markdown files, for the current user only (no admin rights).
/// This puts LandMark in "Open with" and in Settings › Apps › Default apps. Windows doesn't let apps make themselves
/// the default, so the user picks LandMark there (or ticks "Always" in "Open with").
/// </summary>
internal static class FileAssociation
{
    public const string ProgId = "LandMark.Markdown";
    public static readonly string[] Extensions = [".md", ".markdown", ".mdown", ".mkd"];

    private const string AppName = "LandMark";
    private const string CapabilitiesKey = @"Software\LandMark\Capabilities";
    private const string CommandKey = @$"Software\Classes\{ProgId}\shell\open\command";

    // The registration written when the app was called Landmarks; replaced by Register().
    private const string OldName = "Landmarks";
    private const string OldProgId = "Landmarks.Markdown";

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't find LandMark.exe.");

    /// <summary>Writes (or rewrites) the registration so it points at this copy of LandMark.exe.</summary>
    public static void Register()
    {
        string exe = ExePath;
        string icon = $"\"{exe}\",0";
        string command = $"\"{exe}\" \"%1\"";

        RemoveOldRegistration();

        using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
        {
            // The file type LandMark opens; its icon is used for .md files once LandMark is the default.
            using (var type = classes.CreateSubKey(ProgId))
            {
                type.SetValue("", "Markdown Document");
                type.SetValue("FriendlyTypeName", "Markdown Document");
                using (var key = type.CreateSubKey("DefaultIcon")) key.SetValue("", icon);
                using (var key = type.CreateSubKey(@"shell\open\command")) key.SetValue("", command);
            }

            foreach (string extension in Extensions)
            {
                // List LandMark under "Open with" for each extension.
                using (var key = classes.CreateSubKey($@"{extension}\OpenWithProgids"))
                    key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

                // On a PC where no app has claimed the extension, Windows doesn't treat it as a file type:
                // it's missing from Default apps and double-clicking asks every time. Claim it then, but never
                // replace another app's registration (the user can still switch in Default apps).
                using var type = classes.CreateSubKey(extension);
                if (!HasHandler(extension) || type.GetValue("") as string == OldProgId) type.SetValue("", ProgId);
                if (type.GetValue("Content Type") == null) type.SetValue("Content Type", "text/markdown");
                if (type.GetValue("PerceivedType") == null) type.SetValue("PerceivedType", "text");
            }

            using (var app = classes.CreateSubKey($@"Applications\{Path.GetFileName(exe)}"))
            {
                app.SetValue("FriendlyAppName", AppName);
                using (var key = app.CreateSubKey(@"shell\open\command")) key.SetValue("", command);
                using (var key = app.CreateSubKey("SupportedTypes"))
                    foreach (string extension in Extensions) key.SetValue(extension, "");
            }
        }

        // What Settings › Default apps shows for LandMark.
        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "Read Markdown and turn it into email, Word, PDF and plain text.");
            capabilities.SetValue("ApplicationIcon", icon);
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (string extension in Extensions) associations.SetValue(extension, ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, CapabilitiesKey);

        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// If LandMark was registered from a copy that has since been moved or deleted, points the registration
    /// at this copy so double-clicking .md files keeps working. A registration that still works is left alone,
    /// so running a development build doesn't take over from the installed one. A registration made under the
    /// old name (Landmarks) is replaced with one for this copy.
    /// </summary>
    public static void RepairIfMoved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandKey);
            if (key?.GetValue("") is not string command)
            {
                using var old = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{OldProgId}");
                if (old != null) Register();
                return;
            }

            string registeredExe = command.TrimStart('"').Split('"')[0];
            if (!File.Exists(registeredExe))
                Register();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            // Not worth bothering the user at startup; File › Open .md Files with LandMark fixes it.
        }
    }

    /// <summary>True when double-clicking a .md file opens this copy of LandMark (Windows' answer, including the user's choice).</summary>
    public static bool IsDefault()
    {
        var path = new StringBuilder(1024);
        uint size = (uint)path.Capacity;
        if (AssocQueryString(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_EXECUTABLE, ".md", "open", path, ref size) != 0) return false;
        return string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes what the app wrote to the registry when it was called Landmarks.</summary>
    private static void RemoveOldRegistration()
    {
        using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
        {
            classes.DeleteSubKeyTree(OldProgId, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree($@"Applications\{OldName}.exe", throwOnMissingSubKey: false);
            foreach (string extension in Extensions)
            {
                using var openWith = classes.OpenSubKey($@"{extension}\OpenWithProgids", writable: true);
                openWith?.DeleteValue(OldProgId, throwOnMissingValue: false);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{OldName}", throwOnMissingSubKey: false);
        using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue(OldName, throwOnMissingValue: false);
    }

    /// <summary>True when some app (user or machine-wide) is registered as the extension's handler.</summary>
    private static bool HasHandler(string extension)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(extension);
        return key?.GetValue("") is string progId && progId.Length > 0;
    }

    /// <summary>Opens Settings › Apps › Default apps at the LandMark page, where the user can pick it for each file type.</summary>
    public static void OpenDefaultAppsSettings() =>
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}") { UseShellExecute = true });

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int ASSOCF_INIT_IGNOREUNKNOWN = 0x400;
    private const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint outputSize);
}
