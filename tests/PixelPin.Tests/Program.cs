using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelPin.Models;
using PixelPin.Services;
using PixelPin.Tests;
using PixelPin.Utilities;

// The desktop probe needs a real interactive session and is therefore opt-in:
//   dotnet run --project .\tests\PixelPin.Tests -c Release -- --desktop-probe
if (args.Contains("--desktop-probe", StringComparer.OrdinalIgnoreCase))
{
    var output = args.SkipWhile(arg => !string.Equals(arg, "--desktop-probe", StringComparison.OrdinalIgnoreCase))
        .Skip(1)
        .FirstOrDefault();
    return DesktopProbe.Run(output ?? Path.Combine(AppContext.BaseDirectory, "probe"));
}

var tests = new (string Name, Action Run)[]
{
    ("hotkey parser accepts modifier combinations", TestHotKeyParser),
    ("hotkey validation rejects duplicates across features", TestHotKeyDuplicates),
    ("settings normalization clamps unsafe preferences", TestSettingsNormalization),
    ("settings export and import preserve normalized preferences", TestSettingsPortability),
    ("settings json does not persist the shortcut table", TestSettingsJsonShape),
    ("theme preference parses, formats, and normalizes safely", TestThemePreference),
    ("selection geometry normalizes, clamps, and resizes", TestSelectionGeometry),
    ("selection snapping aligns to nearby window edges", TestSelectionSnap),
    ("selection keyboard nudge moves and resizes by exact steps", TestSelectionKeyboard),
    ("hotkey recorder builds and formats captured combinations", TestHotKeyRecording),
    ("sticker alignment snaps to peers and screen edges", TestStickerAlignment),
    ("beautify layout sizes the frame and fits the shadow", TestBeautifyLayout),
    ("beautify presets survive a settings round trip", TestBeautifyPresetRoundTrip),
    ("window hit testing follows z-order", TestWindowHitTesting),
    ("ocr text joins scripts without inventing separators", TestOcrText),
    ("ocr reports why it is unavailable instead of guessing", TestOcrAvailability),
    ("capture quality rejects blank window renders", TestCaptureQuality),
    ("occlusion decides between screen and window rendering", TestOcclusionCheck),
    ("capture routes put the window surface before the screen crop", TestWindowCapturePlanner),
    ("scrolling capture measures the real offset and refuses to guess", TestScrollStitcher),
    ("sticker geometry preserves aspect ratio while resizing", TestStickerGeometry),
    ("pixelation averages the region into blocks", TestPixelate),
    ("box blur spreads neighbouring colours", TestBoxBlur),
    ("cropped capture pixels are detached from the full source", TestDetachedCrop),
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
    Expect(HotKeyDefinition.TryParse("ctrl+shift+f9", out var lower, out _), "Lower-case shortcut was rejected.");
    Expect(lower.ToString() == "Ctrl+Shift+F9", "Shortcut was not normalized to display form.");
}

static void TestHotKeyDuplicates()
{
    var slots = new List<(string Label, string Value)>
    {
        ("截图快捷键", "F1"),
        ("截图并贴图快捷键", "F3"),
        ("全屏截图快捷键", "Ctrl+F1"),
        ("粘贴为贴图快捷键", "F3"),
    };

    var duplicate = HotKeyDefinition.FindDuplicate(slots);
    Expect(duplicate is not null, "A duplicated shortcut was accepted.");
    Expect(duplicate!.Contains("F3", StringComparison.Ordinal), "The duplicate message did not name the shortcut.");
    Expect(HotKeyDefinition.ValidateAll(slots) is not null, "ValidateAll accepted duplicates.");

    var valid = new List<(string Label, string Value)>
    {
        ("截图快捷键", "F1"),
        ("截图并贴图快捷键", "F3"),
    };
    Expect(HotKeyDefinition.FindDuplicate(valid) is null, "Distinct shortcuts were reported as duplicates.");
    Expect(HotKeyDefinition.ValidateAll(valid) is null, "Distinct shortcuts failed validation.");

    var broken = new List<(string Label, string Value)> { ("截图快捷键", "Ctrl+Shift") };
    Expect(HotKeyDefinition.ValidateAll(broken) is not null, "A modifier-only shortcut passed validation.");
}

static void TestSettingsNormalization()
{
    var settings = new AppSettings
    {
        CaptureHotKey = "  ",
        CapturePinHotKey = " ",
        CaptureFullScreenHotKey = "",
        PasteStickerHotKey = null!,
        ToggleMouseThroughHotKey = "  ",
        CloseAllStickersHotKey = "  ",
        SaveDirectory = "  ",
        DefaultStickerOpacity = 4,
        HistoryLimit = -10,
        HistoryDiskLimitMegabytes = 8,
        FileNameTemplate = " ",
        DefaultAnnotationColor = "not-a-color",
        DefaultAnnotationStrokeWidth = 99,
    };

    settings.Normalize();
    Expect(settings.CaptureHotKey == "F1", "Capture shortcut default was not restored.");
    Expect(settings.CapturePinHotKey == "F3", "Capture-and-pin shortcut default was not restored.");
    Expect(settings.CaptureFullScreenHotKey == "Ctrl+F1", "Full screen shortcut default was not restored.");
    Expect(settings.PasteStickerHotKey == "Ctrl+F3", "Paste shortcut default was not restored.");
    Expect(settings.ToggleMouseThroughHotKey == "Ctrl+Alt+P", "Mouse-through shortcut default was not restored.");
    Expect(settings.CloseAllStickersHotKey == "Shift+F3", "Close-all shortcut default was not restored.");
    Expect(settings.DefaultStickerOpacity == 1, "Opacity was not clamped.");
    Expect(settings.HistoryLimit == 0, "History floor was not applied.");
    Expect(settings.HistoryDiskLimitMegabytes == 32, "History cache limit floor was not applied.");
    Expect(settings.FileNameTemplate == "PixelPin_{yyyyMMdd_HHmmss}", "Filename default was not restored.");
    Expect(settings.DefaultAnnotationColor == "#E53935", "Invalid annotation colour was not reset.");
    Expect(settings.DefaultAnnotationStrokeWidth == 14, "Annotation stroke width was not clamped.");

    settings.DefaultAnnotationColor = "red";
    settings.Normalize();
    Expect(settings.DefaultAnnotationColor == "#FF0000", "Named colours were not normalized to hex.");

    var defaults = new AppSettings();
    defaults.Normalize();
    Expect(HotKeyDefinition.ValidateAll(defaults.HotKeySlots) is null,
        "The shipped default shortcuts conflict with each other.");
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
            CapturePinHotKey = "Ctrl+Shift+F10",
            CaptureFullScreenHotKey = "Ctrl+Shift+F11",
            PasteStickerHotKey = "Ctrl+Alt+M",
            ToggleMouseThroughHotKey = "Ctrl+Alt+N",
            CloseAllStickersHotKey = "Ctrl+Alt+B",
            SaveDirectory = @"C:\Exports",
            DefaultStickerOpacity = 0.66,
            HistoryLimit = 88,
            HistoryDiskLimitMegabytes = 333,
            StartWithWindows = true,
            CopyAfterCapture = false,
            PinAfterCapture = true,
            SnapToWindowEdges = false,
            HighlightWindowOnHover = false,
            StickerKeepAspectRatio = false,
            StickerCloseOnDoubleClick = false,
            DefaultAnnotationColor = "#123456",
            DefaultAnnotationStrokeWidth = 7,
            FileNameTemplate = "Shot_{yyyy-MM-dd}",
        };

        service.Export(settings, path);
        var imported = service.Import(path);

        Expect(imported.CaptureHotKey == "Ctrl+Shift+F9", "Capture hotkey did not survive export/import.");
        Expect(imported.CapturePinHotKey == "Ctrl+Shift+F10", "Capture-and-pin hotkey did not survive export/import.");
        Expect(imported.CaptureFullScreenHotKey == "Ctrl+Shift+F11", "Full screen hotkey did not survive export/import.");
        Expect(imported.PasteStickerHotKey == "Ctrl+Alt+M", "Paste hotkey did not survive export/import.");
        Expect(imported.ToggleMouseThroughHotKey == "Ctrl+Alt+N", "Mouse-through hotkey did not survive export/import.");
        Expect(imported.CloseAllStickersHotKey == "Ctrl+Alt+B", "Close-all hotkey did not survive export/import.");
        Expect(imported.DefaultStickerOpacity == 0.66, "Opacity did not survive export/import.");
        Expect(imported.HistoryLimit == 88, "History limit did not survive export/import.");
        Expect(imported.HistoryDiskLimitMegabytes == 333, "History disk limit did not survive export/import.");
        Expect(imported.PinAfterCapture, "Boolean settings did not survive export/import.");
        Expect(!imported.SnapToWindowEdges && !imported.HighlightWindowOnHover,
            "Overlay behaviour settings did not survive export/import.");
        Expect(!imported.StickerKeepAspectRatio && !imported.StickerCloseOnDoubleClick,
            "Sticker behaviour settings did not survive export/import.");
        Expect(imported.DefaultAnnotationColor == "#123456", "Annotation colour did not survive export/import.");
        Expect(imported.DefaultAnnotationStrokeWidth == 7, "Annotation stroke width did not survive export/import.");

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

