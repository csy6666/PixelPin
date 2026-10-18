using System;
using System.Collections.Generic;

namespace PixelPin.Utilities;

/// <summary>How a window image can be obtained, in increasing order of compromise.</summary>
public enum WindowCaptureRoute
{
    /// <summary>
    /// Windows.Graphics.Capture reads the window's own composed surface. It is not affected
    /// by anything stacked on top, it returns the window's real alpha (so rounded corners
    /// stay rounded), and it works for hardware-accelerated windows that refuse to render
    /// through GDI. This is the only route that can reach a window partly off-screen.
    /// </summary>
    GraphicsCapture,

    /// <summary>
    /// PrintWindow with PW_RENDERFULLCONTENT asks the window to draw itself into a bitmap.
    /// Cheaper than a graphics capture and needs no GPU, but a window that renders through
    /// DirectComposition can silently return black.
    /// </summary>
    Render,

    /// <summary>
    /// Cropping the desktop capture. This is literally what the user is looking at, so it is
    /// the best answer for a window that is fully visible, and the worst for one that is not.
    /// </summary>
    Screen,
}

/// <summary>The facts that decide which routes can be attempted for one window.</summary>
public readonly record struct WindowCaptureConditions(
    double VisibleFraction,
    bool GraphicsCaptureSupported,
    bool FullyOnScreen,
    bool Minimized)
{
    /// <summary>Below this a screen crop shows more of other windows than of the target.</summary>
    public const double ScreenCropFloor = 0.5;

    /// <summary>At or above this the screen shows the window as the user sees it.</summary>
    public const double MostlyVisible = 0.98;
}

/// <summary>
/// An ordered list of routes to try, or a refusal that explains why none can work.
/// </summary>
public sealed record WindowCapturePlan(IReadOnlyList<WindowCaptureRoute> Routes, string? Refusal)
{
    public static WindowCapturePlan Refuse(string reason) => new(Array.Empty<WindowCaptureRoute>(), reason);

    public bool IsRefused => Routes.Count == 0;

    public override string ToString() =>
        IsRefused ? $"refused: {Refusal}" : string.Join(" -> ", Routes);
}

/// <summary>
/// Decides the order in which capture routes are attempted for a single window. Kept free
/// of any desktop call so the ordering rules can be tested without a screen.
/// </summary>
public static class WindowCapturePlanner
{
    /// <summary>
    /// Builds the route order for one window.
    ///
    /// A window the user can fully see is read from the screen first: that is exactly what
    /// they are looking at, and it costs one desktop copy. Everything else prefers a route
    /// that does not depend on what is stacked on top, because a screen crop of a covered
    /// window returns a picture of a different window.
    /// </summary>
    public static WindowCapturePlan Build(WindowCaptureConditions conditions)
    {
        if (conditions.Minimized)
        {
            return WindowCapturePlan.Refuse("窗口已最小化，Windows 不会为最小化的窗口提供画面。");
        }

        var mostlyVisible = conditions.VisibleFraction >= WindowCaptureConditions.MostlyVisible;

        // Fully visible and fully on-screen: the screen crop is both correct and cheapest.
        if (mostlyVisible && conditions.FullyOnScreen)
        {
            return new WindowCapturePlan(new[] { WindowCaptureRoute.Screen }, null);
        }

        var routes = new List<WindowCaptureRoute>(3);

        // A window that runs past the edge of the desktop cannot be cropped whole, and a
        // covered one cannot be cropped correctly, so both start with the composed surface.
        if (conditions.GraphicsCaptureSupported)
        {
            routes.Add(WindowCaptureRoute.GraphicsCapture);
        }

        routes.Add(WindowCaptureRoute.Render);

        // A screen crop is only offered when it is mostly the target window. Offering it for
        // a mostly covered window would hand back a picture of whatever is on top instead.
        if (conditions.VisibleFraction >= WindowCaptureConditions.ScreenCropFloor)
        {
            routes.Add(WindowCaptureRoute.Screen);
        }

        return new WindowCapturePlan(routes, null);
    }
}
