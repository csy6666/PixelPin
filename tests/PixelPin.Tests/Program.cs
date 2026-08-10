using System.Windows;
using System.Windows.Input;
using PixelPin.Models;
using PixelPin.Services;
using PixelPin.Utilities;

var tests = new (string Name, Action Run)[]
{
    ("hotkey parser accepts modifier combinations", TestHotKeyParser),
    ("settings normalization clamps unsafe preferences", TestSettingsNormalization),
    ("settings export and import preserve normalized preferences", TestSettingsPortability),
    ("selection geometry normalizes, clamps, and resizes", TestSelectionGeometry),
    ("filename templates expand and sanitize output", TestFileNames),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL: {test.Name}: {exception.Message}");
    }
}

return failures.Count == 0 ? 0 : 1;

static void TestHotKeyParser()
{
    Expect(HotKeyDefinition.TryParse("Ctrl+Alt+P", out var hotKey, out var error), error);
    Expect(hotKey.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt), "Modifiers were not parsed.");
    Expect(hotKey.Key == Key.P, "P was not parsed as the trigger key.");
    Expect(hotKey.ToString() == "Ctrl+Alt+P", "Shortcut round-trip changed.");
    Expect(!HotKeyDefinition.TryParse("Ctrl+Shift", out _, out _), "Modifier-only shortcut was accepted.");
    Expect(!HotKeyDefinition.TryParse("F1+F2", out _, out _), "Two trigger keys were accepted.");
}

static void TestSettingsNormalization()
{
    var settings = new AppSettings
    {
        CaptureHotKey = "  ",
        ToggleMouseThroughHotKey = "  ",
        SaveDirectory = "  ",
        DefaultStickerOpacity = 4,
        HistoryLimit = -10,
        HistoryDiskLimitMegabytes = 8,
        FileNameTemplate = " ",
    };

    settings.Normalize();
    Expect(settings.CaptureHotKey == "F1", "Capture shortcut default was not restored.");
    Expect(settings.ToggleMouseThroughHotKey == "Ctrl+Alt+P", "Mouse-through shortcut default was not restored.");
    Expect(settings.DefaultStickerOpacity == 1, "Opacity was not clamped.");
    Expect(settings.HistoryLimit == 0, "History floor was not applied.");
    Expect(settings.HistoryDiskLimitMegabytes == 32, "History cache limit floor was not applied.");
    Expect(settings.FileNameTemplate == "PixelPin_{yyyyMMdd_HHmmss}", "Filename default was not restored.");
}

static void TestSelectionGeometry()
{
    var normalized = SelectionGeometry.Normalize(new Point(100, 80), new Point(20, 30));
    Expect(normalized == new Rect(20, 30, 80, 50), "Selection was not normalized.");

    var clamped = SelectionGeometry.Clamp(new Rect(-30, 90, 70, 30), new Size(100, 100), 12);
    Expect(clamped == new Rect(0, 70, 70, 30), "Selection was not clamped to bounds.");

    var resized = SelectionGeometry.Resize(new Rect(20, 20, 40, 40), ResizeEdge.Left | ResizeEdge.Top, new Vector(50, 50), new Size(120, 120), 12);
    Expect(resized.Width >= 12 && resized.Height >= 12, "Resize crossed below the minimum size.");
    Expect(resized.Left >= 0 && resized.Top >= 0, "Resize escaped the canvas.");
}

static void TestSettingsPortability()
{
    var directory = Path.Combine(Path.GetTempPath(), $"PixelPin.Tests.{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.json");

    try
    {
        var service = new SettingsService();
        var settings = new AppSettings
        {
            CaptureHotKey = "Ctrl+Shift+F9",
            ToggleMouseThroughHotKey = "Ctrl+Alt+M",
            SaveDirectory = @"C:\Exports",
            DefaultStickerOpacity = 0.66,
            HistoryLimit = 88,
            HistoryDiskLimitMegabytes = 333,
            StartWithWindows = true,
            CopyAfterCapture = false,
            PinAfterCapture = true,
            FileNameTemplate = "Shot_{yyyy-MM-dd}",
        };

        service.Export(settings, path);
        var imported = service.Import(path);

        Expect(imported.CaptureHotKey == "Ctrl+Shift+F9", "Capture hotkey did not survive export/import.");
        Expect(imported.ToggleMouseThroughHotKey == "Ctrl+Alt+M", "Mouse-through hotkey did not survive export/import.");
        Expect(imported.DefaultStickerOpacity == 0.66, "Opacity did not survive export/import.");
        Expect(imported.HistoryLimit == 88, "History limit did not survive export/import.");
        Expect(imported.HistoryDiskLimitMegabytes == 333, "History disk limit did not survive export/import.");
        Expect(imported.PinAfterCapture, "Boolean settings did not survive export/import.");

        File.WriteAllText(path, "{ not valid json }");
        ExpectThrows(() => service.Import(path), "Malformed settings JSON was accepted.");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void TestFileNames()
{
    var name = BitmapUtilities.BuildFileName("capture/{yyyy-MM-dd}_{HHmmss}", new DateTime(2026, 8, 10, 9, 4, 5));
    Expect(!name.Contains('/'), "Invalid path separator was not sanitized.");
    Expect(name.Contains("2026-08-10_090405"), "Time tokens were not expanded.");
}

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectThrows(Action action, string message)
{
    try
    {
        action();
    }
    catch
    {
        return;
    }

    throw new InvalidOperationException(message);
}