static void TestSettingsJsonShape()
{
    var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());
    Expect(!json.Contains("HotKeySlots", StringComparison.Ordinal),
        "The computed shortcut table was persisted into the settings file.");
    Expect(json.Contains("CapturePinHotKey", StringComparison.Ordinal),
        "The capture-and-pin shortcut is missing from the settings file.");

    // Settings written by the previous version must still load with new defaults filled in.
    var legacy = """{ "CaptureHotKey": "Ctrl+F2", "HistoryLimit": 12 }""";
    var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacy)!;
    loaded.Normalize();
    Expect(loaded.CaptureHotKey == "Ctrl+F2", "A legacy capture hotkey was overwritten.");
    Expect(loaded.HistoryLimit == 12, "A legacy history limit was overwritten.");
    Expect(loaded.CapturePinHotKey == "F3", "A new setting did not receive its default for a legacy file.");
    Expect(loaded.SnapToWindowEdges, "A new behaviour setting did not default to enabled.");
}

static void TestThemePreference()
{
    // Parse has to accept what the settings file and the picker actually contain:
    // the enum name in any casing, and nothing else.
    Expect(ThemeService.Parse("Dark") == AppTheme.Dark, "The stored theme name was not parsed.");
    Expect(ThemeService.Parse("dark") == AppTheme.Dark, "Theme parsing was case sensitive.");
    Expect(ThemeService.Parse("LIGHT") == AppTheme.Light, "An upper-case theme name was not parsed.");
    Expect(ThemeService.Parse("System") == AppTheme.System, "The system theme was not parsed.");
    Expect(ThemeService.Parse(null) == AppTheme.System, "A missing theme did not fall back to system.");
    Expect(ThemeService.Parse("") == AppTheme.System, "An empty theme did not fall back to system.");
    Expect(ThemeService.Parse("midnight") == AppTheme.System, "An unknown theme did not fall back to system.");

    // Round-tripping matters because the picker stores Tag values and the settings
    // file stores the result of Format.
    foreach (var theme in new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark })
    {
        Expect(ThemeService.Parse(ThemeService.Format(theme)) == theme,
            $"{theme} did not survive a format and parse round trip.");
        Expect(!string.IsNullOrWhiteSpace(ThemeService.Describe(theme)),
            $"{theme} has no label for the picker.");
    }

    // A hand-edited settings file must not leave an unusable value behind.
    var settings = new AppSettings { Theme = "Midnight" };
    settings.Normalize();
    Expect(settings.Theme == "System", "An unknown theme was not reset to System.");

    settings.Theme = "dark";
    settings.Normalize();
    Expect(settings.Theme == "Dark", "A lower-case theme was not normalized to its enum name.");

    // And the default for a file written before the setting existed.
    var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "HistoryLimit": 5 }""")!;
    legacy.Normalize();
    Expect(legacy.Theme == "System", "A legacy settings file did not default to the system theme.");
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

static void TestSelectionSnap()
{
    var guides = new List<Rect> { new(100, 100, 300, 200) };

    var snappedLeft = SelectionSnap.Snap(new Rect(104, 300, 60, 40), guides);
    Expect(snappedLeft.Left == 100, "A near-left edge was not snapped onto the window border.");

    var snappedRight = SelectionSnap.Snap(new Rect(380, 300, 24, 40), guides);
    Expect(snappedRight.Right == 400, "A near-right edge was not snapped onto the window border.");

    var untouched = SelectionSnap.Snap(new Rect(150, 150, 60, 40), guides);
    Expect(untouched == new Rect(150, 150, 60, 40), "A selection far from every guide was modified.");

    var none = SelectionSnap.Snap(new Rect(10, 10, 20, 20), []);
    Expect(none == new Rect(10, 10, 20, 20), "An empty guide list changed the selection.");

    var whole = SelectionSnap.SnapToContaining(new Rect(101, 102, 298, 197), guides);
    Expect(whole == guides[0], "A near-complete window selection was not adopted exactly.");

    var unrelated = SelectionSnap.SnapToContaining(new Rect(500, 500, 40, 40), guides);
    Expect(unrelated == new Rect(500, 500, 40, 40), "An unrelated selection was snapped to a window.");
}

static void TestSelectionKeyboard()
{
    var boundary = new Size(500, 400);

    // Plain arrows move the whole selection by one pixel and never resize it.
    var moved = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Right, ModifierKeys.None, boundary);
    Expect(moved == new Rect(101, 100, 80, 60), $"Right arrow moved to {moved} instead of one pixel right.");

    var up = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Up, ModifierKeys.None, boundary);
    Expect(up == new Rect(100, 99, 80, 60), $"Up arrow moved to {up} instead of one pixel up.");

    // Ctrl widens the step to ten pixels.
    var coarse = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Left, ModifierKeys.Control, boundary);
    Expect(coarse == new Rect(90, 100, 80, 60), $"Ctrl+Left moved to {coarse} instead of ten pixels left.");

    Expect(SelectionKeyboard.StepFor(ModifierKeys.None) == 1, "The default step was not one pixel.");
    Expect(SelectionKeyboard.StepFor(ModifierKeys.Control) == 10, "The Ctrl step was not ten pixels.");

    // Shift resizes the edge the arrow points at, leaving the opposite edge fixed.
    var growRight = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Right, ModifierKeys.Shift, boundary);
    Expect(growRight == new Rect(100, 100, 81, 60), $"Shift+Right produced {growRight} instead of widening by one.");
    Expect(growRight.Left == 100, "Resizing the right edge moved the left edge.");

    var growLeft = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Left, ModifierKeys.Shift, boundary);
    Expect(growLeft == new Rect(99, 100, 81, 60), $"Shift+Left produced {growLeft} instead of extending left.");
    Expect(growLeft.Right == 180, "Resizing the left edge moved the right edge.");

    var growTop = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Up, ModifierKeys.Shift, boundary);
    Expect(growTop == new Rect(100, 99, 80, 61), $"Shift+Up produced {growTop} instead of extending upward.");

    var growBottom = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Down, ModifierKeys.Shift, boundary);
    Expect(growBottom == new Rect(100, 100, 80, 61), $"Shift+Down produced {growBottom} instead of extending downward.");

    // Ctrl+Shift combines both: a coarse resize.
    var coarseResize = SelectionKeyboard.Apply(new Rect(100, 100, 80, 60), Key.Down, ModifierKeys.Control | ModifierKeys.Shift, boundary);
    Expect(coarseResize == new Rect(100, 100, 80, 70), $"Ctrl+Shift+Down produced {coarseResize} instead of a ten pixel resize.");

    // The selection must never be pushed outside the surface.
    var pinnedLeft = SelectionKeyboard.Apply(new Rect(0, 0, 80, 60), Key.Left, ModifierKeys.None, boundary);
    Expect(pinnedLeft.Left == 0, $"The selection escaped the left edge to {pinnedLeft.Left}.");

    var pinnedBottom = SelectionKeyboard.Apply(new Rect(420, 340, 80, 60), Key.Down, ModifierKeys.Control, boundary);
    Expect(pinnedBottom.Bottom <= boundary.Height, $"The selection escaped the bottom edge to {pinnedBottom.Bottom}.");

    // Non-arrow keys are left for the rest of the shortcut handling.
    Expect(!SelectionKeyboard.TryGetAdjustment(Key.A, ModifierKeys.None, out _, out _),
        "A letter key was treated as an arrow key.");

    var untouched = SelectionKeyboard.Apply(new Rect(10, 10, 20, 20), Key.F5, ModifierKeys.None, boundary);
    Expect(untouched == new Rect(10, 10, 20, 20), "A non-arrow key changed the selection.");
}

static void TestHotKeyRecording()
{
    // A modifier-only press is rejected so the user can hold Ctrl and then press a key.
    Expect(!HotKeyDefinition.TryCreate(ModifierKeys.Control, Key.LeftCtrl, out _, out var modifierError),
        "A modifier-only press was accepted as a shortcut.");
    Expect(modifierError.Length > 0, "Rejecting a modifier press produced no explanation.");
    Expect(!HotKeyDefinition.TryCreate(ModifierKeys.None, Key.None, out _, out _),
        "An empty press was accepted as a shortcut.");

    Expect(HotKeyDefinition.TryCreate(ModifierKeys.Control | ModifierKeys.Shift, Key.A, out var recorded, out _),
        "A Ctrl+Shift+A press was rejected.");
    Expect(recorded.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && recorded.Key == Key.A,
        "The recorded combination lost a modifier.");
    Expect(recorded.ToString() == "Ctrl+Shift+A", $"Recording rendered as \"{recorded}\" instead of Ctrl+Shift+A.");

    // Digits, numpad, and punctuation must read as the user sees them, not as enum names.
    Expect(HotKeyDefinition.Describe(ModifierKeys.Control, Key.D1) == "Ctrl+1",
        "A digit key rendered with its enum name.");
    Expect(HotKeyDefinition.Describe(ModifierKeys.None, Key.NumPad3) == "小键盘3",
        "A numpad key rendered with its enum name.");
    Expect(HotKeyDefinition.Describe(ModifierKeys.Alt, Key.OemPlus) == "Alt++",
        "The plus key rendered with its enum name.");

    // A held modifier renders as an ellipsis while the real key is still to come.
    Expect(HotKeyDefinition.Describe(ModifierKeys.Control, Key.None) == "Ctrl+…",
        $"A held modifier rendered as \"{HotKeyDefinition.Describe(ModifierKeys.Control, Key.None)}\".");

    // What the recorder produces must be re-readable by the parser that the settings
    // page and the hotkey service share.
    foreach (var key in new[] { Key.A, Key.D1, Key.F1, Key.Space })
    {
        var text = HotKeyDefinition.Describe(ModifierKeys.Control, key);
        Expect(HotKeyDefinition.TryParse(text, out var parsed, out var error),
            $"The recorder produced \"{text}\", which the parser rejected: {error}");
        Expect(parsed.Key == key, $"Round-tripping \"{text}\" changed the key to {parsed.Key}.");
    }
}

static void TestStickerAlignment()
{
    var boundary = new Rect(0, 0, 1920, 1080);
    var peers = new List<Rect> { new(400, 300, 300, 200) };

    // Left edges four pixels apart must lock together.
    var leftSnap = StickerAlignment.Align(new Rect(404, 700, 120, 90), peers, boundary);
    Expect(leftSnap.Bounds.Left == 400, $"A near left edge snapped to {leftSnap.Bounds.Left} instead of 400.");
    Expect(leftSnap.Bounds.Top == 700, "Snapping the left edge moved the sticker vertically.");
    Expect(leftSnap.Guides.Any(guide => guide.IsVertical && guide.Position == 400),
        "Snapping the left edge drew no vertical guide.");

    // Centres must snap too, which is what makes stacked stickers look deliberate.
    var centerSnap = StickerAlignment.Align(new Rect(200, 303, 300, 200), peers, boundary);
    Expect(centerSnap.Bounds.Left == 200 && centerSnap.Bounds.Top == 300,
        $"A centre line did not snap: {centerSnap.Bounds}.");

    // A rectangle far from every line is left exactly where it was.
    var free = StickerAlignment.Align(new Rect(1000, 700, 120, 90), peers, boundary);
    Expect(free.Bounds == new Rect(1000, 700, 120, 90), $"A distant sticker was moved to {free.Bounds}.");
    Expect(free.Guides.Count == 0, "A sticker that snapped to nothing still drew guides.");

    // The threshold is respected: five pixels away snaps, ten does not.
    var insideThreshold = StickerAlignment.Align(new Rect(405, 700, 120, 90), peers, boundary);
    Expect(insideThreshold.Bounds.Left == 400,
        $"Five pixels from a peer edge snapped to {insideThreshold.Bounds.Left} instead of 400.");

    var outside = StickerAlignment.Align(new Rect(410, 700, 120, 90), peers, boundary);
    Expect(outside.Bounds.Left == 410, $"A point beyond the threshold was snapped to {outside.Bounds.Left}.");

    // Exactly at the threshold still snaps; one pixel further does not.
    var atThreshold = StickerAlignment.Align(new Rect(406, 700, 120, 90), peers, boundary);
    Expect(atThreshold.Bounds.Left == 400, "A point exactly on the threshold did not snap.");

    // Screen edges and the screen centre are both valid targets even with no peers.
    var edgeSnap = StickerAlignment.Align(new Rect(3, 500, 120, 90), [], boundary);
    Expect(edgeSnap.Bounds.Left == 0, $"A near screen edge snapped to {edgeSnap.Bounds.Left} instead of 0.");

    var screenCenter = StickerAlignment.Align(new Rect(842, 500, 240, 90), [], boundary);
    Expect(screenCenter.Bounds.Left + (screenCenter.Bounds.Width / 2) == 960,
        "The sticker centre did not snap to the screen centre.");

    // A sticker's centre must never snap onto the screen's EDGE. Dragging a sticker near the
    // right edge of the screen used to pull its centre onto that edge, which nudged it further
    // out: the user asked for a small placement and got a different one.
    // This 560-wide sticker's centre sits two pixels inside the right edge. The top is well
    // clear of the screen's horizontal centre line so only the vertical axis is under test.
    var centerNearRightEdge = StickerAlignment.Align(new Rect(1638, 700, 560, 90), [], boundary);
    Expect(centerNearRightEdge.Bounds.Left == 1638,
        $"A sticker centre was snapped onto the screen edge, moving it to {centerNearRightEdge.Bounds.Left}.");
    Expect(centerNearRightEdge.Guides.Count == 0,
        "A sticker centre matched the screen edge and drew a guide for it.");

    // The mirror case: an edge must not snap onto a centre line either.
    var edgeNearCenter = StickerAlignment.Align(new Rect(964, 500, 120, 90), [], boundary);
    Expect(edgeNearCenter.Bounds.Left == 964,
        $"A sticker edge was snapped onto the screen centre line, moving it to {edgeNearCenter.Bounds.Left}.");

    // Centre alignment against another sticker is still a target: it is what makes a stack of
    // stickers look deliberate. This rectangle's centre is two pixels from the peer's centre.
    var peerCenter = StickerAlignment.Align(new Rect(432, 500, 240, 90), peers, boundary);
    Expect(peerCenter.Bounds.Left == 430,
        $"A sticker centre did not snap to a peer centre: {peerCenter.Bounds}.");

    // Both axes can snap at once, which must produce two guides.
    var both = StickerAlignment.Align(new Rect(402, 302, 120, 90), peers, boundary);
    Expect(both.Bounds.Left == 400 && both.Bounds.Top == 300, $"Both axes did not snap: {both.Bounds}.");
    Expect(both.Guides.Count == 2, $"Two axes snapped but {both.Guides.Count} guides were drawn.");

    // Degenerate peers must not act as targets.
    var degenerate = StickerAlignment.Align(new Rect(404, 700, 120, 90), [new Rect(400, 0, 0, 0)], boundary);
    Expect(degenerate.Bounds.Left == 404, "A zero-sized peer pulled the sticker to an invisible edge.");

    // Alignment only translates; it must never resize.
    Expect(both.Bounds.Width == 120 && both.Bounds.Height == 90, "Alignment changed the sticker size.");

    // A sticker dragged past an edge must keep a grabbable strip on screen. Without this it
    // can be pushed entirely out of the desktop, where nothing can reach it again: the drag
    // stops being useful at exactly the moment the user is least likely to notice.
    var farLeft = StickerAlignment.ClampToDesktop(new Rect(-500, 500, 120, 90), boundary);
    Expect(farLeft.Left == -120 + 48, $"A sticker dragged far left was clamped to {farLeft.Left}.");
    Expect(farLeft.Right > boundary.Left, "A sticker dragged past the left edge became unreachable.");

    var farRight = StickerAlignment.ClampToDesktop(new Rect(5000, 500, 120, 90), boundary);
    Expect(farRight.Left == 1920 - 48, $"A sticker dragged far right was clamped to {farRight.Left}.");

    var farDown = StickerAlignment.ClampToDesktop(new Rect(500, 5000, 120, 90), boundary);
    Expect(farDown.Top == 1080 - 48, $"A sticker dragged far down was clamped to {farDown.Top}.");

    // A sticker that is already fully visible must not be moved at all, or the clamp would
    // fight the drag near the edges.
    var untouched = StickerAlignment.ClampToDesktop(new Rect(100, 100, 120, 90), boundary);
    Expect(untouched == new Rect(100, 100, 120, 90), $"A visible sticker was clamped to {untouched}.");

    // A sticker larger than the desktop still keeps its left and top edges reachable.
    var oversized = StickerAlignment.ClampToDesktop(new Rect(-4000, -4000, 4000, 3000), boundary);
    Expect(oversized.Left <= boundary.Left && oversized.Top <= boundary.Top,
        $"An oversized sticker was clamped to {oversized}, losing its own top-left corner.");
}

static void TestBeautifyLayout()
{
    var options = new BeautifyOptions { Padding = 40, CornerRadius = 12, ShadowBlur = 20, ShadowDepth = 6 };
    var layout = ImageBeautifier.ComputeLayout(600, 400, options);

    Expect(layout.OutputWidth == 680 && layout.OutputHeight == 480,
        $"The frame was {layout.OutputWidth}x{layout.OutputHeight} instead of 680x480.");
    Expect(layout.ImageRect == new Rect(40, 40, 600, 400), $"The picture sat at {layout.ImageRect}.");
    Expect(layout.CornerRadius == 12, "The corner radius was not carried into the layout.");
    Expect(layout.ShadowBlur == 20 && layout.ShadowDepth == 6, "A shadow that fits was shrunk anyway.");

    // Negative and oversized values are clamped rather than producing a broken render.
    var clamped = new BeautifyOptions { Padding = -50, CornerRadius = -5, ShadowBlur = 900, ShadowOpacity = 4 }.Normalize();
    Expect(clamped.Padding == 0, "A negative padding was not clamped to zero.");
    Expect(clamped.CornerRadius == 0, "A negative corner radius was not clamped to zero.");
    Expect(clamped.ShadowBlur == BeautifyOptions.MaximumShadowBlur, "An oversized blur was not clamped.");
    Expect(clamped.ShadowOpacity == 1, "An opacity above one was not clamped.");

    // The shadow is what gives way when the padding is too small, because the padding
    // is the value the user actually chose.
    var tight = ImageBeautifier.ComputeLayout(600, 400, new BeautifyOptions
    {
        Padding = 10,
        ShadowBlur = 40,
        ShadowDepth = 20,
    });
    Expect(tight.OutputWidth == 620, $"Tight padding produced width {tight.OutputWidth} instead of 620.");
    Expect(tight.ShadowBlur + tight.ShadowDepth <= 10.001,
        $"The shadow ({tight.ShadowBlur} + {tight.ShadowDepth}) did not shrink to fit the padding.");
    Expect(tight.ShadowBlur > 0, "The shadow was discarded instead of scaled down.");

    // A zero padding removes the shadow entirely rather than drawing it off-canvas.
    var none = ImageBeautifier.ComputeLayout(600, 400, new BeautifyOptions { Padding = 0, ShadowBlur = 30, ShadowDepth = 10 });
    Expect(none.OutputWidth == 600 && none.OutputHeight == 400, "Zero padding still grew the output.");
    Expect(none.ShadowBlur == 0 && none.ShadowDepth == 0, "Zero padding left a shadow that would be clipped.");

    // A radius larger than half the shorter side would make the geometry degenerate.
    var huge = ImageBeautifier.ComputeLayout(100, 40, new BeautifyOptions { Padding = 0, CornerRadius = 200 });
    Expect(huge.CornerRadius == 20, $"The corner radius was not capped at half the short side ({huge.CornerRadius}).");

    ExpectThrows(() => ImageBeautifier.ComputeLayout(0, 100, options), "A zero-width source was accepted.");

    Expect(ImageBeautifier.ParseColor("#1677FF", Colors.Black) == Color.FromRgb(0x16, 0x77, 0xFF),
        "A hex colour was not parsed.");
    Expect(ImageBeautifier.ParseColor("not a colour", Colors.Black) == Colors.Black,
        "An unparsable colour did not fall back.");
}

static void TestBeautifyPresetRoundTrip()
{
    foreach (var preset in BeautifyPresets.All)
    {
        var text = BeautifyPresets.Format(preset.Options);
        var parsed = BeautifyPresets.Parse(text);
        Expect(parsed is not null, $"Preset \"{preset.Name}\" did not survive a round trip.");
        Expect(parsed!.BackgroundStart == preset.Options.Normalize().BackgroundStart,
            $"Preset \"{preset.Name}\" lost its start colour.");
        Expect(parsed.BackgroundEnd == preset.Options.Normalize().BackgroundEnd,
            $"Preset \"{preset.Name}\" lost its end colour.");
        Expect(Math.Abs(parsed.Padding - preset.Options.Normalize().Padding) < 0.001,
            $"Preset \"{preset.Name}\" lost its padding.");
    }

    Expect(BeautifyPresets.Parse(null) is null, "An empty stored preset was not rejected.");
    Expect(BeautifyPresets.Parse("") is null, "A blank stored preset was not rejected.");
    Expect(BeautifyPresets.Parse("1|2|3") is null, "A truncated stored preset was not rejected.");
    Expect(BeautifyPresets.Parse("a|b|c|d|e|f|g") is null, "A non-numeric stored preset was not rejected.");
}

static void TestWindowHitTesting(){
    var windows = new List<Rect>
    {
        new(100, 100, 200, 200),
        new(0, 0, 1000, 1000),
    };

    Expect(WindowHitTesting.TopmostIndexAt(windows, 150, 150) == 0, "The top-most window was not selected.");
    Expect(WindowHitTesting.TopmostIndexAt(windows, 900, 900) == 1, "The desktop window was not selected.");
    Expect(WindowHitTesting.TopmostIndexAt(windows, 5000, 5000) == -1, "A point outside every window was matched.");
    Expect(WindowHitTesting.TopmostIndexAt(windows, 100, 100) == 0, "The window's top-left corner was excluded.");
    Expect(WindowHitTesting.TopmostIndexAt(windows, 300, 300) == 0, "The window's bottom-right edge was excluded.");
}

static void TestStickerGeometry()
{
    var initial = StickerGeometry.InitialSize(1920, 1080);
    Expect(initial.Width == 560, "A wide capture was not capped to the default sticker width.");
    Expect(Math.Abs((initial.Width / initial.Height) - (1920.0 / 1080.0)) < 0.01, "Initial sticker size lost the aspect ratio.");

    var tiny = StickerGeometry.InitialSize(20, 400);
    Expect(tiny.Width >= StickerGeometry.MinimumWidth, "A very narrow image fell below the minimum sticker width.");

    var grown = StickerGeometry.ResizeFromCorner(new Size(400, 200), 2, new Vector(100, 10), keepAspect: true);
    Expect(Math.Abs((grown.Width / grown.Height) - 2) < 0.01, "Corner resize did not keep the aspect ratio.");
    Expect(grown.Width > 400, "Corner resize did not grow the sticker.");

    var free = StickerGeometry.ResizeFromCorner(new Size(400, 200), 2, new Vector(100, 10), keepAspect: false);
    Expect(free.Width == 500 && free.Height == 210, "Free resize did not follow the drag delta.");

    var floored = StickerGeometry.ResizeFromCorner(new Size(400, 200), 2, new Vector(-5000, -5000), keepAspect: true);
    Expect(floored.Width >= StickerGeometry.MinimumWidth && floored.Height >= StickerGeometry.MinimumHeight,
        "Corner resize collapsed below the minimum sticker size.");

    var scaled = StickerGeometry.Scale(new Size(400, 200), 2, 1.5);
    Expect(scaled.Width == 600 && Math.Abs(scaled.Height - 300) < 0.01, "Scaling did not preserve the aspect ratio.");

    Expect(StickerGeometry.RotatedAspectRatio(2, 90) == 0.5, "A quarter turn did not swap the aspect ratio.");
    Expect(StickerGeometry.RotatedAspectRatio(2, 180) == 2, "A half turn incorrectly swapped the aspect ratio.");
}

static void TestPixelate()
{
    const int width = 24;
    const int height = 24;
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var index = ((y * width) + x) * 4;
            var value = x < width / 2 ? (byte)0x00 : (byte)0xFF;
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = 0xFF;
        }
    }

    var source = CreateBitmap(pixels, width, height);
    var pixelated = ImageProcessing.Pixelate(source, new Int32Rect(0, 0, width, height), 6);
    Expect(pixelated.PixelWidth == width && pixelated.PixelHeight == height, "Pixelation changed the image size.");

    var result = new byte[width * height * 4];
    pixelated.CopyPixels(result, width * 4, 0);

    // The left half was pure black and the right half pure white, so within one
    // 6x6 block every pixel must now carry the same value.
    for (var y = 0; y < 6; y++)
    {
        for (var x = 1; x < 6; x++)
        {
            Expect(result[((y * width) + x) * 4] == result[(y * width) * 4], "A pixel block was not flattened.");
        }
    }

    Expect(result[0] == 0x00, "The black half changed colour.");
    Expect(result[((width - 1) * 4)] == 0xFF, "The white half changed colour.");
}

static void TestBoxBlur()
{
    const int width = 16;
    const int height = 16;
    var pixels = new byte[width * height * 4];
    for (var i = 0; i < pixels.Length; i += 4)
    {
        pixels[i + 3] = 0xFF;
    }

    // A single bright pixel in the middle of a black field.
    var center = (((height / 2) * width) + (width / 2)) * 4;
    pixels[center] = 0xFF;
    pixels[center + 1] = 0xFF;
    pixels[center + 2] = 0xFF;

    var source = CreateBitmap(pixels, width, height);
    var blurred = ImageProcessing.BoxBlur(source, new Int32Rect(0, 0, width, height), 2);
    var result = new byte[width * height * 4];
    blurred.CopyPixels(result, width * 4, 0);

    var neighbour = ((((height / 2) + 1) * width) + (width / 2)) * 4;
    Expect(result[neighbour] > 0, "Blur did not spread the bright pixel to its neighbour.");
    Expect(result[center] < 0xFF, "Blur left the original pixel fully saturated.");
    Expect(result[center + 3] == 0xFF, "Blur did not preserve alpha.");

    // The untouched corner must stay black.
    Expect(result[0] == 0x00, "Blur affected a pixel outside its radius.");
}

static void TestFileNames()
{
    var name = BitmapUtilities.BuildFileName("capture/{yyyy-MM-dd}_{HHmmss}", new DateTime(2026, 8, 10, 9, 4, 5));
    Expect(!name.Contains('/'), "Invalid path separator was not sanitized.");
    Expect(name.Contains("2026-08-10_090405"), "Time tokens were not expanded.");
}

static void TestDetachedCrop()
{
    const int sourceWidth = 8;
    const int sourceHeight = 6;
    var pixels = new byte[sourceWidth * sourceHeight * 4];
    for (var i = 0; i < pixels.Length; i += 4)
    {
        pixels[i] = 0x33;
        pixels[i + 1] = 0x66;
        pixels[i + 2] = 0x99;
        pixels[i + 3] = 0xFF;
    }

    var source = CreateBitmap(pixels, sourceWidth, sourceHeight);
    var crop = BitmapUtilities.Crop(source, new Int32Rect(2, 1, 3, 2));
    Expect(crop.PixelWidth == 3 && crop.PixelHeight == 2, "Crop dimensions changed.");
    Expect(crop is not CroppedBitmap, "Crop still wraps and retains the full source bitmap.");

    var actual = new byte[3 * 2 * 4];
    crop.CopyPixels(actual, 3 * 4, 0);
    Expect(actual[0] == 0x33 && actual[1] == 0x66 && actual[2] == 0x99 && actual[3] == 0xFF,
        "Crop pixel content changed.");
}

static void TestOcrText()
{
    // Latin words genuinely need the space between them.
    Expect(OcrText.JoinWords(["PixelPin", "Capture"]) == "PixelPin Capture",
        $"Latin words joined as \"{OcrText.JoinWords(["PixelPin", "Capture"])}\".");

    // The Chinese recognizer reports one word per character. Keeping those spaces would
    // copy "截 图 贴 图", so they must be dropped.
    var chinese = OcrText.JoinWords(["截", "图", "贴", "图"]);
    Expect(chinese == "截图贴图", $"Chinese characters joined as \"{chinese}\" instead of \"截图贴图\".");
    Expect(!chinese.Contains(' ', StringComparison.Ordinal), "Chinese recognition kept word separators.");

    // A space the recognizer reports inside a Latin word must survive.
    Expect(OcrText.JoinWords(["Hello", "world"]) == "Hello world", "Latin spacing was lost.");

    // Mixed script is decided per adjacent pair, because no single per-line rule can be
    // right for both boundaries at once.
    Expect(OcrText.JoinWords(["截", "图", "tool"]) == "截图tool",
        $"Mixed words joined as \"{OcrText.JoinWords(["截", "图", "tool"])}\".");
    Expect(OcrText.JoinWords(["PixelPin", "截图"]) == "PixelPin截图",
        $"A script boundary gained a space: \"{OcrText.JoinWords(["PixelPin", "截图"])}\".");
    Expect(OcrText.JoinWords(["PixelPin", "Capture", "截图"]) == "PixelPin Capture截图",
        $"Mixed Latin and Chinese joined as \"{OcrText.JoinWords(["PixelPin", "Capture", "截图"])}\".");

    Expect(OcrText.JoinWords([]) == string.Empty, "An empty word list produced text.");
    Expect(OcrText.JoinWords(["", "  "]) == string.Empty, "Blank words produced text.");
    Expect(OcrText.JoinWords(["  spaced  "]) == "spaced", "A single word was not trimmed.");

    // Lines follow the same rule as words.
    Expect(OcrText.JoinLines(["第一行", "第二行"], string.Empty) == "第一行第二行", "Chinese lines gained a separator.");
    Expect(OcrText.JoinLines(["first", "second"], " ") == "first second", "Latin lines lost their separator.");
    Expect(OcrText.JoinLines(["a", "", "  ", "b"], " ") == "a b", "Blank lines were not dropped.");
    Expect(OcrText.JoinLines([]) == string.Empty, "An empty line list produced text.");
    Expect(OcrText.Assemble(["a", "b"]).Contains('\n'), "Assemble did not put lines on separate lines.");

    // Script detection drives the separator used between lines. "Dominated" means at
    // least half the characters are CJK, so a mostly-Latin line keeps its spaces.
    Expect(OcrText.IsCjkDominated("截图贴图"), "Pure Chinese was not detected as CJK.");
    Expect(!OcrText.IsCjkDominated("PixelPin Capture"), "Pure Latin was detected as CJK.");
    Expect(OcrText.IsCjkDominated("截图贴图工具 ab"), "Text that is mostly Chinese was not detected as CJK.");
    Expect(!OcrText.IsCjkDominated("截图 tool"), "Text that is mostly Latin was detected as CJK.");
    Expect(!OcrText.IsCjkDominated(""), "Empty text was detected as CJK.");
    Expect(!OcrText.IsCjkDominated(null), "Null text was detected as CJK.");
    Expect(!OcrText.IsCjkDominated("12345"), "Digits were detected as CJK.");
    Expect(OcrText.SeparatorFor(["截图", "贴图"]) == string.Empty, "Chinese lines kept a separator.");
    Expect(OcrText.SeparatorFor(["first", "second"]) == " ", "Latin lines lost their separator.");

    // Comparison ignores whitespace, case, and full-width forms, because a recognizer
    // reports those differently from what was rendered.
    Expect(OcrText.Matches("PixelPin Capture", "PixelPin  Capture"), "Extra whitespace broke a match.");
    Expect(OcrText.Matches("PIXELPIN", "pixelpin"), "Case broke a match.");
    Expect(OcrText.Matches("截图贴图", "截图 贴图"), "Whitespace broke a Chinese match.");
    Expect(OcrText.Matches("ＡＢＣ", "ABC"), "Full-width forms broke a match.");
    Expect(!OcrText.Matches("PixelPin", "PixelPan"), "A different string was reported as a match.");
    Expect(!OcrText.Matches("", ""), "Two empty strings were reported as a match.");
    Expect(!OcrText.Matches(null, "x"), "Null was reported as a match.");

    Expect(OcrText.Contains("PixelPin Capture 2024", "Capture"), "Containment missed a present substring.");
    Expect(!OcrText.Contains("PixelPin", "Capture"), "Containment found an absent substring.");
    Expect(!OcrText.Contains("anything", ""), "An empty needle was reported as contained.");

    // Similarity is a subsequence ratio, so one misread character does not zero it.
    Expect(Math.Abs(OcrText.Similarity("截图贴图工具", "截图贴图工具") - 1.0) < 0.001, "An identical string did not score 1.");
    var nearMiss = OcrText.Similarity("截图贴固工具", "截图贴图工具");
    Expect(nearMiss > 0.7 && nearMiss < 1.0, $"A single misread character scored {nearMiss:0.00}.");
    Expect(OcrText.Similarity("", "abc") == 0, "An empty recognition did not score 0.");
    Expect(OcrText.Similarity("abc", "") == 0, "An empty expectation did not score 0.");

    // Normalize is what the single-line copy uses.
    Expect(OcrText.Normalize("  a \n\n b\tc ") == "a b c",
        $"Normalize produced \"{OcrText.Normalize("  a \n\n b\tc ")}\".");
    Expect(OcrText.Normalize(null) == string.Empty, "Normalize did not handle null.");

    Expect(OcrText.DescribeLanguages([]).Contains("未安装", StringComparison.Ordinal),
        "An empty language list was not described as missing.");
    Expect(OcrText.DescribeLanguages([new OcrLanguage("en-US", "英语(美国)")]) == "英语(美国)",
        "A language list was not described.");
}

static void TestOcrAvailability()
{
    // The reason is what stops the feature from quietly returning an empty result that
    // looks like a successful recognition of an image with no text.
    Expect(OcrText.UnavailableReason(platformAvailable: false, languageCount: 0) is not null,
        "A missing OCR platform was reported as available.");
    Expect(OcrText.UnavailableReason(platformAvailable: true, languageCount: 0) is not null,
        "A missing language pack was reported as available.");
    Expect(OcrText.UnavailableReason(platformAvailable: true, languageCount: 2) is null,
        "A working engine was reported as unavailable.");

    var noLanguage = OcrText.UnavailableReason(platformAvailable: true, languageCount: 0)!;
    Expect(noLanguage.Contains("语言包", StringComparison.Ordinal),
        "The missing-language message did not mention language packs.");

    // This machine has recognizers installed, so the service must agree with the platform
    // rather than reporting a capability it does not have.
    var languages = OcrService.AvailableLanguages;
    foreach (var language in languages)
    {
        Expect(language.Tag.Length > 0, "A recognizer was reported without a language tag.");
        Expect(language.DisplayName.Length > 0, "A recognizer was reported without a display name.");
    }

    Expect(OcrService.IsAvailable == (languages.Count > 0), "IsAvailable disagreed with the language list.");
    Expect(OcrService.UnavailableReason is null == OcrService.IsAvailable,
        "UnavailableReason disagreed with IsAvailable.");

    // A tiny image is upscaled before recognition, because the engine cannot read glyphs
    // only a few pixels tall.
    var tiny = CreateBitmap(new byte[20 * 10 * 4], 20, 10);
    var prepared = OcrService.PrepareForRecognition(tiny);
    Expect(prepared.PixelWidth > tiny.PixelWidth,
        $"A {tiny.PixelWidth}px wide image was not upscaled for recognition.");
    Expect(prepared.PixelWidth == (int)(tiny.PixelWidth * OcrService.SmallImageUpscale),
        "The upscale factor was not applied.");

    // An image already large enough must be left alone, so recognition is not slowed or
    // blurred by needless resampling.
    var wide = CreateBitmap(new byte[200 * 40 * 4], 200, 40);
    Expect(ReferenceEquals(OcrService.PrepareForRecognition(wide), wide),
        "An already-readable image was upscaled anyway.");
}

static void TestCaptureQuality()
{
    var size = new Size(40, 40);

    // A normal image with content passes.
    var content = CreateNoise(40, 40, 0x40);
    Expect(CaptureQuality.Inspect(content, size) == CaptureRejection.None, "A normal image was rejected.");
    Expect(CaptureQuality.IsUsable(content, size), "A normal image was reported unusable.");
    // A window that rendered nothing comes back as one flat colour. That is only damning
    // when the platform also reported failure, so the flag is passed explicitly here.
    var uniform = CreateSolid(40, 40, 0xFF, 0xFF, 0xFF);
    Expect(CaptureQuality.Inspect(uniform, size, renderReportedSuccess: false) == CaptureRejection.Uniform,
        "A single-colour render that reported failure was accepted as a real capture.");

    // A genuinely blank window looks exactly the same, and refusing it would refuse to
    // screenshot a real (if empty) window.
    Expect(CaptureQuality.Inspect(uniform, size, renderReportedSuccess: true) == CaptureRejection.None,
        "A blank window that rendered successfully was refused.");

    // A hardware-accelerated or protected window renders as pure black, and that is
    // refused whether or not the platform claimed success.
    var black = CreateSolid(40, 40, 0x00, 0x00, 0x00);
    Expect(CaptureQuality.Inspect(black, size, renderReportedSuccess: false) == CaptureRejection.Black,
        "An all-black render was accepted as a real capture.");
    Expect(CaptureQuality.Inspect(black, size, renderReportedSuccess: true) == CaptureRejection.Black,
        "An all-black render was accepted because the platform claimed success.");

    // A transparent surface means GDI never drew anything, regardless of the flag.
    var transparent = CreateSolid(40, 40, 0x00, 0x00, 0x00, 0x00);
    Expect(CaptureQuality.Inspect(transparent, size) == CaptureRejection.Empty,
        "A fully transparent render was accepted as a real capture.");
    Expect(CaptureQuality.Inspect(transparent, size, renderReportedSuccess: true) == CaptureRejection.Empty,
        "A fully transparent render was accepted because the platform claimed success.");

    Expect(CaptureQuality.Inspect(null, size) == CaptureRejection.Empty, "A null image was accepted.");

    // A zero-sized bitmap cannot be constructed in WPF at all, so the guard against one
    // inside Inspect is defensive only and is not reachable from a test.

    // A size mismatch means the window moved or resized mid-capture.
    var mismatched = CreateNoise(40, 40, 0x40);
    Expect(CaptureQuality.Inspect(mismatched, new Size(80, 80)) == CaptureRejection.Empty,
        "A size mismatch was accepted.");

    // Every rejection must explain itself; a silent failure would look like a success.
    foreach (var rejection in new[] { CaptureRejection.Empty, CaptureRejection.Uniform, CaptureRejection.Black })
    {
        Expect(CaptureQuality.Describe(rejection) is { Length: > 0 },
            $"{rejection} produced no explanation.");
    }

    Expect(CaptureQuality.Describe(CaptureRejection.None) is null, "A passing image produced an explanation.");

    // A mostly-black image with a little content is a real window, not a failed render:
    // rejecting it would refuse to screenshot a dark-themed app.
    var darkButReal = CreateDarkWithContent(40, 40);
    Expect(CaptureQuality.Inspect(darkButReal, size) == CaptureRejection.None,
        "A dark but genuine window render was rejected.");
}

static void TestOcclusionCheck()
{
    var target = new Rect(0, 0, 100, 100);

    Expect(OcclusionCheck.VisibleFraction(target, null) == 1, "No occluders did not mean fully visible.");
    Expect(OcclusionCheck.VisibleFraction(target, []) == 1, "An empty occluder list did not mean fully visible.");
    Expect(OcclusionCheck.IsMostlyVisible(target, []), "An unobstructed window was reported as covered.");

    // A window covering everything hides the target completely.
    var full = new List<Rect> { new(0, 0, 100, 100) };
    Expect(OcclusionCheck.VisibleFraction(target, full) == 0, "A full cover did not hide the window.");
    Expect(!OcclusionCheck.IsMostlyVisible(target, full), "A fully covered window was reported as visible.");

    // Half covering leaves roughly half visible.
    var half = new List<Rect> { new(0, 0, 50, 100) };
    var halfFraction = OcclusionCheck.VisibleFraction(target, half);
    Expect(Math.Abs(halfFraction - 0.5) < 0.06, $"A half cover measured {halfFraction:0.000} instead of about 0.5.");
    Expect(!OcclusionCheck.IsMostlyVisible(target, half), "A half covered window was reported as visible.");

    // A tiny sliver of overlap must not change the route, because the screen crop is still
    // essentially what the user sees.
    var sliver = new List<Rect> { new(0, 0, 1, 1) };
    Expect(OcclusionCheck.IsMostlyVisible(target, sliver), "A one-pixel overlap was treated as occlusion.");

    // An occluder that misses the target entirely must not count.
    var elsewhere = new List<Rect> { new(500, 500, 100, 100) };
    Expect(OcclusionCheck.VisibleFraction(target, elsewhere) == 1, "A distant window reduced visibility.");

    // Overlapping occluders must not be double counted, which would push the fraction
    // below zero or above one.
    var overlapping = new List<Rect> { new(0, 0, 60, 100), new(0, 0, 60, 100) };
    var overlapFraction = OcclusionCheck.VisibleFraction(target, overlapping);
    Expect(overlapFraction >= 0 && overlapFraction <= 1, $"Overlapping occluders measured {overlapFraction}.");
    Expect(Math.Abs(overlapFraction - 0.4) < 0.06, $"Duplicate occluders measured {overlapFraction:0.000}.");

    // A degenerate occluder cannot cover anything.
    Expect(OcclusionCheck.VisibleFraction(target, [new Rect(0, 0, 0, 0)]) == 1,
        "A zero-sized occluder reduced visibility.");
    Expect(OcclusionCheck.VisibleFraction(new Rect(0, 0, 0, 0), full) == 0, "A zero-sized target was visible.");

    // The stacking list is top-most first, so everything before the target covers it.
    var zOrder = new List<Rect> { new(0, 0, 100, 100), new(10, 10, 50, 50), new(20, 20, 30, 30) };
    var above = OcclusionCheck.WindowsAbove(zOrder, 2);
    Expect(above.Count == 2, $"Two windows were stacked above the target but {above.Count} were reported.");
    Expect(above[0] == zOrder[0], "The occluder list was not in stacking order.");
    Expect(OcclusionCheck.WindowsAbove(zOrder, 0).Count == 0, "The top-most window reported occluders.");
    Expect(OcclusionCheck.WindowsAbove(zOrder, 9).Count == 0, "An out-of-range index produced occluders.");
    Expect(OcclusionCheck.WindowsAbove(null, 1).Count == 0, "A null stacking list produced occluders.");
    Expect(OcclusionCheck.WindowsAbove(zOrder, -1).Count == 0, "A negative index produced occluders.");
}

static void TestWindowCapturePlanner()
{
    static WindowCaptureConditions Visible() => new(1, true, true, false);
    static WindowCaptureConditions Covered() => new(0.1, true, true, false);
    static WindowCaptureConditions Partial() => new(0.7, true, true, false);

    // A window the user can fully see is read from the screen: that is exactly what they
    // are looking at, and it avoids a GPU round trip.
    var visible = WindowCapturePlanner.Build(Visible());
    Expect(!visible.IsRefused, "A fully visible window was refused.");
    Expect(visible.Routes.Count == 1 && visible.Routes[0] == WindowCaptureRoute.Screen,
        $"A fully visible window planned {visible} instead of just the screen.");

    // A covered window must never be cropped from the screen first, because that returns
    // whatever is stacked on top.
    var covered = WindowCapturePlanner.Build(Covered());
    Expect(!covered.IsRefused, "A covered window was refused while graphics capture was available.");
    Expect(covered.Routes[0] == WindowCaptureRoute.GraphicsCapture,
        $"A covered window planned {covered}, so the screen crop came first.");
    Expect(!covered.Routes.Contains(WindowCaptureRoute.Screen),
        $"A mostly covered window planned {covered}, which would crop the window on top of it.");

    // PrintWindow is still worth trying when the graphics route is unavailable.
    var coveredNoGpu = WindowCapturePlanner.Build(Covered() with { GraphicsCaptureSupported = false });
    Expect(coveredNoGpu.Routes.Count == 1 && coveredNoGpu.Routes[0] == WindowCaptureRoute.Render,
        $"Without graphics capture a covered window planned {coveredNoGpu}.");

    // A mostly visible window keeps the screen crop as a last resort, because the crop is
    // mostly right, but only after the two exact routes have been refused.
    var partial = WindowCapturePlanner.Build(Partial());
    Expect(partial.Routes[^1] == WindowCaptureRoute.Screen,
        $"A mostly visible window planned {partial}, which dropped the screen fallback.");
    Expect(partial.Routes[0] == WindowCaptureRoute.GraphicsCapture,
        $"A partly covered window planned {partial}, so the screen crop came first.");
    Expect(partial.Routes.Count == 3, $"A partly covered window planned {partial} instead of three routes.");

    // A window hanging off the edge of the desktop is fully visible but cannot be cropped
    // whole, so the composed surface has to come first. A crop is still kept as a last
    // resort: it shows the target window's on-screen part, which beats refusing outright,
    // but it is truncated so it must never be the first choice.
    var offScreen = WindowCapturePlanner.Build(Visible() with { FullyOnScreen = false });
    Expect(offScreen.Routes[0] == WindowCaptureRoute.GraphicsCapture,
        $"An off-screen window planned {offScreen}, which would crop it short.");
    Expect(offScreen.Routes.Count > 1 && offScreen.Routes[^1] == WindowCaptureRoute.Screen,
        $"An off-screen window planned {offScreen}, so a truncated crop became the only answer.");

    // Nothing can capture a minimised window, and saying so is better than returning a
    // black image.
    var minimized = WindowCapturePlanner.Build(Visible() with { Minimized = true });
    Expect(minimized.IsRefused, "A minimised window was given a capture route.");
    Expect(minimized.Refusal is not null && minimized.Refusal.Contains("最小化", StringComparison.Ordinal),
        $"The minimised refusal did not explain itself: {minimized.Refusal}");

    // A refusal and a route list are mutually exclusive: refused means no routes and a
    // reason, usable means at least one route and no reason.
    foreach (var conditions in new[] { Visible(), Covered(), Partial(), Visible() with { Minimized = true } })
    {
        var plan = WindowCapturePlanner.Build(conditions);
        Expect(plan.IsRefused == (plan.Refusal is not null),
            $"A plan both refused and explained itself inconsistently: {plan}");
    }
}

/// <summary>
/// Scrolling capture lives or dies on one question: is the vertical offset between two
/// frames known, or merely guessed? Every assertion here is about that, because a stitcher
/// that assumes a fixed wheel step produces a long image that is subtly duplicated or has
/// a gap, and looks perfectly plausible until someone reads it.
/// </summary>
static void TestScrollStitcher()
{
    const int width = 48;
    const int height = 200;
    const int stride = width * 4;

    // Every row is unique, so a correct offset is the only offset that matches. Content
    // that repeats would make the expected answer unknowable and the test meaningless.
    static byte[] Gradient(int width, int rows)
    {
        var stride = width * 4;
        var buffer = new byte[stride * rows];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * stride) + (x * 4);
                buffer[index] = (byte)(y & 0xFF);
                buffer[index + 1] = (byte)((y >> 8) & 0xFF);
                buffer[index + 2] = 0x80;
                buffer[index + 3] = 0xFF;
            }
        }

        return buffer;
    }

    static byte[] Periodic(int width, int rows, int period)
    {
        var stride = width * 4;
        var buffer = new byte[stride * rows];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * stride) + (x * 4);
                buffer[index] = (byte)((y % period) * 11);
                buffer[index + 1] = 0x40;
                buffer[index + 2] = 0x40;
                buffer[index + 3] = 0xFF;
            }
        }

        return buffer;
    }

    static byte[] Frame(byte[] content, int width, int startRow, int rows)
    {
        var stride = width * 4;
        var buffer = new byte[stride * rows];
        Array.Copy(content, (long)startRow * stride, buffer, 0, (long)rows * stride);
        return buffer;
    }

    var content = Gradient(width, 500);

    // A real scroll is measured exactly, not assumed.
    var first = Frame(content, width, 0, height);
    var scrolled = Frame(content, width, 24, height);
    var measured = ScrollStitcher.Measure(first, scrolled, width, height, stride);
    Expect(measured.IsUsable, $"A 24 pixel scroll was not measured: {measured.Failure} {measured.Reason}");
    Expect(measured.Offset == 24, $"A 24 pixel scroll was measured as {measured.Offset} pixels.");

    // An uneven offset must also come out exactly, which is what makes the capture work
    // regardless of how far the application decides a wheel notch scrolls.
    var oddScroll = ScrollStitcher.Measure(
        first, Frame(content, width, 37, height), width, height, stride);
    Expect(oddScroll.Offset == 37, $"A 37 pixel scroll was measured as {oddScroll.Offset} pixels.");

    // A real region nearly always contains something that does not scroll with the content:
    // a scrollbar whose thumb moves, a fixed header, a focus ring. The offset must still be
    // measured from the part that does scroll. Requiring a near-perfect match would reject
    // exactly the regions users select, which is what a real window exposed: a rigid 216
    // pixel scroll scored 0.948 and was refused for being 0.002 short of the gate.
    var withScrollbar = (byte[])scrolled.Clone();
    var barWidth = Math.Max(1, width / 10);
    for (var row = 0; row < height; row++)
    {
        for (var column = width - barWidth; column < width; column++)
        {
            var index = (row * stride) + (column * 4);
            // The thumb only occupies part of the bar, so the strip is genuinely different
            // from one frame to the next rather than uniformly shifted.
            var value = (byte)(((row * 7) + (column * 13)) & 0xFF);
            withScrollbar[index] = value;
            withScrollbar[index + 1] = (byte)(255 - value);
            withScrollbar[index + 2] = value;
            withScrollbar[index + 3] = 0xFF;
        }
    }

    var withBar = ScrollStitcher.Measure(first, withScrollbar, width, height, stride);
    Expect(withBar.IsUsable, $"A scroll beside a moving scrollbar was refused: {withBar.Failure} {withBar.Reason}");
    Expect(withBar.Offset == 24, $"A scroll beside a moving scrollbar was measured as {withBar.Offset} pixels.");


    var still = ScrollStitcher.Measure(first, Frame(content, width, 0, height), width, height, stride);
    Expect(still.IsFinished, $"Identical frames were reported as {still.Failure} instead of finished.");
    Expect(still.Offset == 0, $"Identical frames reported an offset of {still.Offset}.");

    // Repeating content matches at more than one offset. Choosing the highest scoring one
    // would silently misalign the stitch, so it must be refused.
    var repeating = Periodic(width, 500, 20);
    var ambiguous = ScrollStitcher.Measure(
        Frame(repeating, width, 0, height),
        Frame(repeating, width, 24, height),
        width,
        height,
        stride);
    Expect(ambiguous.Failure == ScrollStepFailure.Ambiguous,
        $"Repeating content was reported as {ambiguous.Failure} instead of ambiguous.");
    Expect(ambiguous.Reason is not null && ambiguous.Reason.Contains("重复", StringComparison.Ordinal),
        $"The ambiguity refusal did not explain itself: {ambiguous.Reason}");

    // A blank page scrolls without any visible change, so the two frames are byte-identical.
    // No offset can be read from that, and stopping is the safe answer: the result is one
    // honest screenshot rather than a stitched image built on an invented offset. This is
    // why uniform content is reported as "no movement" and not as a refusal.
    var uniform = Periodic(width, 500, 1);
    var blank = ScrollStitcher.Measure(
        Frame(uniform, width, 0, height), Frame(uniform, width, 24, height), width, height, stride);
    Expect(blank.Failure == ScrollStepFailure.NoMovement,
        $"Uniform content was reported as {blank.Failure} instead of no movement.");

    // Unrelated frames must not be forced into a match.
    var random = new Random(20240607);
    var noiseA = new byte[stride * height];
    var noiseB = new byte[stride * height];
    random.NextBytes(noiseA);
    random.NextBytes(noiseB);
    var unrelated = ScrollStitcher.Measure(noiseA, noiseB, width, height, stride);
    Expect(unrelated.Failure == ScrollStepFailure.Unmatched,
        $"Unrelated frames were reported as {unrelated.Failure} instead of unmatched.");

    // A scroll that consumed the whole viewport leaves almost no overlap, so continuity
    // cannot be proven from it.
    var tooFar = ScrollStitcher.Measure(
        first, Frame(content, width, height - 16, height), width, height, stride);
    Expect(tooFar.Failure == ScrollStepFailure.TooLarge,
        $"A scroll leaving the minimum overlap was reported as {tooFar.Failure} instead of too large.");

    // Scrolling further than the viewport is tall has no overlap at all.
    var beyond = ScrollStitcher.Measure(
        first, Frame(content, width, height + 40, height), width, height, stride);
    Expect(beyond.Failure is ScrollStepFailure.Unmatched or ScrollStepFailure.TooLarge,
        $"A scroll larger than the viewport was reported as {beyond.Failure}.");

    // The decisive assertion: appending the measured offset must reproduce the content
    // that scrolled into view, with no duplicated and no missing rows.
    var expected = Frame(content, width, 0, height + 24);
    var stitched = ScrollStitcher.Append(first, height, scrolled, height, width, stride, measured.Offset);
    Expect(stitched.Length == stride * (height + 24),
        $"Stitching a 24 pixel scroll produced {stitched.Length / stride} rows instead of {height + 24}.");
    Expect(stitched.SequenceEqual(expected),
        "The stitched image did not match the content that scrolled into view, so rows were duplicated or lost.");

    // Without this the assertion above could pass on an implementation that ignores the
    // offset entirely, so confirm a wrong offset really does produce a different image.
    var wrong = ScrollStitcher.Append(first, height, scrolled, height, width, stride, 20);
    Expect(!wrong.SequenceEqual(expected),
        "A wrong offset produced the expected image, so the stitching assertion proves nothing.");

    // A second step must accumulate on the stitched image rather than on the last frame.
    var secondScrolled = Frame(content, width, 61, height);
    var secondOffset = ScrollStitcher.Measure(scrolled, secondScrolled, width, height, stride);
    Expect(secondOffset.Offset == 37, $"The second step was measured as {secondOffset.Offset} pixels.");
    var grown = ScrollStitcher.Append(stitched, height + 24, secondScrolled, height, width, stride, secondOffset.Offset);
    var expectedTwo = Frame(content, width, 0, height + 61);
    Expect(grown.SequenceEqual(expectedTwo),
        $"Two chained steps produced {grown.Length / stride} rows that did not match the source content.");

    // Guards that exist so a caller cannot ask for nonsense.
    Expect(Throws<ArgumentOutOfRangeException>(
            () => ScrollStitcher.Append(first, height, scrolled, height, width, stride, 0)),
        "Appending a zero offset was allowed.");
    Expect(Throws<ArgumentOutOfRangeException>(
            () => ScrollStitcher.Append(first, height, scrolled, height, width, stride, height + 1)),
        "Appending more rows than the frame holds was allowed.");
}

static bool Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
        return false;
    }
    catch (TException)
    {
        return true;
    }
}

/// <summary>A bitmap with varied pixel values, standing in for a real window render.</summary>
static BitmapSource CreateNoise(int width, int height, byte baseValue){
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var index = ((y * width) + x) * 4;
            pixels[index] = (byte)((x * 7) % 256);
            pixels[index + 1] = (byte)((y * 11) % 256);
            pixels[index + 2] = baseValue;
            pixels[index + 3] = 0xFF;
        }
    }

    return CreateBitmap(pixels, width, height);
}

/// <summary>A bitmap filled with a single colour and alpha.</summary>
static BitmapSource CreateSolid(int width, int height, byte red, byte green, byte blue, byte alpha = 0xFF)
{
    var pixels = new byte[width * height * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
        pixels[index + 3] = alpha;
    }

    return CreateBitmap(pixels, width, height);
}

/// <summary>
/// A dark image with a small bright region and many near-black shades, which is what a
/// dark-themed window looks like. It must pass, because refusing it would refuse to
/// screenshot a real app.
/// </summary>
static BitmapSource CreateDarkWithContent(int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var index = ((y * width) + x) * 4;

            // A small bright block standing in for text, over a background that varies
            // across several dark shades the way antialiased glyphs and borders do.
            var bright = x < 4 && y < 4;
            var shade = (byte)(bright ? 0x00 : 0x18 + ((x + y) % 24));

            pixels[index] = shade;
            pixels[index + 1] = (byte)(shade + 2);
            pixels[index + 2] = (byte)(shade + 4);
            pixels[index + 3] = 0xFF;
        }
    }

    return CreateBitmap(pixels, width, height);
}

static BitmapSource CreateBitmap(byte[] pixels, int width, int height)
{
    var bitmap = BitmapSource.Create(
        width,
        height,
        96,
        96,
        PixelFormats.Bgra32,
        null,
        pixels,
        width * 4);
    bitmap.Freeze();
    return bitmap;
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
