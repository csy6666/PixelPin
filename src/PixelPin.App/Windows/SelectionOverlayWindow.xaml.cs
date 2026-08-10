using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixelPin.Interop;
using PixelPin.Utilities;
using DrawingRectangle = System.Drawing.Rectangle;

namespace PixelPin.Windows;

public enum CaptureAction
{
    Copy,
    Save,
    Pin,
    Edit,
}

public partial class SelectionOverlayWindow : Window
{
    private const double HandleSize = 10;
    private readonly BitmapSource _screenImage;
    private readonly DrawingRectangle _screenBounds;
    private Rect _selection;
    private Rect _initialSelection;
    private Point _start;
    private Point _lastPoint;
    private CaptureDragOperation _dragOperation;
    private ResizeEdge _resizeEdge;
    private bool _finished;
    private bool _suppressCancellation;

    public SelectionOverlayWindow(BitmapSource screenImage, DrawingRectangle screenBounds)
    {
        InitializeComponent();
        _screenImage = ConvertToBgra(screenImage);
        _screenBounds = screenBounds;
        ScreenshotImage.Source = _screenImage;

        Left = screenBounds.Left;
        Top = screenBounds.Top;
        Width = screenBounds.Width;
        Height = screenBounds.Height;
    }

    public event Action<BitmapSource, CaptureAction>? Completed;

    public event Action? Cancelled;

    public void CloseSilently()
    {
        _suppressCancellation = true;
        _finished = true;
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
        Surface.Focus();
        UpdateVisuals();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_finished && !_suppressCancellation)
        {
            Cancelled?.Invoke();
        }

        base.OnClosed(e);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _start = ClampToSurface(e.GetPosition(Surface));
        _lastPoint = _start;
        _initialSelection = _selection;

        if (HasSelection && _selection.Contains(_start))
        {
            _dragOperation = CaptureDragOperation.Move;
        }
        else
        {
            _dragOperation = CaptureDragOperation.Create;
            _selection = new Rect(_start, _start);
        }

        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        var point = ClampToSurface(e.GetPosition(Surface));
        UpdateReadout(point);

        if (_dragOperation == CaptureDragOperation.Create && e.LeftButton == MouseButtonState.Pressed)
        {
            _selection = SelectionGeometry.Normalize(_start, point);
            UpdateVisuals();
        }
        else if (_dragOperation == CaptureDragOperation.Move && e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = point - _start;
            _selection = SelectionGeometry.Clamp(
                new Rect(_initialSelection.Left + delta.X, _initialSelection.Top + delta.Y, _initialSelection.Width, _initialSelection.Height),
                new Size(Surface.ActualWidth, Surface.ActualHeight),
                12);
            UpdateVisuals();
        }

        _lastPoint = point;
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

        if (_dragOperation == CaptureDragOperation.Create)
        {
            _selection = SelectionGeometry.Clamp(
                SelectionGeometry.Normalize(_start, ClampToSurface(e.GetPosition(Surface))),
                new Size(Surface.ActualWidth, Surface.ActualHeight),
                1);
        }

