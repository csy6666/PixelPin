using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PixelPin.Interop;
using PixelPin.Utilities;

namespace PixelPin.Windows;

/// <summary>Behaviour switches handed to stickers by the settings page.</summary>
public sealed record StickerOptions(
    double Opacity,
    bool KeepAspectRatio,
    bool CloseOnDoubleClick,
    bool ShowShadow = true)
{
    public static StickerOptions Default { get; } = new(1.0, true, true);
}

public partial class StickyWindow : Window
{
    private readonly BitmapSource _image;
    private readonly Action<BitmapSource> _copy;
    private readonly Action<BitmapSource> _save;
    private readonly Action<BitmapSource> _saveAs;
    private readonly Action<BitmapSource> _saveJpeg;
    private readonly Action<BitmapSource> _createSticker;
    private readonly bool _closeOnDoubleClick;
    private double _rotation;
    private bool _keepAspectRatio;
    private bool _hasShadow = true;
    private Size _resizeStart;
    private Func<StickyWindow, IReadOnlyList<Rect>>? _alignmentPeers;
    private AlignmentGuideWindow? _guideWindow;
    private bool _isDragging;
    private Point _dragGrabOffset;

    // Drag diagnostics. Only the events a user could notice are recorded, so the log stays
    // small enough to still be useful after a real session.
    private int _dragSamples;
    private string? _lastGuideSignature;
    private Rect _dragStartBounds;

    public StickyWindow(
        BitmapSource image,
        StickerOptions options,
        Action<BitmapSource> copy,
        Action<BitmapSource> save,
        Action<BitmapSource> saveAs,
        Action<BitmapSource> saveJpeg,
        Action<BitmapSource> createSticker)
    {
        InitializeComponent();
        _image = image;
        _copy = copy;
        _save = save;
        _saveAs = saveAs;
        _saveJpeg = saveJpeg;
        _createSticker = createSticker;
        _keepAspectRatio = options.KeepAspectRatio;
        _closeOnDoubleClick = options.CloseOnDoubleClick;

        StickerImage.Source = image;
        OpacitySlider.Value = Math.Clamp(options.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        Opacity = OpacitySlider.Value;
        KeepAspectMenuItem.IsChecked = _keepAspectRatio;

        var size = StickerGeometry.InitialSize(image.PixelWidth, image.PixelHeight);
        Width = size.Width;
        Height = size.Height;
        Left = Math.Max(12, SystemParameters.WorkArea.Right - Width - 28);
        Top = Math.Max(12, SystemParameters.WorkArea.Bottom - Height - 60);

        if (options.ShowShadow)
        {
            ApplyShadow();
        }
    }

    public bool IsMouseThrough { get; private set; }

    public Guid Id { get; } = Guid.NewGuid();

    public string GroupName { get; private set; } = "未分组";

    public bool IsAlwaysOnTop => Topmost;

    public bool KeepsAspectRatio => _keepAspectRatio;

    public string DisplayName => $"{_image.PixelWidth} x {_image.PixelHeight} 贴图";

    public string ManagerDescription =>
        $"{GroupName} | {(IsAlwaysOnTop ? "置顶" : "普通层级")} | {(IsMouseThrough ? "鼠标穿透" : "可交互")} | {_rotation:0}°";

    public event EventHandler? StickerStateChanged;

    public void SetGroupName(string? groupName)
    {
        GroupName = string.IsNullOrWhiteSpace(groupName)
            ? "未分组"
            : groupName.Trim()[..Math.Min(48, groupName.Trim().Length)];
        NotifyStateChanged();
    }

    public void SetAlwaysOnTop(bool enabled)
    {
        if (Topmost == enabled)
        {
            return;
        }

        Topmost = enabled;
        if (enabled)
        {
            Activate();
        }

        NotifyStateChanged();
    }

    public void BringToFront()
    {
        Topmost = true;
        Activate();
        NotifyStateChanged();
    }

    public void SetKeepAspectRatio(bool enabled)
    {
        _keepAspectRatio = enabled;
        KeepAspectMenuItem.IsChecked = enabled;
        NotifyStateChanged();
    }

    public void SetMouseThrough(bool enabled)
    {
        if (IsMouseThrough == enabled)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        var updated = enabled
            ? style | (int)NativeMethods.WsExTransparent
            : style & ~(int)NativeMethods.WsExTransparent;

        NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, updated);
        IsMouseThrough = enabled;
        MouseThroughBadge.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = enabled ? Visibility.Collapsed : Toolbar.Visibility;
        NotifyStateChanged();
    }

