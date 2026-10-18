using System.Windows;
using System.Windows.Input;

namespace PixelPin.Utilities;

/// <summary>
/// Maps arrow-key input onto selection adjustments. Kept free of window types so the
/// exact step sizes and the edge mapping can be unit tested without a desktop.
/// </summary>
public static class SelectionKeyboard
{
    /// <summary>Single-pixel step, for placing a selection edge exactly.</summary>
    public const double FineStep = 1;

    /// <summary>Ctrl step, for covering distance quickly.</summary>
    public const double CoarseStep = 10;

    /// <summary>Step size for the current modifier state; Ctrl selects the coarse step.</summary>
    public static double StepFor(ModifierKeys modifiers)
        => modifiers.HasFlag(ModifierKeys.Control) ? CoarseStep : FineStep;

    /// <summary>
    /// Translates an arrow key into an adjustment. Without Shift the whole selection
    /// moves; with Shift the edge or corner named by the arrow moves instead. Returns
    /// false for keys this feature does not handle, so the caller can pass them on.
    /// </summary>
    public static bool TryGetAdjustment(Key key, ModifierKeys modifiers, out ResizeEdge edge, out Vector delta)
    {
        edge = ResizeEdge.None;
        delta = default;

        var horizontal = key is Key.Left or Key.Right;
        if (!horizontal && key is not (Key.Up or Key.Down))
        {
            return false;
        }

        // Left and Up move towards the origin, so they carry the negative sign.
        var step = StepFor(modifiers);
        var signed = key is Key.Left or Key.Up ? -step : step;
        delta = horizontal ? new Vector(signed, 0) : new Vector(0, signed);

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            edge = key switch
            {
                Key.Left => ResizeEdge.Left,
                Key.Right => ResizeEdge.Right,
                Key.Up => ResizeEdge.Top,
                _ => ResizeEdge.Bottom,
            };
        }

        return true;
    }

    /// <summary>
    /// Applies an arrow key to a selection. Returns the adjusted rectangle, or the
    /// original when the key is not an arrow key.
    /// </summary>
    public static Rect Apply(Rect selection, Key key, ModifierKeys modifiers, Size boundary)
    {
        if (!TryGetAdjustment(key, modifiers, out var edge, out var delta))
        {
            return selection;
        }

        return edge == ResizeEdge.None
            ? SelectionGeometry.Nudge(selection, delta, boundary)
            : SelectionGeometry.Resize(selection, edge, delta, boundary);
    }
}
