using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using PixelPin.Interop;
using PixelPin.Models;
using PixelPin.Utilities;

namespace PixelPin.Services;

public sealed class ScreenCaptureService
{
    /// <summary>Shell surfaces that are technically windows but are never a capture target.</summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "XamlExplorerHostIslandWindow",
        "Windows.Internal.Shell.TabProxyWindow",
        "SysShadow",
        "NarratorHelperWindow",
        "DV2ControlHost",
    };

    public IReadOnlyList<CapturedScreen> CaptureScreens()
    {
        var captures = new List<CapturedScreen>();
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            captures.Add(new CapturedScreen(BitmapUtilities.ToBitmapSource(bitmap), bounds));
        }

        return captures;
    }

    public CapturedDesktop CaptureDesktop()
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Windows 没有报告可用的桌面。");
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return new CapturedDesktop(BitmapUtilities.ToBitmapSource(bitmap), bounds);
    }

    /// <summary>
    /// Captures one rectangle of the virtual desktop. Used by scrolling capture, which needs
    /// the same region again and again while the application underneath it scrolls, so it
    /// cannot rely on a full-desktop image that would be stale by the second frame.
    /// </summary>
    public BitmapSource? CaptureRegion(Int32Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return null;
        }

        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.X, region.Y, 0, 0, new System.Drawing.Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
        return BitmapUtilities.ToBitmapSource(bitmap);
    }

    /// <summary>Captures every display in one image, without showing a selection overlay.</summary>
    public CaptureOutcome CaptureFullScreen()
    {
        var desktop = CaptureDesktop();
        return new CaptureOutcome(desktop.Image, "PixelPin_full_{yyyyMMdd_HHmmss}");
    }

    public CaptureOutcome CaptureForegroundWindow()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || NativeMethods.IsIconic(window))
        {
            throw new InvalidOperationException("没有可截图的可见前台窗口。");
        }

        return CaptureWindowHandle(window, "PixelPin_window_{yyyyMMdd_HHmmss}");
    }

    public IReadOnlyList<CaptureWindowDescriptor> EnumerateCaptureWindows()
    {
        var windows = new List<CaptureWindowDescriptor>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
                {
                    return true;
                }

                var titleLength = NativeMethods.GetWindowTextLength(handle);
                if (titleLength <= 0 || !NativeMethods.GetWindowRect(handle, out var bounds)
                    || bounds.Width < 32 || bounds.Height < 32)
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(handle, out var processId);
                if (processId == Environment.ProcessId)
                {
                    return true;
                }

                var builder = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(handle, builder, builder.Capacity);
                var title = builder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                var processName = GetProcessName(processId);
                windows.Add(new CaptureWindowDescriptor(
                    handle,
                    title,
                    processName,
                    new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                    GetWindowClass(handle)));
            }
            catch
            {
                // A window can disappear while EnumWindows is walking the desktop.
            }

            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Visible application windows in z-order, top-most first. This is the list the
    /// capture overlay uses to highlight and snap to a window under the pointer, so it
    /// skips shell surfaces, cloaked UWP hosts, tool windows, and PixelPin itself.
    /// </summary>
    public IReadOnlyList<CaptureWindowDescriptor> EnumerateZOrderWindows()
    {
        var windows = new List<CaptureWindowDescriptor>();
        var ownProcessId = Environment.ProcessId;

        NativeMethods.EnumWindows((handle, _) =>
        {
            try
            {
                if (TryDescribeWindow(handle, ownProcessId, out var descriptor))
                {
                    windows.Add(descriptor);
                }
            }
            catch
            {
                // A window can disappear while EnumWindows is walking the desktop.
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public CaptureOutcome CaptureWindow(CaptureWindowDescriptor window)
    {
        if (window.Handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(window.Handle) || NativeMethods.IsIconic(window.Handle))
        {
            throw new InvalidOperationException("所选窗口已不可用，请刷新列表后重新选择。");
        }

        return CaptureWindowHandle(window.Handle, "PixelPin_window_{yyyyMMdd_HHmmss}");
    }

    private static bool TryDescribeWindow(nint handle, int ownProcessId, out CaptureWindowDescriptor descriptor)
    {
        descriptor = null!;

        if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(handle, out var rect) || rect.Width < 16 || rect.Height < 16)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == (uint)ownProcessId)
        {
            return false;
        }

        if (IsCloaked(handle))
        {
            return false;
        }

        var extendedStyle = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        if ((extendedStyle & (int)NativeMethods.WsExToolWindow) != 0
            || (extendedStyle & (int)NativeMethods.WsExNoActivate) != 0
            || (extendedStyle & (int)NativeMethods.WsExTransparent) != 0)
        {
            return false;
        }

        var className = GetWindowClass(handle);
        if (ShellWindowClasses.Contains(className))
        {
            return false;
        }

        var title = GetWindowTitle(handle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        descriptor = CaptureWindowDescriptor.Detection(
            handle,
            title,
            className,
            new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height));
        return true;
    }

    private static bool IsCloaked(nint handle)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DwmwaCloaked, out var cloaked, sizeof(int)) == 0
                && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string GetWindowClass(nint handle)
    {
        var builder = new StringBuilder(256);
        return NativeMethods.GetClassName(handle, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static string GetWindowTitle(nint handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private CaptureOutcome CaptureWindowHandle(nint window, string suggestedFileName)
    {
        var bounds = WindowCaptureService.GetCaptureBounds(window);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("Windows 无法确定当前窗口边界。");
        }

        var (visible, above) = MeasureOcclusion(window, bounds);
        var conditions = new WindowCaptureConditions(
            visible,
            WgcCaptureService.IsSupported,
            IsFullyOnScreen(bounds),
            NativeMethods.IsIconic(window));

        var result = WindowCaptureService.TryCaptureWindow(window, conditions, CropDesktop);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Failure ?? "无法截取该窗口。");
        }

        return new CaptureOutcome(result.Image!, suggestedFileName);
    }

    /// <summary>
    /// Crops a window rectangle out of a fresh desktop capture, or null when the rectangle
    /// does not overlap the desktop at all.
    /// </summary>
    private BitmapSource? CropDesktop(Int32Rect windowBounds)
    {
        var desktop = CaptureDesktop();
        var crop = new Int32Rect(
            windowBounds.X - desktop.Bounds.Left,
            windowBounds.Y - desktop.Bounds.Top,
            windowBounds.Width,
            windowBounds.Height);

        crop = Intersect(crop, new Int32Rect(0, 0, desktop.Image.PixelWidth, desktop.Image.PixelHeight));
        return crop.Width > 0 && crop.Height > 0 ? BitmapUtilities.Crop(desktop.Image, crop) : null;
    }

    /// <summary>
    /// True when the whole window is inside the virtual desktop. A window hanging over the
    /// edge cannot be cropped whole, which is the one case where a graphics capture beats
    /// reading the screen even though the window is fully visible.
    /// </summary>
    private static bool IsFullyOnScreen(Int32Rect bounds)
    {
        var desktop = SystemInformation.VirtualScreen;
        return bounds.X >= desktop.Left
            && bounds.Y >= desktop.Top
            && bounds.X + bounds.Width <= desktop.Right
            && bounds.Y + bounds.Height <= desktop.Bottom;
    }

    /// <summary>
    /// Measures how much of a window is covered, and returns the rectangles stacked above
    /// it. The z-order list is reused rather than re-enumerated so the coverage answer and
    /// the rectangles come from one snapshot of the desktop.
    /// </summary>
    private (double Visible, IReadOnlyList<Rect> Above) MeasureOcclusion(nint window, Int32Rect bounds)
    {
        var zOrder = EnumerateZOrderWindows();
        var index = -1;
        for (var position = 0; position < zOrder.Count; position++)
        {
            if (zOrder[position].Handle == window)
            {
                index = position;
                break;
            }
        }

        if (index <= 0)
        {
            // Either the window is already on top or it is absent from the list, which
            // happens when it has no title. Both cases are treated as unobstructed, which
            // preserves the screen-crop behaviour.
            return (1, []);
        }

        var above = new List<Rect>(index);
        for (var position = 0; position < index; position++)
        {
            var other = zOrder[position].Bounds;
            above.Add(new Rect(other.Left, other.Top, other.Width, other.Height));
        }

        var target = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        return (OcclusionCheck.VisibleFraction(target, above), above);
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return "未知进程";
        }
    }

    private static Int32Rect Intersect(Int32Rect left, Int32Rect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new Int32Rect(x, y, Math.Max(0, rightEdge - x), Math.Max(0, bottomEdge - y));
    }
}
