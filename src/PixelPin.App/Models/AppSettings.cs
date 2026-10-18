using System.Text.Json.Serialization;

namespace PixelPin.Models;

public sealed class AppSettings
{
    public const string DefaultCaptureHotKey = "F1";
    public const string DefaultCapturePinHotKey = "F3";
    public const string DefaultCaptureFullScreenHotKey = "Ctrl+F1";
    public const string DefaultPasteStickerHotKey = "Ctrl+F3";
    public const string DefaultToggleMouseThroughHotKey = "Ctrl+Alt+P";
    public const string DefaultCloseAllStickersHotKey = "Shift+F3";
    public const string FallbackAnnotationColor = "#E53935";
    public const double FallbackAnnotationStrokeWidth = 3;

    public string CaptureHotKey { get; set; } = DefaultCaptureHotKey;

    /// <summary>Region capture that pins the result instead of copying it.</summary>
    public string CapturePinHotKey { get; set; } = DefaultCapturePinHotKey;

    /// <summary>Captures every display at once without showing a selection overlay.</summary>
    public string CaptureFullScreenHotKey { get; set; } = DefaultCaptureFullScreenHotKey;

    /// <summary>Turns the clipboard image into a desktop sticker.</summary>
    public string PasteStickerHotKey { get; set; } = DefaultPasteStickerHotKey;

    public string ToggleMouseThroughHotKey { get; set; } = DefaultToggleMouseThroughHotKey;

    public string CloseAllStickersHotKey { get; set; } = DefaultCloseAllStickersHotKey;

    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PixelPin");

    public double DefaultStickerOpacity { get; set; } = 1.0;

    public int HistoryLimit { get; set; } = 40;

    public int HistoryDiskLimitMegabytes { get; set; } = 512;

    public bool StartWithWindows { get; set; }

    public bool CopyAfterCapture { get; set; } = true;

    public bool PinAfterCapture { get; set; }

    /// <summary>Pulls a dragged region onto nearby window borders.</summary>
    public bool SnapToWindowEdges { get; set; } = true;

    /// <summary>Highlights the window under the pointer before the drag starts.</summary>
    public bool HighlightWindowOnHover { get; set; } = true;

    /// <summary>Keeps pinned images undistorted while resizing.</summary>
    public bool StickerKeepAspectRatio { get; set; } = true;

    /// <summary>Closes a sticker when it is double-clicked.</summary>
    public bool StickerCloseOnDoubleClick { get; set; } = true;

    public string DefaultAnnotationColor { get; set; } = FallbackAnnotationColor;

    public double DefaultAnnotationStrokeWidth { get; set; } = FallbackAnnotationStrokeWidth;

    public string FileNameTemplate { get; set; } = "PixelPin_{yyyyMMdd_HHmmss}";

    /// <summary>
    /// "System", "Light" or "Dark". Stored as text rather than an enum so an
    /// unrecognised value from a newer build degrades to following the system.
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>The configurable shortcuts, in the order they are shown in settings.</summary>
    [JsonIgnore]
    public IReadOnlyList<(string Label, string Value)> HotKeySlots => new[]
    {
        ("截图快捷键", CaptureHotKey),
        ("截图并贴图快捷键", CapturePinHotKey),
        ("全屏截图快捷键", CaptureFullScreenHotKey),
        ("粘贴为贴图快捷键", PasteStickerHotKey),
        ("鼠标穿透快捷键", ToggleMouseThroughHotKey),
        ("关闭所有贴图快捷键", CloseAllStickersHotKey),
    };

    public void Normalize()
    {
        CaptureHotKey = NormalizeHotKey(CaptureHotKey, DefaultCaptureHotKey);
        CapturePinHotKey = NormalizeHotKey(CapturePinHotKey, DefaultCapturePinHotKey);
        CaptureFullScreenHotKey = NormalizeHotKey(CaptureFullScreenHotKey, DefaultCaptureFullScreenHotKey);
        PasteStickerHotKey = NormalizeHotKey(PasteStickerHotKey, DefaultPasteStickerHotKey);
        ToggleMouseThroughHotKey = NormalizeHotKey(ToggleMouseThroughHotKey, DefaultToggleMouseThroughHotKey);
        CloseAllStickersHotKey = NormalizeHotKey(CloseAllStickersHotKey, DefaultCloseAllStickersHotKey);
        SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PixelPin")
            : Environment.ExpandEnvironmentVariables(SaveDirectory.Trim());
        DefaultStickerOpacity = Math.Clamp(DefaultStickerOpacity, 0.15, 1.0);
        HistoryLimit = Math.Clamp(HistoryLimit, 0, 200);
        HistoryDiskLimitMegabytes = Math.Clamp(HistoryDiskLimitMegabytes, 32, 4096);
        DefaultAnnotationColor = NormalizeColor(DefaultAnnotationColor);
        DefaultAnnotationStrokeWidth = Math.Clamp(DefaultAnnotationStrokeWidth, 1, 14);
        FileNameTemplate = string.IsNullOrWhiteSpace(FileNameTemplate)
            ? "PixelPin_{yyyyMMdd_HHmmss}"
            : FileNameTemplate.Trim();
        Theme = NormalizeTheme(Theme);
    }

    private static string NormalizeTheme(string? value)
    {
        return Enum.TryParse<Services.AppTheme>(value, ignoreCase: true, out var theme)
            ? theme.ToString()
            : nameof(Services.AppTheme.System);
    }

    private static string NormalizeHotKey(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FallbackAnnotationColor;
        }

        var trimmed = value.Trim();
        try
        {
            var converted = System.Windows.Media.ColorConverter.ConvertFromString(trimmed);
            return converted is System.Windows.Media.Color color
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : FallbackAnnotationColor;
        }
        catch (FormatException)
        {
            return FallbackAnnotationColor;
        }
        catch (NotSupportedException)
        {
            return FallbackAnnotationColor;
        }
    }
}
