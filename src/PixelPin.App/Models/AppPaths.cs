namespace PixelPin.Models;

public static class AppPaths
{
    /// <summary>
    /// Environment variable that relocates every file PixelPin writes. The desktop
    /// probe and the render harness set it so a test run cannot touch the settings and
    /// history of whoever is logged in - a run that writes into the real profile both
    /// risks the user's data and fails outright on a machine that denies that path.
    /// </summary>
    public const string DataDirectoryVariable = "PIXELPIN_DATA_DIR";

    public static string Root { get; } = ResolveRoot();

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string HistoryDirectory => Path.Combine(Root, "history");

    public static string DiagnosticLogFile => Path.Combine(Root, "diagnostics.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(HistoryDirectory);
    }

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixelPin");
    }
}
