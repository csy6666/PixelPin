using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixelPin.Interop;
using PixelPin.Models;
using PixelPin.Services;
using PixelPin.Utilities;
using DrawingRectangle = System.Drawing.Rectangle;

namespace PixelPin.Windows;

public enum CaptureAction
{
    Copy,
    Save,
    Pin,
    Edit,

    /// <summary>
    /// Hand the selected region to scrolling capture instead of producing one image. The
    /// overlay cannot produce the result itself, because the region has to be captured
    /// repeatedly while the application underneath it scrolls.
    /// </summary>
    Scroll,
}

/// <summary>Behaviour switches handed to the overlay by the settings page.</summary>
public sealed record OverlayOptions(bool SnapToWindowEdges, bool HighlightWindowOnHover)
{
    public static OverlayOptions Default { get; } = new(true, true);
}

public partial class SelectionOverlayWindow : Window
{
    private const double HandleSize = 10;
    private const int MagnifierSize = 13;
    private const double DragThreshold = 3;

    private readonly byte[] _pixelBuffer = new byte[4];
    private readonly byte[] _magnifierBuffer = new byte[MagnifierSize * MagnifierSize * 4];
    private readonly WriteableBitmap _magnifierBitmap;
    private readonly DrawingRectangle _screenBounds;
    private readonly IReadOnlyList<CaptureWindowDescriptor> _windows;
    private readonly OverlayOptions _options;
    private readonly CaptureAction? _autoComplete;
    private readonly List<Rect> _windowGuides = [];

    private BitmapSource _screenImage;
    private Rect? _selection;
    private Int32Rect? _exactPixelSelection;

    /// <summary>
    /// Set when a covered window was rendered directly instead of being cropped out of the
    /// screen. The screen image shows whatever is stacked on top, so a window the user
    /// clicked while it was covered has to come from its own render.
    /// </summary>
    private BitmapSource? _renderedWindowImage;
    private Rect _initialSelection;
    private Point _start;
    private int _hoverWindowIndex = -1;
    private CaptureDragOperation _dragOperation;
    private ResizeEdge _resizeEdge;
    private bool _dragMoved;
    private bool _finished;
    private bool _suppressCancellation;

    public SelectionOverlayWindow(
        BitmapSource screenImage,
        DrawingRectangle screenBounds,
        IReadOnlyList<CaptureWindowDescriptor>? windows = null,
        OverlayOptions? options = null,
        CaptureAction? autoComplete = null)
    {
        InitializeComponent();
        _screenImage = ConvertToBgra(screenImage);
        _screenBounds = screenBounds;
        _windows = windows ?? [];
        _options = options ?? OverlayOptions.Default;
        _autoComplete = autoComplete;
        ScreenshotImage.Source = _screenImage;
        _magnifierBitmap = new WriteableBitmap(
            MagnifierSize,
            MagnifierSize,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        MagnifierImage.Source = _magnifierBitmap;

        Left = screenBounds.Left;
        Top = screenBounds.Top;
        Width = screenBounds.Width;
        Height = screenBounds.Height;

        // The overlay has no text to compose into, so the input method has no business here.
        // Leaving it enabled lets an active Chinese IME swallow the single letter shortcuts,
        // and the key handler's ImeProcessed normalisation covers the case where the system
        // routes a key through the IME anyway.
        InputMethod.SetIsInputMethodEnabled(this, false);

        if (autoComplete == CaptureAction.Scroll)
        {
            HintText.Text = "滚动截图：拖动框选可以滚动的内容区域，松开后自动向下滚动并拼接。右键或 Esc 取消。";
        }
        else if (autoComplete is not null)
        {
            HintText.Text = "快速模式：松开鼠标立即完成。单击高亮窗口直接截取该窗口；右键或 Esc 取消。";
        }
    }

    public event Action<BitmapSource, CaptureAction>? Completed;

    /// <summary>
    /// Raised instead of <see cref="Completed"/> when the user asks for a scrolling capture.
    /// Carries the selection in virtual-desktop pixels, because the caller has to capture
    /// that same rectangle repeatedly once this overlay is out of the way.
    /// </summary>
    public event Action<Int32Rect>? ScrollRequested;

    public event Action? Cancelled;

    /// <summary>
    /// True once the overlay has laid out and applied its dimmer at least once.
    /// Until then the visual tree has no dimmer geometry, so anything that renders
    /// the overlay must wait for this.
    /// </summary>
    internal bool IsRenderReady { get; private set; }

    public void CloseSilently()
    {
        _suppressCancellation = true;
        _finished = true;
        ReleaseCaptureImage();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            _screenBounds.Left,
            _screenBounds.Top,
            _screenBounds.Width,
            _screenBounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // The overlay is placed with SWP_NOACTIVATE so it does not fight the window underneath
        // for the z-order, but that also leaves it without keyboard focus, and WPF focus alone
        // does not give it any: every shortcut - Enter to copy, Esc to cancel, the arrow keys
        // that place the selection - would go to the application behind it until the user
        // happened to click the overlay first. It is a modal surface for the duration of the
        // capture, so it takes the foreground explicitly.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ForceForegroundWindow(hwnd);
        }

        Surface.Focus();
        Keyboard.Focus(Surface);
        RefreshWindowGuides();
        UpdateVisuals();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_finished && !_suppressCancellation)
        {
            Cancelled?.Invoke();
        }

