using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LandMark;

/// <summary>
/// Lets a newly started LandMark hand a file to a LandMark window that already has it open, so double-clicking
/// an open file brings its window to the front instead of opening a second copy.
/// </summary>
/// <remarks>
/// The new process sends each running LandMark window a WM_COPYDATA message with the file's path. A window
/// that has the file open comes to the front and replies 1. Other files still open in a window of their own.
/// </remarks>
internal static class RunningInstances
{
    private const int WM_COPYDATA = 0x004A;
    private static readonly IntPtr ShowFileMessage = new(0x4C4D4B31);   // "LMK1": marks our messages
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int ASFW_ANY = -1;

    /// <summary>Returns true if another LandMark window had the file open and has been brought to the front.</summary>
    public static bool TryShowInRunningWindow(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var windows = FindOtherLandMarkWindows();
        if (windows.Count == 0) return false;

        // This process was started by the user (e.g. a double-click), so Windows lets it bring a window to the
        // front; pass that right on to the window that has the file.
        AllowSetForegroundWindow(ASFW_ANY);

        IntPtr text = Marshal.StringToHGlobalUni(fullPath);
        try
        {
            var data = new CopyDataStruct { dwData = ShowFileMessage, cbData = (fullPath.Length + 1) * 2, lpData = text };
            foreach (var window in windows)
            {
                if (SendMessageTimeout(window, WM_COPYDATA, IntPtr.Zero, ref data, SMTO_ABORTIFHUNG, 2000, out var result) != IntPtr.Zero
                    && result == 1)
                {
                    // The window has restored itself; also bring it forward from here, since Windows can refuse
                    // the request when it comes from a background app.
                    SetForegroundWindow(window);
                    return true;
                }
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    /// <summary>
    /// Makes the window answer requests from other LandMark processes. <paramref name="showIfOpen"/> is given a
    /// full path and returns true if the window has that file open (and has come to the front).
    /// </summary>
    public static void Listen(Window window, Func<string, bool> showIfOpen)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_COPYDATA) return IntPtr.Zero;
            var data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
            if (data.dwData != ShowFileMessage || data.lpData == IntPtr.Zero) return IntPtr.Zero;

            handled = true;
            string path = Marshal.PtrToStringUni(data.lpData, data.cbData / 2).TrimEnd('\0');
            return showIfOpen(path) ? new IntPtr(1) : IntPtr.Zero;
        });
    }

    /// <summary>
    /// The top-level WPF windows of other LandMark processes. Process.MainWindowHandle isn't reliable here: the
    /// preview's web browser control adds a hidden-but-"visible" top-level window, which .NET can pick instead
    /// (e.g. when LandMark is minimized). WPF names its window classes "HwndWrapper[LandMark;…".
    /// </summary>
    private static List<IntPtr> FindOtherLandMarkWindows()
    {
        uint self = (uint)Environment.ProcessId;
        string classPrefix = $"HwndWrapper[{AppDomain.CurrentDomain.FriendlyName};";
        var found = new List<IntPtr>();
        var className = new System.Text.StringBuilder(256);
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processId != self && GetWindow(window, GW_OWNER) == IntPtr.Zero)
            {
                className.Clear();
                if (GetClassName(window, className, className.Capacity) > 0 &&
                    className.ToString().StartsWith(classPrefix, StringComparison.Ordinal))
                    found.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