    /// <summary>Physical-pixel bounds, used to persist sticker placement if ever needed.</summary>
    public Rect GetScreenBounds()
    {
        return new Rect(Left, Top, Width, Height);
    }

    /// <summary>
    /// Supplies the rectangles this sticker should align against while dragging. The
    /// controller owns the sticker registry, so it answers this; the sticker itself
    /// never needs to know about the other windows.
    /// </summary>
    public void SetAlignmentSource(Func<StickyWindow, IReadOnlyList<Rect>> source)
    {
        _alignmentPeers = source;
    }

    /// <summary>True while a drag is in progress; used by the desktop probe.</summary>
    internal bool IsDragging => _isDragging;

    /// <summary>
    /// Optional per-step trace of the drag. A sticker that follows the pointer badly - lagging,
    /// stopping short, or snapping somewhere unexpected - leaves no other evidence: the window
    /// simply ends up in the wrong place. The numbers behind each step make that diagnosable
    /// from a user's report instead of only from a debugger.
    /// </summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Guides currently drawn, for the desktop probe. Empty when not dragging.</summary>
    internal IReadOnlyList<AlignmentGuide> ActiveGuides { get; private set; } = Array.Empty<AlignmentGuide>();

    /// <summary>
    /// Moves the sticker as if the pointer had dragged it to the given virtual-desktop
    /// DIP position, applying alignment exactly as a real drag would. Exists so the
    /// desktop probe can verify snapping without synthesising mouse input.
    /// </summary>
    internal void DragToForTesting(Point virtualScreenDip)
    {
        _dragGrabOffset = new Point(Width / 2, Height / 2);
        ApplyDragTo(virtualScreenDip, allowAlignment: true);
    }