        _dragOperation = CaptureDragOperation.None;
        UpdateVisuals();
    }

    private void ResizeHandle_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not string value)
        {
            return;
        }

        _resizeEdge = Enum.Parse<ResizeEdge>(value);
        _initialSelection = _selection;
    }

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!HasSelection)
        {
            return;
        }

        _selection = SelectionGeometry.Resize(
            _initialSelection,
            _resizeEdge,
            new Vector(e.HorizontalChange, e.VerticalChange),
            new Size(Surface.ActualWidth, Surface.ActualHeight));
        _initialSelection = _selection;
        UpdateVisuals();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            return;
        }

        if (!HasSelection)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C)
        {
            Complete(CaptureAction.Copy);
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)
        {
            Complete(CaptureAction.Save);
            return;
        }

        if (e.Key == Key.P)
        {
            Complete(CaptureAction.Pin);
            return;
        }

        if (e.Key == Key.Enter)
        {
            Complete(CaptureAction.Copy);
        }
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
        UpdateVisuals();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Copy);

    private void Save_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Save);

    private void Pin_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Pin);

    private void Edit_Click(object sender, RoutedEventArgs e) => Complete(CaptureAction.Edit);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Complete(CaptureAction action)
    {
        if (_finished || !HasSelection)
        {
            return;
        }

        try
        {
            var pixelSelection = GetPhysicalSelection();
            var image = BitmapUtilities.Crop(_screenImage, pixelSelection);
            _finished = true;
            Completed?.Invoke(image, action);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "PixelPin capture", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var hasSelection = HasSelection;
        SelectionBorder.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        Canvas.SetLeft(SelectionBorder, _selection.Left);
        Canvas.SetTop(SelectionBorder, _selection.Top);
        SelectionBorder.Width = _selection.Width;
        SelectionBorder.Height = _selection.Height;

        Dimmer.Data = hasSelection
            ? new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight)),
                new RectangleGeometry(_selection))
            : new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight));

        SetHandle(TopLeftHandle, _selection.Left, _selection.Top, hasSelection);
        SetHandle(TopHandle, _selection.Left + _selection.Width / 2, _selection.Top, hasSelection);
        SetHandle(TopRightHandle, _selection.Right, _selection.Top, hasSelection);
        SetHandle(RightHandle, _selection.Right, _selection.Top + _selection.Height / 2, hasSelection);
        SetHandle(BottomRightHandle, _selection.Right, _selection.Bottom, hasSelection);
        SetHandle(BottomHandle, _selection.Left + _selection.Width / 2, _selection.Bottom, hasSelection);
        SetHandle(BottomLeftHandle, _selection.Left, _selection.Bottom, hasSelection);
        SetHandle(LeftHandle, _selection.Left, _selection.Top + _selection.Height / 2, hasSelection);

        if (hasSelection)
        {
            var toolbarWidth = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 350;
            var toolbarHeight = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 42;
            var left = Math.Clamp(_selection.Left, 0, Math.Max(0, Surface.ActualWidth - toolbarWidth));
            var top = _selection.Bottom + 10;
            if (top + toolbarHeight > Surface.ActualHeight)
            {
                top = Math.Max(0, _selection.Top - toolbarHeight - 10);
            }

            Canvas.SetLeft(Toolbar, left);
            Canvas.SetTop(Toolbar, top);
        }
    }

    private void UpdateReadout(Point point)
    {
        var pixels = GetPhysicalPoint(point);
        if (pixels.X < 0 || pixels.Y < 0 || pixels.X >= _screenImage.PixelWidth || pixels.Y >= _screenImage.PixelHeight)
        {
            return;
        }

        var buffer = new byte[4];
        _screenImage.CopyPixels(new Int32Rect(pixels.X, pixels.Y, 1, 1), buffer, 4, 0);
        ReadoutText.Text = $"{pixels.X,5},{pixels.Y,-5}  RGB {buffer[2]:X2}{buffer[1]:X2}{buffer[0]:X2}";

        Canvas.SetLeft(ReadoutPanel, Math.Clamp(point.X + 14, 0, Math.Max(0, Surface.ActualWidth - 180)));
        Canvas.SetTop(ReadoutPanel, Math.Clamp(point.Y + 14, 0, Math.Max(0, Surface.ActualHeight - 32)));

        var sourceRect = new Int32Rect(
            Math.Max(0, pixels.X - 6),
            Math.Max(0, pixels.Y - 6),
            Math.Min(13, _screenImage.PixelWidth - Math.Max(0, pixels.X - 6)),
            Math.Min(13, _screenImage.PixelHeight - Math.Max(0, pixels.Y - 6)));
        if (sourceRect.Width > 0 && sourceRect.Height > 0)
        {
            MagnifierImage.Source = BitmapUtilities.Crop(_screenImage, sourceRect);
            Canvas.SetLeft(MagnifierPanel, Math.Clamp(point.X + 14, 0, Math.Max(0, Surface.ActualWidth - MagnifierPanel.Width)));
            Canvas.SetTop(MagnifierPanel, Math.Clamp(point.Y + 48, 0, Math.Max(0, Surface.ActualHeight - MagnifierPanel.Height)));
        }
    }

    private Int32Rect GetPhysicalSelection()
    {
        var topLeft = GetPhysicalPoint(_selection.TopLeft);
        var bottomRight = GetPhysicalPoint(_selection.BottomRight);
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

    private bool HasSelection => _selection.Width >= 2 && _selection.Height >= 2;

    private enum CaptureDragOperation
    {
        None,
        Create,
        Move,
    }

    private readonly record struct PixelPoint(int X, int Y);
}