        ReleaseCaptureImage();
        base.OnClosed(e);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _start = ClampToSurface(e.GetPosition(Surface));
        _dragMoved = false;
        _initialSelection = _selection ?? Rect.Empty;

        if (_selection is { } current && current.Contains(_start))
        {
            _dragOperation = CaptureDragOperation.Move;
        }
        else
        {
            _dragOperation = CaptureDragOperation.Create;
        }

        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        var point = ClampToSurface(e.GetPosition(Surface));
        UpdateReadout(point);

        if (e.LeftButton != MouseButtonState.Pressed || _dragOperation == CaptureDragOperation.None)
        {
            UpdateHover(point);
            return;
        }

        if (!_dragMoved && (point - _start).Length < DragThreshold)
        {
            return;
        }

        _dragMoved = true;
        ClearHover();

        if (_dragOperation == CaptureDragOperation.Create)
        {
            _exactPixelSelection = null;
        _renderedWindowImage = null;
            _selection = SelectionGeometry.Normalize(_start, point);
        }
        else
        {
            var delta = point - _start;
            _selection = SelectionGeometry.Clamp(
                new Rect(
                    _initialSelection.Left + delta.X,
                    _initialSelection.Top + delta.Y,
                    _initialSelection.Width,
                    _initialSelection.Height),
                new Size(Surface.ActualWidth, Surface.ActualHeight),
                12);
            _exactPixelSelection = null;
        _renderedWindowImage = null;
        }

