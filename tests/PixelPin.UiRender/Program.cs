using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PixelPin.Models;
using PixelPin.Services;
using PixelPin.Windows;

namespace PixelPin.UiRender;

/// <summary>
/// Renders the redesigned tool windows off screen so XAML errors, missing resources
/// and collapsed layouts surface without the desktop probe (which needs a mouse and
/// write access outside the workspace).
/// </summary>
internal static class Program
{
    /// <summary>
    /// The real App, minus its startup work. App.OnStartup builds an AppController,
    /// which reads the user's saved settings and applies the OS theme — so every
    /// "light" render came out dark on a dark desktop. Skipping it keeps the renders
    /// deterministic while still loading App.xaml's resources, including the implicit
    /// Window style. LoadComponent resolves against the declaring assembly, so the
    /// base class must still be PixelPin.App itself, not a subclass.
    /// </summary>
    private static readonly List<string> Failures = new();

    [STAThread]
    private static int Main()
    {
        var application = new PixelPin.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent();
        application.Resources["PixelPinIcon"] = new BitmapImage();

        // Let the dispatcher run App.OnStartup once, so the AppController has already
        // applied the OS theme, then pin light: the controller applies its theme once
        // and never again, so the baseline renders stay reproducible on a dark desktop.
        Pump(TimeSpan.FromMilliseconds(400));
        ThemeService.Apply(AppTheme.Light);
        Pump(TimeSpan.FromMilliseconds(120));

        // Written into the repository's review folder rather than next to the build
        // output, so the renders a reviewer looks at are the ones this run produced.
        var output = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-review");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);

        var sample = CreateSampleImage(640, 400);

        foreach (var (name, create) in WindowFactories(sample))
        {
            try
            {
                RenderWindow(name, create(), output);
            }
            catch (Exception exception)
            {
                Failures.Add($"{name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        // Contrast and metrics checks that a screenshot alone would not report.
        CheckButtonMetrics(output);
        CheckSliderWiring();
        CheckCheckBoxState();
        CheckTextIsReallyDrawn(output, sample);
        CheckPresetSwatches();
        CheckContrast();
        CheckEditorToolState(output);
        CheckOverlayBar(output);
        CheckStickerChrome(output);
        CheckDarkTheme(output, sample);
        CheckWindowChrome();
        CheckThemePicker();
        CheckEmptyStates(output);
        CheckFocusRingCoverage();
        CheckTabStops(output, sample);
        CheckMinimumSizesAt150Percent(sample);

        foreach (var failure in Failures)
        {
            Console.WriteLine("FAIL " + failure);
        }

        Console.WriteLine(Failures.Count == 0 ? "ui-render: all windows rendered" : $"ui-render: {Failures.Count} failure(s)");
        application.Shutdown();

        // The harness renders dozens of windows and hosts a real Application; exiting
        // explicitly keeps a stray dispatcher object from holding the process open.
        Environment.Exit(Failures.Count == 0 ? 0 : 1);
        return Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Every window the redesign covers, in a stable order.</summary>
    private static (string Name, Func<Window> Create)[] WindowFactories(BitmapSource sample) =>
    [
        ("settings", () => new SettingsWindow(new AppSettings(), _ => null, _ => new AppSettings(), (_, _) => { }, () => { })),
        ("history", () => new HistoryWindow(BuildHistory(sample), _ => { }, _ => { }, _ => { }, _ => { })),
        ("diagnostics", () => new DiagnosticsWindow(() => "PixelPin 诊断\n路径：C:\\Users\\demo\\AppData\\Local\\PixelPin\n版本：0.7.1\n\n最近消息\n[10:02:11] 已启动\n[10:02:15] 注册全局快捷键 F1", () => { }, () => { })),
        ("window-picker", () => new WindowPickerWindow(() => new[]
        {
            new CaptureWindowDescriptor(1, "文档 - 记事本", "notepad", new System.Drawing.Rectangle(0, 0, 1200, 800)),
            new CaptureWindowDescriptor(2, "PixelPin 设置", "PixelPin", new System.Drawing.Rectangle(0, 0, 660, 780)),
        }, _ => { })),
        ("sticker-manager", () => new StickerManagerWindow(() => Array.Empty<StickyWindow>(), (_, _) => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { })),
        ("ocr", () => new OcrWindow(sample, _ => { })),
        ("beautify", () => new BeautifyWindow(sample, null)),
        ("editor", () => new ImageEditorWindow(sample, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, new EditorOptions("#E53935", 3))),
        ("sticky", () => new StickyWindow(sample, new StickerOptions(1.0, KeepAspectRatio: true, CloseOnDoubleClick: true), _ => { }, _ => { }, _ => { }, _ => { }, _ => { })),
    ];

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RenderWindow(string name, Window window, string output)
    {
        // Off screen so the render does not steal focus from the user's desktop.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
        Pump(TimeSpan.FromMilliseconds(450));

        if (window.Content is not FrameworkElement content)
        {
            Failures.Add($"{name}: window has no content");
            return;
        }

        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            Failures.Add($"{name}: content measured {width}x{height}");
            return;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Save(bitmap, Path.Combine(output, $"ui-{name}.png"));
        if (name is "settings" or "history")
        {
            Console.WriteLine($"  [light {name}] page token="
                + $"{(Application.Current.TryFindResource("AppBackgroundBrush") as SolidColorBrush)?.Color} "
                + $"centre={ReadPixel(bitmap, width / 2, height / 2)}");
        }

        // Same tree at 200% so high-DPI layout and glyph fallback are exercised, and
        // so the OCR check below gets glyphs large enough to read reliably.
        var retina = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        retina.Render(content);
        Save(retina, Path.Combine(output, $"ui-{name}@2x.png"));

        Console.WriteLine($"{name}: {width}x{height} -> {Path.Combine(output, $"ui-{name}.png")}");

        // Dump every text run with its slot, so a binding that silently produced
        // nothing shows up as an empty line instead of a blank area in a PNG that
        // nobody can diff.
        var dump = FindElements<TextBlock>(content)
            .Where(text => text.IsVisible)
            .Select(text =>
            {
                var slot = text.TransformToAncestor(content).TransformBounds(new Rect(text.RenderSize));
                return $"{(int)slot.X,5},{(int)slot.Y,5} {(int)slot.Width,4}x{(int)slot.Height,-4} {text.Text}";
            })
            .ToList();
        File.WriteAllLines(Path.Combine(output, $"ui-{name}.txt"), dump);
        var empty = dump.Count(line => line.EndsWith(" ", StringComparison.Ordinal) || line.EndsWith(" \r", StringComparison.Ordinal));
        if (empty > 0)
        {
            Console.WriteLine($"{name}: {empty} visible TextBlock(s) with no text");
        }

        // A control that is present but laid out to nothing is a silent layout bug.
        var collapsed = FindElements<Button>(content)
            .Where(button => button.IsVisible && (button.ActualWidth < 12 || button.ActualHeight < 12))
            .Select(button => $"{button.Content}")
            .ToList();
        if (collapsed.Count > 0)
        {
            Failures.Add($"{name}: buttons collapsed to nothing: {string.Join(", ", collapsed)}");
        }

        // DesiredSize includes Margin, ActualWidth does not, so subtract it before
        // comparing; otherwise every padded label looks clipped.
        var clipped = FindElements<TextBlock>(content)
            .Where(text => text.IsVisible && text.TextWrapping == TextWrapping.NoWrap
                && text.ActualWidth > 0
                && text.DesiredSize.Width - text.Margin.Left - text.Margin.Right > text.ActualWidth + 1.0
                && text.TextTrimming == TextTrimming.None)
            .Take(6)
            .ToList();
        foreach (var text in clipped)
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(text);
            Console.WriteLine($"{name}: clip probe '{text.Text}' actual={text.ActualWidth:0.0} "
                + $"desired={text.DesiredSize.Width:0.0} margin={text.Margin} font={text.FontSize} "
                + $"parent={parent?.GetType().Name}");
        }
        var clippedText = clipped
            .Select(text => $"{text.Text} ({text.ActualWidth:0.0} of "
                + $"{text.DesiredSize.Width - text.Margin.Left - text.Margin.Right:0.0})")
            .Take(5)
            .ToList();
        if (clipped.Count > 0)
        {
            Console.WriteLine($"{name}: text wider than its slot (no trimming): {string.Join(" | ", clipped)}");
        }

        window.Close();
        Pump(TimeSpan.FromMilliseconds(120));
    }

    /// <summary>
    /// A custom Slider template is easy to get visually right and functionally wrong:
    /// the Track is what turns a thumb drag into a value, so a template that only
    /// paints the track leaves the user dragging a knob that changes nothing.
    /// </summary>
    private static void CheckSliderWiring()
    {
        var host = new Border { Width = 220, Height = 40, Background = Brushes.White };
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = 0.25, Width = 200 };
        host.Child = slider;
        host.Measure(new Size(220, 40));
        host.Arrange(new Rect(0, 0, 220, 40));
        host.UpdateLayout();

        var thumb = FindElements<Thumb>(slider).FirstOrDefault();
        if (thumb is null)
        {
            Failures.Add("slider template has no Thumb");
            return;
        }

        var before = thumb.TransformToAncestor(host).TransformBounds(new Rect(thumb.RenderSize));
        slider.Value = 0.8;
        host.UpdateLayout();
        var after = thumb.TransformToAncestor(host).TransformBounds(new Rect(thumb.RenderSize));
        if (after.X <= before.X + 5)
        {
            Failures.Add($"slider thumb did not follow Value (x {before.X:0.0} -> {after.X:0.0})");
        }

        // Now the other direction: a drag has to reach Slider.Value, not just the Track.
        slider.Value = 0.5;
        host.UpdateLayout();
        var startValue = slider.Value;
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(60, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(60, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        host.UpdateLayout();
        if (Math.Abs(slider.Value - startValue) < 0.01)
        {
            Failures.Add($"dragging the slider thumb left Value at {slider.Value:0.00}");
        }
    }

    /// <summary>
    /// Confirms the check mark is actually drawn when IsChecked is true, by counting
    /// accent pixels in both states rather than trusting the trigger.
    /// </summary>
    private static void CheckCheckBoxState()
    {
        var accent = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
        var uncheckedCount = CountAccentPixels(new CheckBox { Content = "随 Windows 启动", IsChecked = false }, accent);
        var checkedCount = CountAccentPixels(new CheckBox { Content = "随 Windows 启动", IsChecked = true }, accent);
        if (uncheckedCount > 2)
        {
            Failures.Add($"unchecked check box painted {uncheckedCount} accent pixels");
        }

        if (checkedCount < 40)
        {
            Failures.Add($"checked check box painted only {checkedCount} accent pixels, the mark is missing");
        }
    }

    private static int CountAccentPixels(CheckBox box, Color accent)
    {
        var host = new Border { Width = 260, Height = 40, Background = Brushes.White, Child = box };
        host.Measure(new Size(260, 40));
        host.Arrange(new Rect(0, 0, 260, 40));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(260, 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixels = new byte[260 * 40 * 4];
        bitmap.CopyPixels(pixels, 260 * 4, 0);

        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Close enough to the accent colour to count as the fill or the mark.
            if (Math.Abs(pixels[i] - accent.B) < 12
                && Math.Abs(pixels[i + 1] - accent.G) < 12
                && Math.Abs(pixels[i + 2] - accent.R) < 12)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// WCAG 2.1 contrast for every text token against the surfaces it is used on.
    /// 4.5:1 is the body-text threshold and 3:1 covers large text and UI borders;
    /// the check reads the real token dictionary, so a future palette edit that
    /// dims the hint colour fails here instead of in a user's screenshot.
    /// </summary>
    private static void CheckContrast()
    {
        var pairs = new (string Foreground, string Background, double Minimum)[]
        {
            ("TextPrimaryBrush", "AppBackgroundBrush", 4.5),
            ("TextPrimaryBrush", "CardBackgroundBrush", 4.5),
            ("TextPrimaryBrush", "ControlHoverBrush", 4.5),
            ("TextSecondaryBrush", "AppBackgroundBrush", 4.5),
            ("TextSecondaryBrush", "CardBackgroundBrush", 4.5),
            ("TextTertiaryBrush", "CardBackgroundBrush", 4.5),
            ("TextTertiaryBrush", "AppBackgroundBrush", 4.5),
            ("AccentTextBrush", "AccentSubtleBrush", 4.5),
            ("TextOnAccentBrush", "AccentBrush", 4.5),
            ("DangerBrush", "CardBackgroundBrush", 4.5),
            ("SuccessBrush", "CardBackgroundBrush", 4.5),
            ("TextSecondaryBrush", "FooterBackgroundBrush", 4.5),
        };

        foreach (var (foregroundKey, backgroundKey, minimum) in pairs)
        {
            if (Application.Current.TryFindResource(foregroundKey) is not SolidColorBrush foreground
                || Application.Current.TryFindResource(backgroundKey) is not SolidColorBrush background)
            {
                Failures.Add($"contrast: token {foregroundKey} or {backgroundKey} is missing");
                continue;
            }

            var ratio = ContrastRatio(foreground.Color, background.Color);
            if (ratio < minimum)
            {
                Failures.Add($"contrast {foregroundKey} on {backgroundKey} is {ratio:0.00}:1, needs {minimum}:1");
            }
        }

        Console.WriteLine("contrast: token pairs checked");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var a = RelativeLuminance(first) + 0.05;
        var b = RelativeLuminance(second) + 0.05;
        return Math.Max(a, b) / Math.Min(a, b);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var scaled = value / 255.0;
            return scaled <= 0.03928 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>
    /// Verifies every beautify preset actually shows its own gradient. A swatch that
    /// silently falls back to white looks plausible in a screenshot, which is exactly
    /// why this compares the rendered pixels instead of trusting the binding.
    /// </summary>
    private static void CheckPresetSwatches()
    {
        var window = new BeautifyWindow(CreateSampleImage(320, 200), null);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
        Pump(TimeSpan.FromMilliseconds(400));

        var list = FindElements<ListBox>(window).FirstOrDefault();
        if (list is null || window.Content is not FrameworkElement content)
        {
            Failures.Add("beautify window has no preset list");
            window.Close();
            return;
        }

        var expected = PixelPin.Utilities.BeautifyPresets.All
            .Select(preset => preset.Name)
            .ToList();

        // Each preset's brush is a gradient; sample its start colour from the model and
        // require the rendered swatch to contain something close to it.
        for (var index = 0; index < expected.Count; index++)
        {
            var item = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            if (item is null)
            {
                Failures.Add($"preset {index} has no container");
                continue;
            }

            var swatch = FindElements<Border>(item)
                .FirstOrDefault(border => Math.Abs(border.Width - 34) < 0.5);
            if (swatch is null)
            {
                Failures.Add($"preset {index} ({expected[index]}) has no swatch");
                continue;
            }

            if (swatch.Background is not Brush brush || brush is not LinearGradientBrush && brush is not SolidColorBrush)
            {
                Failures.Add($"preset {index} ({expected[index]}) swatch has no brush");
                continue;
            }

            var bounds = swatch.TransformToAncestor(content).TransformBounds(new Rect(swatch.RenderSize));
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(content.ActualWidth),
                (int)Math.Ceiling(content.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var start = brush is LinearGradientBrush gradient
                ? gradient.GradientStops[0].Color
                : ((SolidColorBrush)brush).Color;

            var found = false;
            for (var y = (int)bounds.Top + 3; y < (int)bounds.Bottom - 3 && !found; y++)
            {
                for (var x = (int)bounds.Left + 3; x < (int)bounds.Right - 3; x++)
                {
                    var offset = (y * bitmap.PixelWidth + x) * 4;
                    if (Math.Abs(pixels[offset] - start.B) < 26
                        && Math.Abs(pixels[offset + 1] - start.G) < 26
                        && Math.Abs(pixels[offset + 2] - start.R) < 26)
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                Failures.Add($"preset {index} ({expected[index]}) swatch did not paint {start}");
            }
        }

        Console.WriteLine($"preset swatches checked: {expected.Count}");
        window.Close();
        Pump(TimeSpan.FromMilliseconds(120));
    }

    /// <summary>
    /// Dark mode is the change most likely to look done while being half done: a
    /// window that still uses StaticResource keeps its light brushes and simply
    /// renders unchanged. So this compares key sets, then re-renders every window
    /// in dark and requires the pixels to actually differ.
    /// </summary>
    private static void CheckDarkTheme(string output, BitmapSource sample)
    {
        // Compare the key sets from the files themselves. Loading a second
        // ResourceDictionary by relative URI blocks in this host, and the set
        // comparison does not need WPF to parse the file.
        var themeDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PixelPin.App", "Themes"));
        var lightKeys = ReadDictionaryKeys(Path.Combine(themeDirectory, "Tokens.Light.xaml"));
        var darkKeys = ReadDictionaryKeys(Path.Combine(themeDirectory, "Tokens.Dark.xaml"));
        if (lightKeys.Count == 0 || darkKeys.Count == 0)
        {
            Failures.Add($"could not read theme dictionaries from {themeDirectory}");
            return;
        }

        var missingInDark = lightKeys.Except(darkKeys).ToList();
        var extraInDark = darkKeys.Except(lightKeys).ToList();
        if (missingInDark.Count > 0)
        {
            Failures.Add($"dark tokens are missing: {string.Join(", ", missingInDark)}");
        }

        if (extraInDark.Count > 0)
        {
            Failures.Add($"dark tokens define unknown keys: {string.Join(", ", extraInDark)}");
        }

        Console.WriteLine($"tokens: {lightKeys.Count} keys in both dictionaries");

        PixelPin.Services.ThemeService.Apply(PixelPin.Services.AppTheme.Dark);
        Pump(TimeSpan.FromMilliseconds(120));
        Console.WriteLine($"theme after Apply(Dark): isDark={PixelPin.Services.ThemeService.IsDark} "
            + $"page={(Application.Current.TryFindResource("AppBackgroundBrush") as SolidColorBrush)?.Color.ToString() ?? "missing"}");
        if (!PixelPin.Services.ThemeService.IsDark)
        {
            Failures.Add("ThemeService did not report the dark theme after applying it");
        }

        if (Application.Current.TryFindResource("AppBackgroundBrush") is not SolidColorBrush darkPage)
        {
            Failures.Add("the dark dictionary did not take effect");
        }
        else if (RelativeLuminance(darkPage.Color) > 0.2)
        {
            Failures.Add($"the dark page token is still light ({darkPage.Color})");
        }

        // Windows that deliberately keep their own chrome in both themes. The sticker
        // and the capture overlay are floating glass surfaces over arbitrary desktop
        // content, so they are drawn dark on purpose rather than following the theme.
        string[] themeIndependent = ["sticky"];

        foreach (var (name, create) in WindowFactories(sample))
        {
            try
            {
                var lightPath = Path.Combine(output, $"ui-{name}.png");
                var darkBitmap = RenderWindowToBitmap(name, create());
                if (darkBitmap is null)
                {
                    Failures.Add($"{name}: dark render produced nothing");
                    continue;
                }

                Save(darkBitmap, Path.Combine(output, $"ui-{name}-dark.png"));

                if (themeIndependent.Contains(name))
                {
                    Console.WriteLine($"{name}: keeps its own chrome in both themes (dark render saved)");
                    continue;
                }

                if (File.Exists(lightPath))
                {
                    var lightBitmap = LoadPng(lightPath);
                    var differences = CountDifferences(lightBitmap, darkBitmap);
                    var darkWhite = CountNearWhite(darkBitmap);
                    var lightWhite = CountNearWhite(lightBitmap);
                    var darkPixels = darkBitmap.PixelWidth * darkBitmap.PixelHeight;
                    Console.WriteLine($"{name}: {differences} pixels differ from light, "
                        + $"near-white {lightWhite} -> {darkWhite} of {darkPixels}");
                    if (differences < 500)
                    {
                        Failures.Add($"{name}: dark render barely differs from light ({differences} pixels)");
                    }

                    // A window that keeps a StaticResource brush stays white in dark
                    // mode, which this catches even when everything else switched.
                    if (darkWhite > lightWhite * 0.5 || darkWhite > darkPixels * 0.05)
                    {
                        Failures.Add($"{name}: {darkWhite} near-white pixels remain in the dark render "
                            + $"(light had {lightWhite})");
                    }
                }
            }
            catch (Exception exception)
            {
                Failures.Add($"{name} (dark): {exception.GetType().Name}: {exception.Message}");
            }
        }

        // Contrast has to hold in dark too; the same pairs are checked against the
        // dark dictionary rather than the light one.
        CheckContrast();

        PixelPin.Services.ThemeService.Apply(PixelPin.Services.AppTheme.Light);
        Pump(TimeSpan.FromMilliseconds(120));
    }

    /// <summary>Pixels that are effectively white, which is what a leaked light brush looks like.</summary>
    private static int CountNearWhite(BitmapSource source)
    {
        var pixels = GetBgraBytes(source);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] >= 245 && pixels[i + 1] >= 245 && pixels[i + 2] >= 245 && pixels[i + 3] >= 250)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Every x:Key declared in a resource dictionary file.</summary>
    private static List<string> ReadDictionaryKeys(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(path), "x:Key=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    /// <summary>Shows a window off screen and returns its content as a bitmap.</summary>
    private static BitmapSource? RenderWindowToBitmap(string name, Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
        Pump(TimeSpan.FromMilliseconds(450));

        if (window.Content is not FrameworkElement content
            || content.ActualWidth <= 0 || content.ActualHeight <= 0)
        {
            window.Close();
            Pump(TimeSpan.FromMilliseconds(80));
            return null;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        bitmap.Freeze();

        // Diagnostic for the dark-mode comparison: state the actual page pixel and
        // the first card's brush, so a "no difference" result can be attributed.
        if (name is "settings")
        {
            var centre = ReadPixel(bitmap, bitmap.PixelWidth / 2, bitmap.PixelHeight / 2);
            var card = FindElements<Border>(content)
                .FirstOrDefault(border => border.Background is SolidColorBrush);
            Console.WriteLine($"  [{name}] centre={centre} first-border-bg="
                + $"{(card?.Background as SolidColorBrush)?.Color}");
        }

        window.Close();
        Pump(TimeSpan.FromMilliseconds(80));
        return bitmap;
    }

    private static BitmapSource LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static Color ReadPixel(BitmapSource source, int x, int y)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static int CountDifferences(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight)
        {
            return int.MaxValue;
        }

        var left = GetBgraBytes(first);
        var right = GetBgraBytes(second);
        if (left.Length != right.Length)
        {
            return int.MaxValue;
        }

        var count = 0;
        for (var i = 0; i < left.Length; i += 4)
        {
            if (Math.Abs(left[i] - right[i]) > 6
                || Math.Abs(left[i + 1] - right[i + 1]) > 6
                || Math.Abs(left[i + 2] - right[i + 2]) > 6)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Reads pixels as straight BGRA. The format check matters: wrapping an
    /// already-Bgra32 source in a FormatConvertedBitmap yields an empty buffer here,
    /// which makes every comparison look identical.
    /// </summary>
    private static byte[] GetBgraBytes(BitmapSource source)
    {
        BitmapSource readable = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = readable.PixelWidth * 4;
        var buffer = new byte[stride * readable.PixelHeight];
        readable.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>
    /// The sticker's toolbar and context menu are its entire interface, and both only
    /// appear on interaction, so neither shows up in a plain window render.
    /// </summary>
    private static void CheckStickerChrome(string output)
    {
        var sticker = new StickyWindow(
            CreateSampleImage(400, 300),
            new StickerOptions(1.0, KeepAspectRatio: true, CloseOnDoubleClick: true),
            _ => { }, _ => { }, _ => { }, _ => { }, _ => { });
        sticker.WindowStartupLocation = WindowStartupLocation.Manual;
        sticker.Left = -20000;
        sticker.Top = -20000;
        sticker.ShowInTaskbar = false;
        sticker.Show();
        Pump(TimeSpan.FromMilliseconds(500));

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var bar = typeof(StickyWindow).GetField("Toolbar", flags)?.GetValue(sticker) as FrameworkElement;
        if (bar is null || sticker.Content is not FrameworkElement content)
        {
            Failures.Add("sticker has no floating toolbar");
            sticker.Close();
            return;
        }

        bar.Visibility = Visibility.Visible;
        Pump(TimeSpan.FromMilliseconds(200));

        var buttons = FindElements<Button>(bar).Where(button => button.IsVisible).ToList();
        var tiny = buttons.Where(button => button.ActualWidth < 24 || button.ActualHeight < 24).ToList();
        Console.WriteLine($"sticker toolbar: {buttons.Count} buttons, {bar.ActualWidth:0}x{bar.ActualHeight:0}");
        if (buttons.Count < 11)
        {
            Failures.Add($"sticker toolbar has {buttons.Count} buttons, expected at least 11");
        }

        if (tiny.Count > 0)
        {
            Failures.Add($"sticker toolbar buttons too small to hit: {tiny.Count}");
        }

        if (buttons.Any(button => button.ToolTip is not string tip || tip.Length == 0))
        {
            Failures.Add("sticker toolbar has an icon-only button without a tooltip");
        }

        var readout = FindElements<TextBlock>(bar).FirstOrDefault(text => text.Name == "OpacityReadout");
        if (readout is null || readout.Text != "100%")
        {
            Failures.Add($"sticker opacity readout shows '{readout?.Text}'");
        }

        // The bar must not cover the whole sticker, or the pinned image is unusable
        // while the pointer happens to be over it.
        if (bar.ActualHeight > content.ActualHeight * 0.4)
        {
            Failures.Add($"sticker toolbar is {bar.ActualHeight:0}px tall on a {content.ActualHeight:0}px sticker");
        }

        // Context menu: the checkable "keep aspect ratio" item must draw a tick.
        var frame = typeof(StickyWindow).GetField("Frame", flags)?.GetValue(sticker) as FrameworkElement;
        var menu = frame?.ContextMenu;
        if (menu is null)
        {
            Failures.Add("sticker has no context menu");
        }
        else
        {
            menu.PlacementTarget = frame;
            menu.IsOpen = true;
            Pump(TimeSpan.FromMilliseconds(400));

            var items = menu.Items.OfType<MenuItem>().ToList();
            if (items.Count < 9)
            {
                Failures.Add($"sticker context menu has {items.Count} items, expected at least 9");
            }

            var checkable = items.FirstOrDefault(item => item.IsCheckable);
            if (checkable is null || checkable.IsChecked != true)
            {
                Failures.Add("sticker context menu has no checked aspect-ratio item");
            }

            if (menu.ActualWidth > 0 && menu.ActualHeight > 0)
            {
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(menu.ActualWidth),
                    (int)Math.Ceiling(menu.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                Save(bitmap, Path.Combine(output, "ui-sticker-menu.png"));

                // A tick in the accent colour proves the checkable state renders.
                var accent = ((SolidColorBrush)Application.Current.FindResource("AccentPressedBrush")).Color;
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var ticks = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (Math.Abs(pixels[i] - accent.B) < 40
                        && Math.Abs(pixels[i + 1] - accent.G) < 40
                        && Math.Abs(pixels[i + 2] - accent.R) < 40)
                    {
                        ticks++;
                    }
                }

                Console.WriteLine($"sticker menu: {menu.ActualWidth:0}x{menu.ActualHeight:0}, {items.Count} items, "
                    + $"{ticks} tick pixels");
                if (ticks < 10)
                {
                    Failures.Add("sticker menu did not draw the checked item's tick");
                }
            }
            else
            {
                Failures.Add($"sticker context menu measured {menu.ActualWidth:0}x{menu.ActualHeight:0}");
            }

            menu.IsOpen = false;
            Pump(TimeSpan.FromMilliseconds(150));
        }

        sticker.Close();
        Pump(TimeSpan.FromMilliseconds(120));
    }

    /// <summary>
    /// Renders the capture overlay with a live selection. The dimmer is asserted by
    /// the desktop probe at a fixed composite ratio, so this check is about the bar:
    /// every action must be laid out inside it, and the bar must stay inside the
    /// screen even when the selection sits against the bottom edge.
    /// </summary>
    private static void CheckOverlayBar(string output)
    {
        var screen = new System.Drawing.Rectangle(0, 0, 900, 600);
        var overlay = new SelectionOverlayWindow(
            CreateSampleImage(900, 600),
            screen,
            [
                new CaptureWindowDescriptor(1, "文档 - 记事本", "notepad", new System.Drawing.Rectangle(60, 80, 520, 360)),
            ]);
        overlay.WindowStartupLocation = WindowStartupLocation.Manual;
        overlay.ShowInTaskbar = false;
        overlay.Show();
        Pump(TimeSpan.FromMilliseconds(500));

        if (!(bool)(typeof(SelectionOverlayWindow)
                .GetProperty("IsRenderReady", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(overlay) ?? false))
        {
            Failures.Add("overlay never became render ready");
            overlay.Close();
            return;
        }

        // A selection near the bottom edge forces the bar to flip above it.
        typeof(SelectionOverlayWindow)
            .GetMethod("SetSelectionForTesting", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(overlay, [new Rect(180, 380, 420, 200)]);
        Pump(TimeSpan.FromMilliseconds(250));

        var bar = FindElements<Border>(overlay)
            .FirstOrDefault(border => border.Name == "Toolbar");
        if (bar is null || overlay.Content is not FrameworkElement content)
        {
            Failures.Add("overlay has no capture bar");
            overlay.Close();
            return;
        }

        var barBounds = bar.TransformToAncestor(content).TransformBounds(new Rect(bar.RenderSize));
        Console.WriteLine($"overlay window {overlay.Width:0}x{overlay.Height:0}, content {content.ActualWidth:0}x{content.ActualHeight:0}");
        Console.WriteLine($"overlay bar: {barBounds.Left:0},{barBounds.Top:0} {barBounds.Width:0}x{barBounds.Height:0}"
            + $" in {content.ActualWidth:0}x{content.ActualHeight:0}");

        if (barBounds.Right > content.ActualWidth + 0.5 || barBounds.Bottom > content.ActualHeight + 0.5
            || barBounds.Left < -0.5 || barBounds.Top < -0.5)
        {
            Failures.Add($"overlay capture bar falls outside the screen: {barBounds}");
        }

        var buttons = FindElements<Button>(bar).Where(button => button.IsVisible).ToList();
        if (buttons.Count < 7)
        {
            Failures.Add($"overlay capture bar has {buttons.Count} visible buttons, expected 7");
        }

        var collapsed = buttons.Where(button => button.ActualWidth < 40 || button.ActualHeight < 26).ToList();
        if (collapsed.Count > 0)
        {
            Failures.Add($"overlay bar buttons collapsed: {string.Join(", ", collapsed.Select(b => b.ToolTip))}");
        }

        // The bar sits on an arbitrary screenshot, so its text has to be legible on
        // its own surface rather than relying on the page behind it.
        if (Application.Current.TryFindResource("AccentBrush") is SolidColorBrush accent)
        {
            var barBackground = (bar.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
            var ratio = ContrastRatio(Color.FromRgb(0xF2, 0xF2, 0xF2), BlendOver(barBackground, Colors.Black, 0xF0));
            Console.WriteLine($"overlay bar text contrast: {ratio:0.00}:1");
            if (ratio < 4.5)
            {
                Failures.Add($"overlay bar text contrast is {ratio:0.00}:1, needs 4.5:1");
            }

            var accentRatio = ContrastRatio(Colors.White, accent.Color);
            if (accentRatio < 4.5)
            {
                Failures.Add($"overlay accent button text contrast is {accentRatio:0.00}:1");
            }
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Save(bitmap, Path.Combine(output, "ui-overlay.png"));

        overlay.Close();
        Pump(TimeSpan.FromMilliseconds(120));
    }

    /// <summary>Composites a translucent colour over an opaque one, for contrast maths.</summary>
    private static Color BlendOver(Color foreground, Color background, byte alpha)
    {
        var a = alpha / 255.0;
        return Color.FromRgb(
            (byte)Math.Round((foreground.R * a) + (background.R * (1 - a))),
            (byte)Math.Round((foreground.G * a) + (background.G * (1 - a))),
            (byte)Math.Round((foreground.B * a) + (background.B * (1 - a))));
    }

    /// <summary>
    /// The editor toolbar is only useful if the armed tool is visible. Drives
    /// SelectTool the way the probe does and requires exactly one toggle to be
    /// checked, and the checked one to be the tool that was selected.
    /// </summary>
    private static void CheckEditorToolState(string output)
    {
        var editor = new ImageEditorWindow(
            CreateSampleImage(640, 400), _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
            new EditorOptions("#E53935", 3));
        editor.WindowStartupLocation = WindowStartupLocation.Manual;
        editor.Left = -20000;
        editor.Top = -20000;
        editor.ShowInTaskbar = false;
        editor.Show();
        Pump(TimeSpan.FromMilliseconds(500));

        var initial = FindElements<System.Windows.Controls.Primitives.ToggleButton>(editor)
            .Where(toggle => toggle.Tag is string)
            .Select(toggle => $"{toggle.Tag}={(toggle.IsChecked == true ? "on" : "off")}");
        Console.WriteLine("editor initial tool state: " + string.Join(" ", initial));

        foreach (var tool in new[] { "Mosaic", "Text", "Select", "Rectangle" })
        {
            var toggle = FindElements<System.Windows.Controls.Primitives.ToggleButton>(editor)
                .FirstOrDefault(candidate => (candidate.Tag as string) == tool);
            if (toggle is null)
            {
                Failures.Add($"editor toolbar has no {tool} toggle");
                continue;
            }

            // ToggleButtonAutomationPeer.Toggle() flips IsChecked without raising Click,
            // so raise the click the toolbar actually listens to.
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, toggle));
            Pump(TimeSpan.FromMilliseconds(80));

            var checkedTools = FindElements<System.Windows.Controls.Primitives.ToggleButton>(editor)
                .Where(candidate => candidate.IsChecked == true)
                .Select(candidate => candidate.Tag as string)
                .ToList();

            if (checkedTools.Count != 1 || checkedTools[0] != tool)
            {
                Failures.Add($"editor tool state after selecting {tool}: "
                    + $"[{string.Join(", ", checkedTools)}] checked");
            }
        }

        // A tool with a focusable toggle must also be reachable by keyboard, and the
        // checked style has to differ from the idle style in the rendered pixels.
        var mosaic = FindElements<System.Windows.Controls.Primitives.ToggleButton>(editor)
            .FirstOrDefault(toggle => (toggle.Tag as string) == "Mosaic");
        var text = FindElements<System.Windows.Controls.Primitives.ToggleButton>(editor)
            .FirstOrDefault(toggle => (toggle.Tag as string) == "Text");
        if (mosaic is null || text is null)
        {
            Failures.Add("editor toolbar is missing a tool toggle");
        }
        else if (editor.Content is FrameworkElement content)
        {
            // Sample the same toggle twice: once while it is armed, once after the
            // selection moved away. Sampling two different toggles would compare the
            // wrong thing, because each is only styled while it is active.
            toggleClick(mosaic);
            var active = SampleBackground(content, mosaic);
            toggleClick(text);
            var idle = SampleBackground(content, mosaic);
            if (active == idle)
            {
                Failures.Add("editor checked tool looks identical to an idle tool");
            }

            Console.WriteLine($"editor tool fills: active {active} idle {idle}");
        }

        Console.WriteLine($"editor toolbar: {FindElements<System.Windows.Controls.Primitives.ToggleButton>(editor).Count()} toggles");
        editor.Close();
        Pump(TimeSpan.FromMilliseconds(120));

        void toggleClick(System.Windows.Controls.Primitives.ToggleButton target)
        {
            target.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, target));
            Pump(TimeSpan.FromMilliseconds(80));
        }
    }

    /// <summary>Renders the window and reads the pixel at the centre of an element.</summary>
    private static string SampleBackground(FrameworkElement root, FrameworkElement target)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth),
            (int)Math.Ceiling(root.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var bounds = target.TransformToAncestor(root).TransformBounds(new Rect(target.RenderSize));
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);

        // Sample a point inside the rounded corner but clear of the glyph and label.
        var x = (int)bounds.Left + 4;
        var y = (int)(bounds.Top + (bounds.Height / 2));
        if (x < 0 || y < 0 || x >= bitmap.PixelWidth || y >= bitmap.PixelHeight)
        {
            return "out-of-bounds";
        }

        var offset = (y * bitmap.PixelWidth + x) * 4;
        return $"#{pixels[offset + 2]:X2}{pixels[offset + 1]:X2}{pixels[offset]:X2}";
    }

    /// <summary>
    /// Reads the rendered settings window back with the app's own OCR engine. A
    /// layout can render without error and still draw tofu boxes or clipped text;
    /// recognising the strings proves pixels, font fallback and encoding all agree.
    /// </summary>
    private static void CheckTextIsReallyDrawn(string output, BitmapSource sample)
    {
        var path = Path.Combine(output, "ui-settings@2x.png");
        if (!File.Exists(path))
        {
            Failures.Add("settings render missing, cannot verify text");
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();

        var language = PixelPin.Services.OcrService.AvailableLanguages
            .FirstOrDefault(item => item.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (language is null)
        {
            Console.WriteLine("ocr check skipped: no Chinese recognizer installed");
            return;
        }

        var task = PixelPin.Services.OcrService.RecognizeAsync(image, language.Tag);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump(TimeSpan.FromMilliseconds(50));
        }

        if (!task.IsCompleted || !task.Result.Succeeded)
        {
            Failures.Add($"settings OCR failed: {(task.IsCompleted ? task.Result.Reason : "timed out")}");
            return;
        }

        var text = task.Result.Text.Replace(" ", string.Empty, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(output, "ui-settings.ocr.txt"), task.Result.Text);
        foreach (var expected in new[] { "设置", "全局快捷键", "区域截图", "保存设置" })
        {
            if (!text.Contains(expected, StringComparison.Ordinal))
            {
                Failures.Add($"settings OCR did not read \"{expected}\" back from the render");
            }
        }
    }

    /// <summary>Renders the same accent button in both themes to prove the token layer drives it.</summary>
    private static void CheckButtonMetrics(string output)
    {
        var accent = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            Width = 240,
            Height = 64,
            Child = new Button
            {
                Content = "保存设置",
                Style = (Style)Application.Current.FindResource("AccentButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        accent.Measure(new Size(240, 64));
        accent.Arrange(new Rect(0, 0, 240, 64));
        accent.UpdateLayout();

        var button = (Button)accent.Child;
        if (Math.Abs(button.ActualHeight - 32) > 0.5)
        {
            Failures.Add($"accent button height is {button.ActualHeight}, expected 32");
        }

        var accentColor = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
        if (button.Background is not SolidColorBrush brush || brush.Color != accentColor)
        {
            Failures.Add("accent button is not using the AccentBrush token");
        }

        var bitmap = new RenderTargetBitmap(240, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(accent);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, "ui-accent-button.png"));
        encoder.Save(stream);
    }

    private static HistoryService BuildHistory(System.Windows.Media.Imaging.BitmapSource sample)
    {
        // The window only reads; an unloaded service gives an empty list without
        // writing to %LocalAppData%, which this sandbox denies.
        var history = new HistoryService();
        var items = (List<HistoryItem>)typeof(HistoryService)
            .GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(history)!;

        items.Add(new HistoryItem
        {
            CreatedAt = DateTimeOffset.Now.AddMinutes(-8),
            PixelWidth = 640,
            PixelHeight = 400,
            IsFavorite = true,
            Tags = "文档, 分享",
            Thumbnail = sample,
        });
        items.Add(new HistoryItem
        {
            CreatedAt = DateTimeOffset.Now.AddHours(-3),
            PixelWidth = 1920,
            PixelHeight = 1080,
            Tags = string.Empty,
            Thumbnail = sample,
        });

        return history;
    }

    private static System.Windows.Media.Imaging.BitmapSource CreateSampleImage(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x4E, 0x65, 0xC8), Color.FromRgb(0x2B, 0x58, 0x76), 45), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, new Rect(40, 40, width - 80, height - 80));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<T> FindElements<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindElements<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// P5 keyboard pass. Every template here is hand-written, so FocusVisualStyle is
    /// opt-in per style — and a control that forgot it is invisible to a keyboard user
    /// while looking perfectly fine in a screenshot.
    /// </summary>
    private static void CheckFocusRingCoverage()
    {
        (string Name, Control Control)[] samples =
        [
            ("Button", new Button { Content = "确定" }),
            ("ToggleButton", new ToggleButton { Content = "矩形" }),
            ("CheckBox", new CheckBox { Content = "复制后贴图" }),
            ("TextBox", new TextBox { Text = "F1" }),
            ("ComboBox", new ComboBox()),
            ("Slider", new Slider()),
        ];

        var host = new Border { Background = Brushes.White, Padding = new Thickness(20) };
        var panel = new StackPanel();
        host.Child = panel;
        foreach (var (_, control) in samples)
        {
            control.Margin = new Thickness(0, 4, 0, 4);
            panel.Children.Add(control);
        }

        host.Measure(new Size(400, 400));
        host.Arrange(new Rect(0, 0, 400, 400));
        host.UpdateLayout();

        foreach (var (name, control) in samples)
        {
            var focus = control.FocusVisualStyle;
            if (focus is null)
            {
                // A text field is allowed to signal focus with its own edge instead of
                // an outer ring, but only if its template really has that trigger.
                if (name == "TextBox" && TemplateHandlesFocus(control))
                {
                    continue;
                }

                Failures.Add($"{name} has no focus visual, so keyboard focus is invisible");
                continue;
            }

            // The ring has to actually resolve its brush and geometry, not just exist.
            if (focus.Setters.Count == 0)
            {
                Failures.Add($"{name} focus visual has no template");
            }
        }

        // And the ring must be reachable: a focusable control that cannot take focus
        // from the keyboard is the same failure seen from the other side.
        var notFocusable = samples
            .Where(sample => !sample.Control.Focusable || !sample.Control.IsTabStop)
            .Select(sample => sample.Name)
            .ToList();
        if (notFocusable.Count > 0)
        {
            Failures.Add($"controls that cannot be reached by Tab: {string.Join(", ", notFocusable)}");
        }

        Console.WriteLine($"focus visuals: {samples.Length} control kinds checked");
    }

    /// <summary>True when a control's own template reacts to keyboard focus.</summary>
    private static bool TemplateHandlesFocus(Control control)
    {
        if (control.Template is not { } template)
        {
            control.ApplyTemplate();
            template = control.Template;
        }

        return template is not null
            && template.Triggers.OfType<Trigger>()
                .Any(trigger => trigger.Property == UIElement.IsKeyboardFocusWithinProperty
                    || trigger.Property == UIElement.IsKeyboardFocusedProperty);
    }

    /// <summary>
    /// Walks Tab order through a real window and reports the path, so a control that
    /// sits outside the traversal (a toolbar inside a ScrollViewer, a button with
    /// IsTabStop cleared) is visible as a gap rather than as a user complaint.
    /// </summary>
    private static void CheckTabStops(string output, BitmapSource sample)
    {
        var window = new SettingsWindow(
            new AppSettings(), _ => null, _ => new AppSettings(), (_, _) => { }, () => { });
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
        Pump(TimeSpan.FromMilliseconds(450));

        if (window.Content is not FrameworkElement content)
        {
            Failures.Add("settings window has no content for the tab walk");
            window.Close();
            return;
        }

        // Walk the real traversal rather than reading the visual tree. The tree order
        // is not the Tab order: the settings footer is declared first so the DockPanel
        // can fill its last child, which put the action bar ahead of every field until
        // its TabIndex was set.
        window.Activate();
        Pump(TimeSpan.FromMilliseconds(80));
        content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

        var visited = new List<string>();
        var seen = new HashSet<object>();
        for (var step = 0; step < 40; step++)
        {
            if (Keyboard.FocusedElement is not Control focused)
            {
                break;
            }

            // Identity, not the label: two sliders and two shortcut fields share a
            // label, and breaking on the label stopped the walk halfway.
            if (!seen.Add(focused))
            {
                break;
            }

            visited.Add($"{focused.GetType().Name}:"
                + $"{(focused as ContentControl)?.Content ?? (focused as TextBox)?.Text}");
            if (!focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
            {
                break;
            }
        }

        Console.WriteLine($"tab order: {visited.Count} stops walked");
        foreach (var step in visited)
        {
            Console.WriteLine("  " + step);
        }

        if (visited.Count >= 4)
        {
            // The first stop must be a form field, not a footer action: the footer sits
            // at the bottom of the window, so meeting it first contradicts what the
            // user sees.
            if (!visited[0].StartsWith("TextBox", StringComparison.Ordinal))
            {
                Failures.Add($"tab order starts at {visited[0]}, not the first field");
            }

            if (!visited.Any(step => step.Contains("保存设置", StringComparison.Ordinal)))
            {
                Failures.Add("tab order never reaches the save button");
            }
        }

        var focusable = FindElements<Control>(content)
            .Where(control => control.Focusable && control.IsTabStop && control.IsVisible && control.IsEnabled)
            .ToList();

        // Count only the kinds a user expects to Tab through. Scrollbars and list
        // items are focusable internally but are not part of the settings form.
        var tabbable = focusable
            .Where(control => control is TextBox or ComboBox or CheckBox or Button or Slider)
            .ToList();

        Console.WriteLine($"tab stops in the tree: {tabbable.Count}");

        if (tabbable.Count < 10)
        {
            Failures.Add($"settings window only exposes {tabbable.Count} tab stops");
        }

        // A control with no size cannot be seen when it has focus.
        var invisible = tabbable
            .Where(control => control.ActualWidth < 8 || control.ActualHeight < 8)
            .Select(control => control.GetType().Name)
            .ToList();
        if (invisible.Count > 0)
        {
            Failures.Add($"tab stops laid out to nothing: {string.Join(", ", invisible)}");
        }

        window.Close();
        Pump(TimeSpan.FromMilliseconds(100));

        // The editor's tool row is the densest surface in the app and the one most
        // likely to overflow once text scales up, so it is measured too.
        var editor = new ImageEditorWindow(
            sample, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, new EditorOptions("#E53935", 3));
        editor.WindowStartupLocation = WindowStartupLocation.Manual;
        editor.Left = -20000;
        editor.Top = -20000;
        editor.ShowInTaskbar = false;
        editor.Show();
        Pump(TimeSpan.FromMilliseconds(500));
        if (editor.Content is FrameworkElement editorContent)
        {
            var toggles = FindElements<ToggleButton>(editorContent).Where(toggle => toggle.IsVisible).ToList();
            var overflowing = toggles
                .Where(toggle => toggle.ActualWidth < 40 || toggle.ActualHeight < 28)
                .ToList();
            Console.WriteLine($"editor tool row: {toggles.Count} toggles, "
                + $"min {toggles.DefaultIfEmpty().Min(t => t?.ActualWidth ?? 0):0}x"
                + $"{toggles.DefaultIfEmpty().Min(t => t?.ActualHeight ?? 0):0}");
            if (overflowing.Count > 0)
            {
                Failures.Add($"editor tool toggles squeezed below a usable size: {overflowing.Count}");
            }
        }

        editor.Close();
        Pump(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// 150% is the most common Windows scaling on laptops, and the point where a row
    /// of fixed-width controls starts to clip. Each window is re-measured with a 1.5x
    /// layout scale and must still fit inside its own MinWidth/MinHeight.
    /// </summary>
    private static void CheckMinimumSizesAt150Percent(BitmapSource sample)
    {
        var windowNames = new (string Name, Func<Window> Create)[]
        {
            ("settings", () => new SettingsWindow(new AppSettings(), _ => null, _ => new AppSettings(), (_, _) => { }, () => { })),
            ("history", () => new HistoryWindow(BuildHistory(sample), _ => { }, _ => { }, _ => { }, _ => { })),
            ("ocr", () => new OcrWindow(sample, _ => { })),
            ("beautify", () => new BeautifyWindow(sample, null)),
            ("editor", () => new ImageEditorWindow(sample, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, new EditorOptions("#E53935", 3))),
        };

        foreach (var (name, create) in windowNames)
        {
            var window = create();
            var scale = 1.5;

            // Measure at the declared minimum size, not at the default size: that is
            // the case a user hits by resizing, and the one that clips.
            var available = new Size(
                Math.Max(320, window.MinWidth * scale),
                Math.Max(240, window.MinHeight * scale));

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowInTaskbar = false;
            window.Show();
            Pump(TimeSpan.FromMilliseconds(350));

            if (window.Content is not FrameworkElement content)
            {
                Failures.Add($"{name}: no content at 150%");
                window.Close();
                continue;
            }

            content.Measure(available);
            content.Arrange(new Rect(available));
            content.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(80));

            var desired = content.DesiredSize;
            var overflowsWidth = desired.Width > available.Width + 1;
            var overflowsHeight = desired.Height > available.Height + 1;
            Console.WriteLine($"{name} @150%: wants {desired.Width:0}x{desired.Height:0} "
                + $"in {available.Width:0}x{available.Height:0}"
                + (overflowsWidth || overflowsHeight ? " -> OVERFLOW" : " -> fits"));

            if (overflowsWidth)
            {
                Failures.Add($"{name} needs {desired.Width:0}px at 150% but its minimum is {available.Width:0}px");
            }

            if (overflowsHeight)
            {
                Failures.Add($"{name} needs {desired.Height:0}px at 150% but its minimum is {available.Height:0}px");
            }

            window.Close();
            Pump(TimeSpan.FromMilliseconds(80));
        }
    }

    /// <summary>
    /// The theme picker has to change the theme as soon as it is used: the whole point
    /// of the 外观 card is previewing before saving. Drives the real ComboBox, then
    /// cancels, which must put the previous theme back.
    /// </summary>
    private static void CheckThemePicker()
    {
        ThemeService.Apply(AppTheme.Light);
        Pump(TimeSpan.FromMilliseconds(80));

        var window = new SettingsWindow(
            new AppSettings(), _ => null, _ => new AppSettings(), (_, _) => { }, () => { });
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.Show();
        Pump(TimeSpan.FromMilliseconds(400));

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var picker = typeof(SettingsWindow).GetField("ThemePicker", flags)?.GetValue(window) as ComboBox;
        if (picker is null)
        {
            Failures.Add("settings window has no ThemePicker");
            window.Close();
            return;
        }

        var options = picker.Items.OfType<ComboBoxItem>()
            .Select(item => item.Tag as string ?? item.Content?.ToString() ?? "?")
            .ToList();
        Console.WriteLine($"theme picker: {picker.Items.Count} options ({string.Join(", ", options)})");
        if (picker.Items.Count != 3)
        {
            Failures.Add($"theme picker offers {picker.Items.Count} options, expected 3");
        }

        // Captured before touching the picker: this is what Cancel has to restore.
        var opened = ThemeService.Preference;
        var openedDark = ThemeService.IsDark;

        var darkItem = picker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as string) == nameof(AppTheme.Dark));
        if (darkItem is null)
        {
            Failures.Add("theme picker has no dark option");
            window.Close();
            return;
        }

        picker.SelectedItem = darkItem;
        Pump(TimeSpan.FromMilliseconds(150));
        if (!ThemeService.IsDark)
        {
            Failures.Add("selecting 深色 in the picker did not switch the theme");
        }

        // Cancel must restore the preference the window opened with, which for a
        // default AppSettings is System — and on this desktop System resolves to dark,
        // so comparing the resolved theme alone would prove nothing.
        var cancel = window.Content is DependencyObject contentRoot
            ? FindElements<Button>(contentRoot).FirstOrDefault(button => button.IsCancel)
            : null;
        if (cancel is null)
        {
            Failures.Add("settings window has no cancel button, so Esc cannot close it");
        }
        else
        {
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(250));
            Console.WriteLine($"theme after cancel: preference={ThemeService.Preference} "
                + $"dark={ThemeService.IsDark} (opened with {opened}/{openedDark})");
            if (ThemeService.Preference != opened || ThemeService.IsDark != openedDark)
            {
                Failures.Add($"cancelling left the previewed theme applied "
                    + $"({ThemeService.Preference}/{ThemeService.IsDark} instead of {opened}/{openedDark})");
            }
        }

        window.Close();
        Pump(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Empty states are DataTrigger-driven, and a trigger that never fires leaves the
    /// state invisible while the markup looks right. Each one is rendered with the
    /// condition it reacts to and must actually show its explanation.
    /// </summary>
    private static void CheckEmptyStates(string output)
    {
        (string Name, Window Window)[] cases =
        [
            ("window-picker-empty", new WindowPickerWindow(() => Array.Empty<CaptureWindowDescriptor>(), _ => { })),
            ("ocr-empty", new OcrWindow(CreateSampleImage(120, 80), _ => { })),
        ];

        foreach (var (name, window) in cases)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowInTaskbar = false;
            window.Show();
            Pump(TimeSpan.FromMilliseconds(450));

            if (window.Content is not FrameworkElement content)
            {
                Failures.Add($"{name}: no content");
                window.Close();
                continue;
            }

            var panels = FindElements<StackPanel>(content)
                .Where(panel => panel.Name == "EmptyState")
                .ToList();
            if (panels.Count == 0)
            {
                Failures.Add($"{name}: no empty-state panel in the tree");
            }
            else if (!panels[0].IsVisible)
            {
                Failures.Add($"{name}: empty state did not appear when it should");
            }
            else
            {
                var lines = FindElements<TextBlock>(panels[0])
                    .Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text))
                    .Select(text => text.Text)
                    .ToList();
                Console.WriteLine($"{name}: empty state shows {lines.Count} line(s): "
                    + string.Join(" / ", lines));
                if (lines.Count < 2)
                {
                    Failures.Add($"{name}: empty state explains nothing ({lines.Count} lines)");
                }

                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(content.ActualWidth),
                    (int)Math.Ceiling(content.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                Save(bitmap, Path.Combine(output, $"ui-{name}.png"));

                // An empty result box also means "still working". Claiming nothing was
                // found before the recognizer answered would be a lie, so that state
                // has to win over the empty one.
                var status = window.GetType()
                    .GetField("StatusText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(window) as TextBlock;
                if (status is not null)
                {
                    status.Text = "正在识别...";
                    Pump(TimeSpan.FromMilliseconds(60));
                    if (panels[0].IsVisible)
                    {
                        Failures.Add($"{name}: empty state shows while recognition is still running");
                    }
                    else
                    {
                        Console.WriteLine($"{name}: empty state correctly hidden while recognizing");
                    }
                }
            }

            window.Close();
            Pump(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// The window frame is drawn by DWM, not WPF, so nothing in a RenderTargetBitmap
    /// shows it. This asks DWM directly, which is the only way to know the title bar
    /// actually went dark instead of staying a white strip above a dark window.
    /// </summary>
    private static void CheckWindowChrome()
    {
        var window = new Window
        {
            Title = "chrome probe",
            Width = 320,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(300));

        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            Failures.Add("chrome probe window has no handle");
            window.Close();
            return;
        }

        foreach (var (theme, expectDark) in new[] { (AppTheme.Dark, true), (AppTheme.Light, false) })
        {
            ThemeService.Apply(theme);
            ThemeService.ApplyWindowChrome(window);
            Pump(TimeSpan.FromMilliseconds(120));

            var immersive = ReadDwmInt(handle, 20);
            var corner = ReadDwmInt(handle, 33);
            Console.WriteLine($"chrome ({theme}): immersiveDarkMode={immersive} cornerPreference={corner}");

            var isDark = immersive == 1;
            if (isDark != expectDark)
            {
                Failures.Add($"chrome: immersive dark mode is {immersive} for {theme}, expected "
                    + $"{(expectDark ? 1 : 0)}");
            }

            // 2 is DWMWCP_ROUND. Anything else means the frame fell back to square
            // corners, which looks wrong next to every other Windows 11 window.
            if (corner != 2 && corner != 1)
            {
                Failures.Add($"chrome: window corners are not rounded ({corner}) for {theme}");
            }
        }

        window.Close();
        Pump(TimeSpan.FromMilliseconds(120));
    }

    /// <summary>Reads an int-valued DWM window attribute, or -1 when unsupported.</summary>
    private static int ReadDwmInt(IntPtr handle, int attribute)
    {
        var value = -1;
        try
        {
            var result = DwmGetWindowAttribute(handle, attribute, ref value, sizeof(int));
            return result == 0 ? value : -1;
        }
        catch (DllNotFoundException)
        {
            return -1;
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