    private void Frame_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!IsMouseThrough)
        {
            Toolbar.Visibility = Visibility.Visible;
        }
    }

    private void Frame_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!IsMouseThrough)
        {
            Toolbar.Visibility = Visibility.Collapsed;
        }
    }

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseThrough || IsControlClick(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            if (_closeOnDoubleClick)
            {
                Close();
            }

            return;
        }

        // Ctrl+drag hands the image to another application (browser, chat, editor)
        // instead of moving this window, mirroring the drag-out gesture in Snipaste.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            StartDragOut();
            return;
        }

        BeginDrag(e);
    }

    /// <summary>
    /// Custom drag loop instead of DragMove: alignment needs a position update on every
    /// mouse move, and DragMove runs a modal loop that reports nothing until it ends.
    /// </summary>
    private void BeginDrag(MouseButtonEventArgs e)
    {
        _dragGrabOffset = e.GetPosition(this);
        _isDragging = true;
        _dragSamples = 0;
        _lastGuideSignature = null;
        _dragStartBounds = new Rect(Left, Top, Width, Height);

        // Capture on the frame, never on the window. Mouse input goes to the capturing
        // element only, so capturing the window sent every move and the button release to the
        // window - and the handlers that follow the pointer live on the frame, so they never
        // ran. The sticker then never moved and stayed in its dragging state for good.
        Frame.CaptureMouse();

        Trace?.Invoke(
            $"拖拽开始: 抓取点 ({_dragGrabOffset.X:0.0},{_dragGrabOffset.Y:0.0}) "
            + $"窗口 ({Left:0.0},{Top:0.0}) {Width:0.0}x{Height:0.0} 捕获={Frame.IsMouseCaptured}");

        ShowGuideWindow();
        e.Handled = true;
    }

    private void Frame_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        ApplyDragTo(
            ToVirtualScreenDip(PointToScreen(e.GetPosition(this))),
            allowAlignment: !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
    }

    /// <summary>
    /// Positions the sticker so the grabbed point stays under the pointer, then applies
    /// alignment. Alt suspends alignment so the user can place a sticker freely.
    /// </summary>
    private void ApplyDragTo(Point virtualScreenDip, bool allowAlignment)
    {
        var candidate = new Rect(
            virtualScreenDip.X - _dragGrabOffset.X,
            virtualScreenDip.Y - _dragGrabOffset.Y,
            Width,
            Height);

        _dragSamples++;

        if (!allowAlignment)
        {
            var clamped = StickerAlignment.ClampToDesktop(candidate, VirtualScreenBounds);
            Left = clamped.Left;
            Top = clamped.Top;
            TraceGuides(Array.Empty<AlignmentGuide>(), candidate, clamped);
            ActiveGuides = Array.Empty<AlignmentGuide>();
            _guideWindow?.ShowGuides(ActiveGuides);
            return;
        }

        var result = StickerAlignment.Align(candidate, _alignmentPeers?.Invoke(this), VirtualScreenBounds);
        var placed = StickerAlignment.ClampToDesktop(result.Bounds, VirtualScreenBounds);
        Left = placed.Left;
        Top = placed.Top;
        TraceGuides(result.Guides, candidate, placed);
        ActiveGuides = result.Guides;
        _guideWindow?.ShowGuides(result.Guides);
    }

    /// <summary>
    /// Records a drag step only when it changed what the user can see: a guide appeared, moved
    /// or disappeared. Tracing every mouse move instead would write hundreds of lines per drag
    /// and push the useful entries out of a rotating log, which is worse than not logging.
    /// </summary>
    private void TraceGuides(
        IReadOnlyList<AlignmentGuide> guides,
        Rect candidate,
        Rect placed)
    {
        if (Trace is null)
        {
            return;
        }

        var signature = string.Join(
            "|",
            guides.Select(guide => $"{guide.IsVertical}:{guide.Position:0.0}"));

        if (string.Equals(signature, _lastGuideSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastGuideSignature = signature;

        var described = guides.Count == 0
            ? "无"
            : string.Join(
                "、",
                guides.Select(guide => guide.IsVertical
                    ? $"竖线 x={guide.Position:0.0}"
                    : $"横线 y={guide.Position:0.0}"));

        Trace(
            $"拖拽对齐: 候选 ({candidate.Left:0.0},{candidate.Top:0.0}) "
            + $"落点 ({placed.Left:0.0},{placed.Top:0.0}) 参考线: {described}");
    }

    private void Frame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void Frame_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        if (Frame.IsMouseCaptured)
        {
            Frame.ReleaseMouseCapture();
        }

        ActiveGuides = Array.Empty<AlignmentGuide>();
        _guideWindow?.ShowGuides(ActiveGuides);

        // One line per drag: where it started, where it ended and how many positions were
        // seen. A drag that never moved reports zero samples, which is the signature of input
        // that never reached the window rather than a pointer that did not move.
        Trace?.Invoke(
            $"拖拽结束: ({_dragStartBounds.Left:0.0},{_dragStartBounds.Top:0.0}) -> "
            + $"({Left:0.0},{Top:0.0}) 位移 ({Left - _dragStartBounds.Left:0.0},{Top - _dragStartBounds.Top:0.0}) "
            + $"采样 {_dragSamples} 次");
        _lastGuideSignature = null;
    }

    private void ShowGuideWindow()
    {
        try
        {
            _guideWindow ??= new AlignmentGuideWindow();
            if (!_guideWindow.IsVisible)
            {
                _guideWindow.Show();
            }
        }
        catch (Exception)
        {
            // The guides are a convenience. Losing them must never break dragging.
            _guideWindow = null;
        }
    }

    /// <summary>
    /// Converts a physical screen point into the device-independent virtual-desktop
    /// space that Window.Left and Window.Top use, so pointer tracking survives scaling.
    /// </summary>
    private Point ToVirtualScreenDip(Point physical)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(physical.X / dpi.DpiScaleX, physical.Y / dpi.DpiScaleY);
    }

    private static Rect VirtualScreenBounds => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    protected override void OnClosed(EventArgs e)
    {
        _guideWindow?.Close();
        _guideWindow = null;
        base.OnClosed(e);
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsMouseThrough)
        {
            return;
        }

        ScaleBy(e.Delta > 0 ? 1.08 : 1 / 1.08);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (control && e.Key == Key.C)
        {
            _copy(_image);
            e.Handled = true;
        }
        else if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.S)
        {
            _saveAs(_image);
            e.Handled = true;
        }
        else if (control && e.Key == Key.S)
        {
            _save(_image);
            e.Handled = true;
        }
        else if (control && e.Key == Key.W)
        {
            Close();
            e.Handled = true;
        }
        else if (control && e.Key == Key.A)
        {
            SetKeepAspectRatio(!_keepAspectRatio);
            e.Handled = true;
        }
        else if (e.Key is Key.Add or Key.OemPlus)
        {
            ScaleBy(1.1);
            e.Handled = true;
        }
        else if (e.Key is Key.Subtract or Key.OemMinus)
        {
            ScaleBy(1 / 1.1);
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            Rotate();
            e.Handled = true;
        }
        else if (alt && e.Key == Key.Up)
        {
            OpacitySlider.Value = Math.Min(1, OpacitySlider.Value + 0.05);
            e.Handled = true;
        }
        else if (alt && e.Key == Key.Down)
        {
            OpacitySlider.Value = Math.Max(0.15, OpacitySlider.Value - 0.05);
            e.Handled = true;
        }
    }

    private void ResizeHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeStart = new Size(Width, Height);
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var size = StickerGeometry.ResizeFromCorner(
            _resizeStart,
            CurrentAspectRatio,
            new Vector(e.HorizontalChange, e.VerticalChange),
            _keepAspectRatio);
        _resizeStart = size;
        Width = size.Width;
        Height = size.Height;
    }

    private void ScaleDown_Click(object sender, RoutedEventArgs e) => ScaleBy(1 / 1.15);

    private void ScaleUp_Click(object sender, RoutedEventArgs e) => ScaleBy(1.15);

    private void Rotate_Click(object sender, RoutedEventArgs e) => Rotate();

    private void KeepAspect_Click(object sender, RoutedEventArgs e) => SetKeepAspectRatio(!_keepAspectRatio);

    private void Shadow_Click(object sender, RoutedEventArgs e) => ToggleShadow();

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e) => SetAlwaysOnTop(!Topmost);

    private void Copy_Click(object sender, RoutedEventArgs e) => _copy(_image);

    private void Save_Click(object sender, RoutedEventArgs e) => _save(_image);

    private void SaveAs_Click(object sender, RoutedEventArgs e) => _saveAs(_image);

    private void SaveJpeg_Click(object sender, RoutedEventArgs e) => _saveJpeg(_image);

    private void MouseThrough_Click(object sender, RoutedEventArgs e) => SetMouseThrough(!IsMouseThrough);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity = e.NewValue;

        // The readout is the only place the exact opacity is stated; a bare slider
        // position cannot be read back to a number.
        if (OpacityReadout is not null)
        {
            OpacityReadout.Text = $"{e.NewValue * 100:0}%";
        }
    }

    private void ScaleBy(double factor)
    {
        var size = StickerGeometry.Scale(new Size(Width, Height), CurrentAspectRatio, factor);
        Width = size.Width;
        Height = size.Height;
    }

    /// <summary>True when the rendered image keeps the source proportions inside the box.</summary>
    internal bool ImageFillsBox
    {
        get
        {
            if (StickerImage.ActualWidth <= 0 || StickerImage.ActualHeight <= 0
                || _image.PixelWidth <= 0 || _image.PixelHeight <= 0)
            {
                return false;
            }

            // Stretch=Uniform plus a layout rotation: the drawn aspect ratio must equal
            // the source aspect ratio, which is what stops rotation from distorting.
            var drawn = StickerImage.ActualWidth / StickerImage.ActualHeight;
            var source = _image.PixelWidth / (double)_image.PixelHeight;
            return Math.Abs(drawn - source) < 0.02;
        }
    }

    internal void InvokeRotate() => Rotate();

    /// <summary>
    /// Rotation is a layout transform rather than a render transform, so the image
    /// keeps its true proportions instead of being stretched into the swapped box.
    /// </summary>
    private void Rotate()
    {
        var centerX = Left + (Width / 2);
        var centerY = Top + (Height / 2);
        var previous = new Size(Width, Height);

        _rotation = (_rotation + 90) % 360;
        StickerImage.LayoutTransform = new RotateTransform(_rotation);

        // The rotated image is re-measured inside the new box, so the window must be
        // told to redo layout before its dimensions are read back.
        Width = previous.Height;
        Height = previous.Width;
        Left = centerX - (Width / 2);
        Top = centerY - (Height / 2);
        StickerImage.InvalidateMeasure();
        StickerImage.UpdateLayout();
        NotifyStateChanged();
    }

    private void ToggleShadow()
    {
        _hasShadow = !_hasShadow;
        ApplyShadow();
    }

    private void ApplyShadow()
    {
        Frame.Effect = _hasShadow
            ? new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.38 }
            : null;
    }

    private void StartDragOut()
    {
        try
        {
            var data = new DataObject();
            data.SetImage(_image);
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法将图片拖到目标程序：{exception.Message}",
                "PixelPin 贴图",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Frame_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Bitmap) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Frame_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource clipboardImage)
            {
                if (!clipboardImage.IsFrozen && clipboardImage.CanFreeze)
                {
                    clipboardImage.Freeze();
                }

                _createSticker(clipboardImage);
                return;
            }

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            {
                return;
            }

            foreach (var file in files)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(file, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                _createSticker(image);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "PixelPin 贴图", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsControlClick(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button || source is Slider || source is MenuItem || source is Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private double CurrentAspectRatio =>
        StickerGeometry.RotatedAspectRatio(_image.PixelWidth / (double)Math.Max(1, _image.PixelHeight), _rotation);

    private void NotifyStateChanged() => StickerStateChanged?.Invoke(this, EventArgs.Empty);
}