        UpdateVisuals();
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragOperation == CaptureDragOperation.None)
        {
            return;
        }

        if (Surface.IsMouseCaptured)
        {
            Surface.ReleaseMouseCapture();
        }

        var operation = _dragOperation;
        _dragOperation = CaptureDragOperation.None;

        if (!_dragMoved)
        {
            // A plain click either adopts the highlighted window or clears the region.
            if (operation == CaptureDragOperation.Create)
            {
                if (!TrySelectHoveredWindow())
                {
                    _selection = null;
                    _exactPixelSelection = null;
        _renderedWindowImage = null;
                }
            }

            UpdateVisuals();
            if (HasSelection && _autoComplete is { } clickAction)
            {
                CompleteOrScroll(clickAction);
            }

            return;
        }

        if (operation == CaptureDragOperation.Create)
        {
            var area = SelectionGeometry.Normalize(_start, ClampToSurface(e.GetPosition(Surface)));
            if (_options.SnapToWindowEdges)
            {
                area = SelectionSnap.Snap(area, _windowGuides);
            }

            _selection = SelectionGeometry.Clamp(
                area,
                new Size(Surface.ActualWidth, Surface.ActualHeight),
                1);
            _exactPixelSelection = null;
        _renderedWindowImage = null;
        }

        UpdateVisuals();
        if (HasSelection && _autoComplete is { } dragAction)
        {
            CompleteOrScroll(dragAction);
        }
    }

    /// <summary>
    /// The scroll action cannot be completed by producing an image, so it routes to the
    /// scrolling capture entry point instead. Every other action goes through
    /// <see cref="Complete"/>.
    /// </summary>
    private void CompleteOrScroll(CaptureAction action)
    {
        if (action == CaptureAction.Scroll)
        {
            CompleteScroll();
            return;
        }

        Complete(action);
    }

    private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Cancel();
        e.Handled = true;
    }

    private void ResizeHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not string value)
        {
            return;
        }

        _resizeEdge = Enum.Parse<ResizeEdge>(value);
        _initialSelection = _selection ?? Rect.Empty;
        _exactPixelSelection = null;
        _renderedWindowImage = null;
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_selection is not { } current)
        {
            return;
        }

        _selection = SelectionGeometry.Resize(
            _initialSelection,
            _resizeEdge,
            new Vector(e.HorizontalChange, e.VerticalChange),
            new Size(Surface.ActualWidth, Surface.ActualHeight));
        _exactPixelSelection = null;
        _renderedWindowImage = null;
        UpdateVisuals();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // With a Chinese input method active the IME consumes keys before the window sees
        // them, and WPF reports the press as Key.ImeProcessed with the real key in
        // ImeProcessedKey. Without this normalisation every letter shortcut silently does
        // nothing for anyone using an IME, while Enter and the arrow keys keep working -
        // which is exactly the kind of fault that manual testing with an English layout
        // never finds. It is done before the Escape check for the same reason: a cancelled
        // selection must always cancel, whatever the input method is doing. The overlay also
        // disables the input method outright, because it has no text to compose into.
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

        if (key == Key.Escape)
        {
            Cancel();
            return;
        }

        if (!HasSelection)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key == Key.C)
        {
            Complete(CaptureAction.Copy);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key == Key.S)
        {
            Complete(CaptureAction.Save);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key == Key.A)
        {
            SelectWholeSurface();
            return;
        }

        // Arrow keys place the selection exactly: move it, or resize the edge the
        // arrow points at when Shift is held. Ctrl widens the step to 10 pixels.
        if (ApplyKeyAdjustment(key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.P:
                Complete(CaptureAction.Pin);
                break;
            case Key.E:
                Complete(CaptureAction.Edit);
                break;
            case Key.L:
                CompleteScroll();
                break;
            case Key.Enter:
                Complete(CaptureAction.Copy);
                break;
        }
    }

    /// <summary>
    /// Applies an arrow-key adjustment to the selection. Shared by the key handler and
    /// the desktop probe so the probe drives the shipped path rather than a copy of it.
    /// Returns false when the key is not an arrow key.
    /// </summary>
    internal bool ApplyKeyAdjustment(Key key, ModifierKeys modifiers)
    {
        if (!HasSelection
            || !SelectionKeyboard.TryGetAdjustment(key, modifiers, out var edge, out var delta))
        {
            return false;
        }

        var boundary = new Size(Surface.ActualWidth, Surface.ActualHeight);
        var current = _selection!.Value;
        _selection = edge == ResizeEdge.None
            ? SelectionGeometry.Nudge(current, delta, boundary)
            : SelectionGeometry.Resize(current, edge, delta, boundary);

        // The keyboard is now authoritative, so a previously recorded exact window
        // crop no longer describes the selection.
        _exactPixelSelection = null;
        _renderedWindowImage = null;
        UpdateVisuals();
        return true;
    }

    /// <summary>The current selection in DIP, for the desktop probe.</summary>
    internal Rect? SelectionForTesting => _selection;

    /// <summary>Selection size in DIP, matching what the size badge shows.</summary>
    internal Size SelectionSizeForTesting => _selection?.Size ?? default;

    /// <summary>Sets a selection directly, for the desktop probe.</summary>
    internal void SetSelectionForTesting(Rect selection)
    {
        _selection = selection;
        _exactPixelSelection = null;
        _renderedWindowImage = null;
        UpdateVisuals();
    }

    /// <summary>
    /// Selects the window at the given index exactly as a click on it would, so the probe
    /// can verify that a covered window is rendered rather than cropped from the screen.
    /// Returns false when the selection could not be produced.
    /// </summary>
    internal bool SelectWindowForTesting(int index)
    {
        if (index < 0 || index >= _windows.Count)
        {
            return false;
        }

        _hoverWindowIndex = index;
        return TrySelectHoveredWindow();
    }

    /// <summary>
    /// The image a completed capture would produce, so the probe can inspect the pixels
    /// without finishing the overlay. Mirrors what Complete hands to its caller.
    /// </summary>
    internal BitmapSource? ResultForTesting()
    {
        if (_renderedWindowImage is not null)
        {
            return _renderedWindowImage;
        }

        if (!HasSelection)
        {
            return null;
        }

        return BitmapUtilities.Crop(_screenImage, _exactPixelSelection ?? GetPhysicalSelection());
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Keep overlays active while the user moves between physical monitors.
        if (!_finished && IsVisible)
        {
            Topmost = true;
        }
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Canvas.SetLeft(ScreenshotImage, 0);
        Canvas.SetTop(ScreenshotImage, 0);
        ScreenshotImage.Width = e.NewSize.Width;
        ScreenshotImage.Height = e.NewSize.Height;
        RefreshWindowGuides();
        UpdateVisuals();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Copy);

    private void Save_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Save);

    private void Pin_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Pin);

    private void Edit_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Edit);

    private void Scroll_Click(object sender, RoutedEventArgs e) => CompleteScroll();

    /// <summary>
    /// Hands the selection to scrolling capture. Unlike <see cref="Complete"/> this does not
    /// produce an image: the region has to be captured again and again while the application
    /// underneath it scrolls, which cannot happen while this overlay is covering it.
    /// </summary>
    private void CompleteScroll()
    {
        if (_finished || !HasSelection)
        {
            return;
        }

        var region = PhysicalSelectionOnDesktop();
        if (region.Width < ScrollCaptureService.MinimumRegionSize
            || region.Height < ScrollCaptureService.MinimumRegionSize)
        {
            MessageBox.Show(
                $"滚动截图需要至少 {ScrollCaptureService.MinimumRegionSize}×{ScrollCaptureService.MinimumRegionSize} 像素的选区，"
                + $"当前选区为 {region.Width}×{region.Height}。",
                "PixelPin 滚动截图",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _finished = true;
        ScrollRequested?.Invoke(region);
    }

    /// <summary>
    /// The selection in virtual-desktop pixels. The crop used for a normal capture is
    /// relative to this monitor's image, which is useless to a caller that has to capture
    /// the same rectangle again after the overlay is gone.
    /// </summary>
    internal Int32Rect PhysicalSelectionOnDesktop()
    {
        var local = _exactPixelSelection ?? GetPhysicalSelection();
        return new Int32Rect(
            _screenBounds.Left + local.X,
            _screenBounds.Top + local.Y,
            local.Width,
            local.Height);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectWholeSurface();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void SelectWholeSurface()
    {
        _selection = new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight);
        _exactPixelSelection = null;
        _renderedWindowImage = null;
        ClearHover();
        UpdateVisuals();
    }

    private bool TrySelectHoveredWindow()
    {
        if (_hoverWindowIndex < 0 || _hoverWindowIndex >= _windows.Count || _hoverWindowIndex >= _windowGuides.Count)
        {
            return false;
        }

        var descriptor = _windows[_hoverWindowIndex];
        _selection = _windowGuides[_hoverWindowIndex];

        // The descriptor is authoritative: crop from its physical bounds so the
        // exported image is exactly the window rectangle, with no rounding drift.
        var crop = new Int32Rect(
            descriptor.Bounds.Left - _screenBounds.Left,
            descriptor.Bounds.Top - _screenBounds.Top,
            descriptor.Bounds.Width,
            descriptor.Bounds.Height);
        crop = Intersect(crop, new Int32Rect(0, 0, _screenImage.PixelWidth, _screenImage.PixelHeight));
        _exactPixelSelection = crop.Width > 0 && crop.Height > 0 ? crop : null;

        // The overlay sits above every window, so it cannot tell from the screen image
        // alone whether the window it is highlighting was already covered. The planner
        // decides the route order: a window the user can fully see is read from the screen
        // (the crop prepared above), anything else is captured from the window's own
        // surface so the result is its real content rather than the window in front of it.
        _renderedWindowImage = null;
        var conditions = new WindowCaptureConditions(
            VisibleFractionFor(descriptor),
            WgcCaptureService.IsSupported,
            IsFullyOnScreen(descriptor),
            false);

        var plan = WindowCapturePlanner.Build(conditions);
        if (plan.Routes.Count == 1 && plan.Routes[0] == WindowCaptureRoute.Screen)
        {
            // The screen already shows this window as the user sees it. The prepared crop is
            // kept rather than baked into an image so nudging the selection re-crops it.
            if (_exactPixelSelection is null)
            {
                return false;
            }

            ClearHover();
            return true;
        }

        var result = WindowCaptureService.TryCaptureWindow(descriptor.Handle, conditions, CropScreenImage);
        if (result.Succeeded)
        {
            _renderedWindowImage = result.Image;
            _exactPixelSelection = null;
            ClearHover();
            return true;
        }

        // Every route refused. A screen crop is still better than nothing when the window is
        // mostly visible, because then it is mostly the right picture; for a mostly covered
        // window it would show a different window, so the capture is refused instead.
        if (_exactPixelSelection is not null
            && conditions.VisibleFraction >= WindowCaptureConditions.ScreenCropFloor)
        {
            ClearHover();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Crops the overlay's own desktop snapshot, so a window capture reuses the image the
    /// overlay was already built from instead of taking a second desktop copy.
    /// </summary>
    private BitmapSource? CropScreenImage(Int32Rect windowBounds)
    {
        var crop = new Int32Rect(
            windowBounds.X - _screenBounds.Left,
            windowBounds.Y - _screenBounds.Top,
            windowBounds.Width,
            windowBounds.Height);

        crop = Intersect(crop, new Int32Rect(0, 0, _screenImage.PixelWidth, _screenImage.PixelHeight));
        return crop.Width > 0 && crop.Height > 0 ? BitmapUtilities.Crop(_screenImage, crop) : null;
    }

    /// <summary>How much of the window is not covered by a window stacked above it.</summary>
    private double VisibleFractionFor(CaptureWindowDescriptor descriptor)
    {
        var index = -1;
        for (var position = 0; position < _windows.Count; position++)
        {
            if (ReferenceEquals(_windows[position], descriptor))
            {
                index = position;
                break;
            }
        }

        if (index <= 0)
        {
            // Not in the z-order list, or already the top-most window: nothing is above it.
            return 1;
        }

        var above = new List<Rect>(index);
        for (var position = 0; position < index; position++)
        {
            var other = _windows[position].Bounds;
            above.Add(new Rect(other.Left, other.Top, other.Width, other.Height));
        }

        var target = new Rect(descriptor.Bounds.Left, descriptor.Bounds.Top, descriptor.Bounds.Width, descriptor.Bounds.Height);
        return OcclusionCheck.VisibleFraction(target, above);
    }

    /// <summary>True when the whole window lies inside the virtual desktop.</summary>
    private static bool IsFullyOnScreen(CaptureWindowDescriptor descriptor)
    {
        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        return descriptor.Bounds.Left >= desktop.Left
            && descriptor.Bounds.Top >= desktop.Top
            && descriptor.Bounds.Right <= desktop.Right
            && descriptor.Bounds.Bottom <= desktop.Bottom;
    }

    private void Complete(CaptureAction action)
    {
        if (_finished || !HasSelection)
        {
            return;
        }

        try
        {
            // Shared with the probe hook so the image the probe inspects is produced by
            // exactly the same code the user's capture goes through.
            var image = ResultForTesting();
            if (image is null)
            {
                return;
            }

            _finished = true;
            Completed?.Invoke(image, action);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "PixelPin 截图", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Cancelled?.Invoke();
    }

    private void UpdateVisuals()
    {
        if (Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
        {
            return;
        }

        IsRenderReady = true;
        var hasSelection = HasSelection;
        SelectionBorder.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        HintPanel.Visibility = hasSelection || _hoverWindowIndex >= 0 ? Visibility.Collapsed : Visibility.Visible;
        SizeBadge.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        if (!hasSelection)
        {
            Dimmer.Data = new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight));
            foreach (var handle in Handles())
            {
                handle.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var selection = _selection!.Value;
        Canvas.SetLeft(SelectionBorder, selection.Left);
        Canvas.SetTop(SelectionBorder, selection.Top);
        SelectionBorder.Width = selection.Width;
        SelectionBorder.Height = selection.Height;

        Dimmer.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight)),
            new RectangleGeometry(selection));

        SetHandle(TopLeftHandle, selection.Left, selection.Top, true);
        SetHandle(TopHandle, selection.Left + selection.Width / 2, selection.Top, true);
        SetHandle(TopRightHandle, selection.Right, selection.Top, true);
        SetHandle(RightHandle, selection.Right, selection.Top + selection.Height / 2, true);
        SetHandle(BottomRightHandle, selection.Right, selection.Bottom, true);
        SetHandle(BottomHandle, selection.Left + selection.Width / 2, selection.Bottom, true);
        SetHandle(BottomLeftHandle, selection.Left, selection.Bottom, true);
        SetHandle(LeftHandle, selection.Left, selection.Top + selection.Height / 2, true);

        // Measure the bar before positioning it. Reading ActualWidth only works after
        // a layout pass, and on the very first selection the bar has never been laid
        // out, so a hardcoded fallback would place it as if it were that wide and let
        // a wider bar hang off the screen edge.
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarSize = Toolbar.DesiredSize;
        var toolbarWidth = toolbarSize.Width > 0 ? toolbarSize.Width : 430;
        var toolbarHeight = toolbarSize.Height > 0 ? toolbarSize.Height : 42;
        var toolbarLeft = Math.Clamp(selection.Left, 0, Math.Max(0, Surface.ActualWidth - toolbarWidth));
        var toolbarTop = selection.Bottom + 10;
        if (toolbarTop + toolbarHeight > Surface.ActualHeight)
        {
            toolbarTop = Math.Max(0, selection.Top - toolbarHeight - 10);
        }

        Canvas.SetLeft(Toolbar, toolbarLeft);
        Canvas.SetTop(Toolbar, toolbarTop);

        var pixels = _exactPixelSelection ?? GetPhysicalSelection();
        SizeBadgeText.Text = $"{pixels.Width} x {pixels.Height}";
        var badgeTop = selection.Top - 26;
        if (badgeTop < 0)
        {
            badgeTop = selection.Top + 6;
        }

        Canvas.SetLeft(SizeBadge, Math.Clamp(selection.Left, 0, Math.Max(0, Surface.ActualWidth - 90)));
        Canvas.SetTop(SizeBadge, badgeTop);
    }

    /// <summary>Converts z-order window bounds into this overlay's device-independent space.</summary>
    private void RefreshWindowGuides()
    {
        _windowGuides.Clear();
        if (Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var scaleX = toDevice.M11 > 0 ? toDevice.M11 : 1;
        var scaleY = toDevice.M22 > 0 ? toDevice.M22 : 1;

        foreach (var window in _windows)
        {
            _windowGuides.Add(new Rect(
                (window.Bounds.Left - _screenBounds.Left) / scaleX,
                (window.Bounds.Top - _screenBounds.Top) / scaleY,
                window.Bounds.Width / scaleX,
                window.Bounds.Height / scaleY));
        }

        if (_hoverWindowIndex >= _windowGuides.Count)
        {
            ClearHover();
        }
    }

    private void UpdateHover(Point point)
    {
        if (!_options.HighlightWindowOnHover || _windowGuides.Count == 0)
        {
            ClearHover();
            return;
        }

        var index = WindowHitTesting.TopmostIndexAt(_windowGuides, point.X, point.Y);
        if (index == _hoverWindowIndex)
        {
            return;
        }

        _hoverWindowIndex = index;
        if (index < 0)
        {
            WindowHighlight.Visibility = Visibility.Collapsed;
            WindowLabel.Visibility = Visibility.Collapsed;
            UpdateVisuals();
            return;
        }

        var bounds = _windowGuides[index];
        Canvas.SetLeft(WindowHighlight, bounds.Left);
        Canvas.SetTop(WindowHighlight, bounds.Top);
        WindowHighlight.Width = bounds.Width;
        WindowHighlight.Height = bounds.Height;
        WindowHighlight.Visibility = Visibility.Visible;

        WindowLabelText.Text = _windows[index].Title;
        Canvas.SetLeft(WindowLabel, Math.Clamp(bounds.Left, 0, Math.Max(0, Surface.ActualWidth - 200)));
        Canvas.SetTop(WindowLabel, Math.Clamp(bounds.Top - 26, 0, Math.Max(0, Surface.ActualHeight - 24)));
        WindowLabel.Visibility = Visibility.Visible;

        UpdateVisuals();
    }

    private void ClearHover()
    {
        if (_hoverWindowIndex < 0)
        {
            return;
        }

        _hoverWindowIndex = -1;
        WindowHighlight.Visibility = Visibility.Collapsed;
        WindowLabel.Visibility = Visibility.Collapsed;
    }

    private void UpdateReadout(Point point)
    {
        var pixels = GetPhysicalPoint(point);
        if (pixels.X < 0 || pixels.Y < 0 || pixels.X >= _screenImage.PixelWidth || pixels.Y >= _screenImage.PixelHeight)
        {
            return;
        }

        _screenImage.CopyPixels(new Int32Rect(pixels.X, pixels.Y, 1, 1), _pixelBuffer, 4, 0);
        ReadoutText.Text = $"{pixels.X,5},{pixels.Y,-5}  RGB {_pixelBuffer[2]:X2}{_pixelBuffer[1]:X2}{_pixelBuffer[0]:X2}";

        Canvas.SetLeft(ReadoutPanel, Math.Clamp(point.X + 14, 0, Math.Max(0, Surface.ActualWidth - 180)));
        Canvas.SetTop(ReadoutPanel, Math.Clamp(point.Y + 14, 0, Math.Max(0, Surface.ActualHeight - 32)));

        var sourceLeft = Math.Clamp(pixels.X - MagnifierSize / 2, 0, Math.Max(0, _screenImage.PixelWidth - MagnifierSize));
        var sourceTop = Math.Clamp(pixels.Y - MagnifierSize / 2, 0, Math.Max(0, _screenImage.PixelHeight - MagnifierSize));
        var sourceRect = new Int32Rect(
            sourceLeft,
            sourceTop,
            Math.Min(MagnifierSize, _screenImage.PixelWidth),
            Math.Min(MagnifierSize, _screenImage.PixelHeight));
        _screenImage.CopyPixels(sourceRect, _magnifierBuffer, MagnifierSize * 4, 0);
        _magnifierBitmap.WritePixels(
            new Int32Rect(0, 0, sourceRect.Width, sourceRect.Height),
            _magnifierBuffer,
            MagnifierSize * 4,
            0);
        Canvas.SetLeft(MagnifierPanel, Math.Clamp(point.X + 14, 0, Math.Max(0, Surface.ActualWidth - MagnifierPanel.Width)));
        Canvas.SetTop(MagnifierPanel, Math.Clamp(point.Y + 48, 0, Math.Max(0, Surface.ActualHeight - MagnifierPanel.Height)));
    }

    private Int32Rect GetPhysicalSelection()
    {
        var selection = _selection ?? new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight);
        var topLeft = GetPhysicalPoint(selection.TopLeft);
        var bottomRight = GetPhysicalPoint(selection.BottomRight);
        var x = Math.Clamp(Math.Min(topLeft.X, bottomRight.X), 0, _screenImage.PixelWidth - 1);
        var y = Math.Clamp(Math.Min(topLeft.Y, bottomRight.Y), 0, _screenImage.PixelHeight - 1);
        var right = Math.Clamp(Math.Max(topLeft.X, bottomRight.X), x + 1, _screenImage.PixelWidth);
        var bottom = Math.Clamp(Math.Max(topLeft.Y, bottomRight.Y), y + 1, _screenImage.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private PixelPoint GetPhysicalPoint(Point point)
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        return new PixelPoint(
            (int)Math.Round(point.X * transform.M11),
            (int)Math.Round(point.Y * transform.M22));
    }

    private Point ClampToSurface(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, Surface.ActualWidth),
            Math.Clamp(point.Y, 0, Surface.ActualHeight));
    }

    private IEnumerable<Thumb> Handles()
    {
        yield return TopLeftHandle;
        yield return TopHandle;
        yield return TopRightHandle;
        yield return RightHandle;
        yield return BottomRightHandle;
        yield return BottomHandle;
        yield return BottomLeftHandle;
        yield return LeftHandle;
    }

    private void SetHandle(Thumb handle, double centerX, double centerY, bool visible)
    {
        handle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(handle, centerX - HandleSize / 2);
        Canvas.SetTop(handle, centerY - HandleSize / 2);
    }

    private static BitmapSource ConvertToBgra(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static Int32Rect Intersect(Int32Rect left, Int32Rect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new Int32Rect(x, y, Math.Max(0, rightEdge - x), Math.Max(0, bottomEdge - y));
    }

    private void ReleaseCaptureImage()
    {
        ScreenshotImage.Source = null;
        MagnifierImage.Source = null;
        _screenImage = null!;
        _selection = null;
        _exactPixelSelection = null;
        _renderedWindowImage = null;
        _windowGuides.Clear();
        Completed = null;
        Cancelled = null;
    }

    private bool HasSelection => _selection is { Width: >= 2, Height: >= 2 };

    private enum CaptureDragOperation
    {
        None,
        Create,
        Move,
    }

    private readonly record struct PixelPoint(int X, int Y);
}
