namespace PixelPin.Models;

public sealed class AppSettings
{
    public string CaptureHotKey { get; set; } = "F1";

    public string ToggleMouseThroughHotKey { get; set; } = "Ctrl+Alt+P";

    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PixelPin");

    public double DefaultStickerOpacity { get; set; } = 1.0;

    public int HistoryLimit { get; set; } = 40;

    public int HistoryDiskLimitMegabytes { get; set; } = 512;

    public bool StartWithWindows { get; set; }

    public bool CopyAfterCapture { get; set; } = true;

    public bool PinAfterCapture { get; set; }

    public string FileNameTemplate { get; set; } = "PixelPin_{yyyyMMdd_HHmmss}";

    public void Normalize()
    {
        CaptureHotKey = string.IsNullOrWhiteSpace(CaptureHotKey) ? "F1" : CaptureHotKey.Trim();
        ToggleMouseThroughHotKey = string.IsNullOrWhiteSpace(ToggleMouseThroughHotKey)
            ? "Ctrl+Alt+P"
            : ToggleMouseThroughHotKey.Trim();
        SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PixelPin")
            : Environment.ExpandEnvironmentVariables(SaveDirectory.Trim());
        DefaultStickerOpacity = Math.Clamp(DefaultStickerOpacity, 0.15, 1.0);
        HistoryLimit = Math.Clamp(HistoryLimit, 0, 200);
        HistoryDiskLimitMegabytes = Math.Clamp(HistoryDiskLimitMegabytes, 32, 4096);
        FileNameTemplate = string.IsNullOrWhiteSpace(FileNameTemplate)
            ? "PixelPin_{yyyyMMdd_HHmmss}"
            : FileNameTemplate.Trim();
    }
}
