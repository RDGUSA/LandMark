using System.IO;

namespace LandMark.Services;

/// <summary>
/// %AppData%\LandMark, where settings and styles are saved. The app was called Landmarks before, so on first run
/// the files saved under %AppData%\Landmarks are copied here (copied, not moved, so the old folder stays as a backup).
/// </summary>
internal static class AppDataFolder
{
    private static readonly string Root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string Path { get; } = System.IO.Path.Combine(Root, "LandMark");

    static AppDataFolder()
    {
        string old = System.IO.Path.Combine(Root, "Landmarks");
        try
        {
            if (Directory.Exists(Path) || !Directory.Exists(old)) return;

            Directory.CreateDirectory(Path);
            foreach (string file in Directory.GetFiles(old))
                File.Copy(file, System.IO.Path.Combine(Path, System.IO.Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't copy: start with default settings rather than failing to open.
        }
    }
}
