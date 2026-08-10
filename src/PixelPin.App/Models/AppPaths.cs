namespace PixelPin.Models;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelPin");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string HistoryDirectory => Path.Combine(Root, "history");

    public static string DiagnosticLogFile => Path.Combine(Root, "diagnostics.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(HistoryDirectory);
    }
}
