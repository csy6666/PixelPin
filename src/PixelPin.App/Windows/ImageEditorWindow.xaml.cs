using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixelPin.Services;
using PixelPin.Utilities;
using ShapePath = System.Windows.Shapes.Path;

namespace PixelPin.Windows;

/// <summary>Annotation defaults handed to the editor by the settings page.</summary>
public sealed record EditorOptions(string Color, double StrokeWidth, double FontSize = 20)
{
    public static EditorOptions Default { get; } = new("#E53935", 3);
}

public partial class ImageEditorWindow : Window
{
    private const int MaximumUndoSteps = 40;
    private const double MinimumZoom = 0.1;
    private const double MaximumZoom = 8;

    private readonly Action<BitmapSource> _copy;
    private readonly Action<BitmapSource> _save;
    private readonly Action<BitmapSource> _saveAs;
    private readonly Action<BitmapSource> _pin;
    private readonly Action<BitmapSource> _persist;
    private readonly List<IEditorCommand> _undo = [];
    private readonly List<IEditorCommand> _redo = [];

    private BitmapSource? _image;
    private EditorTool _tool = EditorTool.Rectangle;
    private Brush _stroke;
    private double _fontSize = 20;
    private double _zoom = 1;
    private int _sequenceNumber = 1;
    private Point _start;
    private UIElement? _draft;
    private bool _drawing;
    private BeautifyOptions? _beautify;

    public ImageEditorWindow(
        BitmapSource image,
        Action<BitmapSource> copy,
        Action<BitmapSource> save,
        Action<BitmapSource> saveAs,
        Action<BitmapSource> pin,
        Action<BitmapSource> persist,
        EditorOptions? options = null)
    {
        InitializeComponent();
        _image = image;
        _copy = copy;
        _save = save;
        _saveAs = saveAs;
        _pin = pin;
        _persist = persist;

        var defaults = options ?? EditorOptions.Default;
        _fontSize = Math.Clamp(defaults.FontSize, 8, 200);
        _stroke = CreateFrozenBrush(defaults.Color);
        StrokeWidthSlider.Value = Math.Clamp(defaults.StrokeWidth, StrokeWidthSlider.Minimum, StrokeWidthSlider.Maximum);
        StrokeWidthSlider.ValueChanged += (_, _) => UpdateStrokeWidthText();
        ColorPreview.Fill = _stroke;
        SelectFontSize(_fontSize);
        UpdateStrokeWidthText();

        Closed += (_, _) =>
        {
            _undo.Clear();
            _redo.Clear();
            DrawingCanvas.Children.Clear();
            _draft = null;
            BaseImage.Source = null;
            _image = null;
        };

        BaseImage.Source = image;
        EditorSurface.Width = image.PixelWidth;
        EditorSurface.Height = image.PixelHeight;
        DrawingCanvas.Width = image.PixelWidth;
        DrawingCanvas.Height = image.PixelHeight;
        StatusText.Text = "选择标注工具后在图片上拖动。Ctrl+Z 撤销，Ctrl+Y 重做。";
        UpdateToolSelection();

        Loaded += (_, _) => FitToWindow();
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        // ToggleButton, not Button: the toolbar has to show which tool is armed.
        if (sender is System.Windows.Controls.Primitives.ToggleButton { Tag: string name })
        {
            SelectTool(name);
        }
    }

    /// <summary>Activates a tool by its <see cref="EditorTool"/> name.</summary>
    internal bool SelectTool(string name)
    {
        if (!Enum.TryParse<EditorTool>(name, out var tool))
        {
            return false;
        }

        FinishTextDraft();
        _tool = tool;
        UpdateToolSelection();
        StatusText.Text = $"已选择{ToolName(tool)}工具。";
        return true;
    }

    /// <summary>
    /// Mirrors the active tool onto the toolbar. SelectTool is also the entry point
    /// used by tests and shortcuts, so the visual state cannot live in the click
    /// handler alone.
    /// </summary>
    private void UpdateToolSelection()
    {
        foreach (var toggle in FindToolToggles(ToolGroups))
        {
            toggle.IsChecked = toggle.Tag is string name
                && Enum.TryParse<EditorTool>(name, out var tool)
                && tool == _tool;
        }
    }

    /// <summary>
    /// Walks the panel's own children rather than the visual tree: SelectTool runs
    /// from the constructor, before any template has been applied, and a visual walk
    /// would find nothing and leave every toggle unchecked.
    /// </summary>
    private static IEnumerable<System.Windows.Controls.Primitives.ToggleButton> FindToolToggles(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is System.Windows.Controls.Primitives.ToggleButton { Tag: string } toggle)
            {
                yield return toggle;
            }
            else if (child is DependencyObject node)
            {
                foreach (var nested in FindToolToggles(node))
                {
                    yield return nested;
                }
            }
        }
    }

    private void ColorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // This fires while InitializeComponent is still running, so read the event
        // source rather than the field, which may not be assigned yet.
        if (sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string hex } })
        {
            return;
        }

        ApplyStroke(CreateFrozenBrush(hex));
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(
                ((SolidColorBrush)_stroke).Color.R,
                ((SolidColorBrush)_stroke).Color.G,
                ((SolidColorBrush)_stroke).Color.B),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var color = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        ColorPicker.SelectedIndex = -1;
        ApplyStroke(new SolidColorBrush(color));
    }

    private void FontSizePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Same InitializeComponent ordering caveat as the colour picker.
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Content: string text } }
            && double.TryParse(text, out var size))
        {
            _fontSize = Math.Clamp(size, 8, 200);
        }
    }

    private void DrawingCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool == EditorTool.Select)
        {
            return;
        }

        BeginDraw(Clamp(e.GetPosition(DrawingCanvas)));
        e.Handled = true;
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _draft is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateDraw(Clamp(e.GetPosition(DrawingCanvas)));
    }

    private void DrawingCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing)
        {
            return;
        }

        EndDraw(Clamp(e.GetPosition(DrawingCanvas)));
    }

    /// <summary>Starts an annotation gesture at <paramref name="point"/> on the drawing canvas.</summary>
    internal void BeginDraw(Point point)
    {
        FinishTextDraft();
        _start = point;

        if (_tool == EditorTool.Text)
        {
            CreateTextDraft(point);
            return;
        }

        if (_tool == EditorTool.Sequence)
        {
            CommitSequenceMarker(point);
            return;
        }

        _drawing = true;
        DrawingCanvas.CaptureMouse();
        _draft = CreateDraft(_tool, point);
        if (_draft is not null)
        {
            DrawingCanvas.Children.Add(_draft);
        }
    }

    /// <summary>Extends the active gesture to <paramref name="point"/>.</summary>
    internal void UpdateDraw(Point point)
    {
        if (!_drawing || _draft is null)
        {
            return;
        }

        UpdateDraft(_draft, _tool, _start, point);
    }

    /// <summary>Finishes the active gesture at <paramref name="point"/> and commits it to the undo stack.</summary>
    internal void EndDraw(Point point)
    {
        if (!_drawing)
        {
            return;
        }

        if (DrawingCanvas.IsMouseCaptured)
        {
            DrawingCanvas.ReleaseMouseCapture();
        }

        _drawing = false;
        if (_draft is null)
        {
            return;
        }

        var visual = _draft;
        var tool = _tool;
        _draft = null;
        UpdateDraft(visual, tool, _start, point);

        if (tool is EditorTool.Mosaic or EditorTool.Blur)
        {
            DrawingCanvas.Children.Remove(visual);
            CommitRedaction(tool, SelectionGeometry.Normalize(_start, point));
            return;
        }

        CommitVisual(visual);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    internal int AnnotationCount => DrawingCanvas.Children.Count;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        FinishTextDraft();
        if (DrawingCanvas.Children.Count == 0)
        {
            return;
        }

        Execute(new ClearVisualsCommand(DrawingCanvas.Children.Cast<UIElement>().ToList()));
        _sequenceNumber = 1;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => _copy(RenderImage());

    private void Save_Click(object sender, RoutedEventArgs e) => _save(RenderImage());

    private void SaveAs_Click(object sender, RoutedEventArgs e) => _saveAs(RenderImage());

    private void Pin_Click(object sender, RoutedEventArgs e) => _pin(RenderImage());

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        // The reason is reported verbatim rather than replaced with a generic message, so
        // the user learns which language component is missing.
        var reason = OcrService.UnavailableReason;
        if (reason is not null)
        {
            MessageBox.Show(reason, "PixelPin 文字识别", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new OcrWindow(RenderImage(), CopyText) { Owner = this };
        window.Show();
    }

    /// <summary>Places recognised text on the clipboard as text, not as an image.</summary>
    private static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法复制文字：{exception.Message}",
                "PixelPin 文字识别",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Beautify_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BeautifyWindow(RenderImage(), _beautify)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || !dialog.Applied)
        {
            return;
        }

        SetBeautify(dialog.Result);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _persist(RenderImage());
        Close();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void ZoomActual_Click(object sender, RoutedEventArgs e) => ApplyZoom(1);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ApplyZoom(_zoom * 1.25);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ApplyZoom(_zoom / 1.25);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (control && shift && e.Key == Key.S)
        {
            _saveAs(RenderImage());
            e.Handled = true;
        }
        else if (control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (control && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (control && e.Key == Key.C)
        {
            _copy(RenderImage());
            e.Handled = true;
        }
        else if (control && e.Key == Key.S)
        {
            _save(RenderImage());
            e.Handled = true;
        }
        else if (control && e.Key == Key.P)
        {
            _pin(RenderImage());
            e.Handled = true;
        }
        else if (control && e.Key == Key.B)
        {
            Beautify_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && shift && e.Key == Key.O)
        {
            Ocr_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (control && e.Key is Key.OemPlus or Key.Add)
        {
            ApplyZoom(_zoom * 1.25);
            e.Handled = true;
        }
        else if (control && e.Key is Key.OemMinus or Key.Subtract)
        {
            ApplyZoom(_zoom / 1.25);
            e.Handled = true;
        }
        else if (control && e.Key == Key.D0)
        {
            ApplyZoom(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelTextDraft();
            e.Handled = true;
        }
    }

    private UIElement? CreateDraft(EditorTool tool, Point start)
    {
        return tool switch
        {
            EditorTool.Rectangle => new Rectangle
            {
                Stroke = _stroke,
                StrokeThickness = StrokeWidthSlider.Value,
                Fill = Brushes.Transparent,
            },
            EditorTool.Ellipse => new Ellipse
            {
                Stroke = _stroke,
                StrokeThickness = StrokeWidthSlider.Value,
                Fill = Brushes.Transparent,
            },
            EditorTool.Highlight => new Rectangle
            {
                Fill = CreateHighlightBrush(),
                Stroke = null,
            },
            EditorTool.Line or EditorTool.Arrow => new ShapePath
            {
                Stroke = _stroke,
                StrokeThickness = StrokeWidthSlider.Value,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            },
            EditorTool.Pen => new Polyline
            {
                Stroke = _stroke,
                StrokeThickness = StrokeWidthSlider.Value,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = new PointCollection { start },
            },
            EditorTool.Mosaic or EditorTool.Blur => new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(48, 0, 0, 0)),
            },
            _ => null,
        };
    }

    private void UpdateDraft(UIElement visual, EditorTool tool, Point start, Point end)
    {
        var area = SelectionGeometry.Normalize(start, end);
        switch (tool)
        {
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            case EditorTool.Highlight:
            case EditorTool.Mosaic:
            case EditorTool.Blur:
                if (visual is FrameworkElement element)
                {
                    Canvas.SetLeft(element, area.Left);
                    Canvas.SetTop(element, area.Top);
                    element.Width = area.Width;
                    element.Height = area.Height;
                }

                break;

            case EditorTool.Line:
            case EditorTool.Arrow:
                if (visual is ShapePath path)
                {
                    path.Data = BuildLineGeometry(start, end, tool == EditorTool.Arrow);
                }

                break;

            case EditorTool.Pen:
                if (visual is Polyline line)
                {
                    line.Points.Add(end);
                }

                break;
        }
    }

    /// <summary>Redaction regions already committed, for diagnostics and tests.</summary>
    internal int RedactionCount { get; private set; }

    /// <summary>
    /// Mosaic and blur are baked into a bitmap overlay instead of a live XAML effect,
    /// so the exported PNG matches the preview pixel for pixel.
    /// </summary>
    private void CommitRedaction(EditorTool tool, Rect area)
    {
        var source = _image;
        if (source is null || area.Width < 2 || area.Height < 2)
        {
            return;
        }

        var region = ToPixelRect(area);
        if (region.Width < 1 || region.Height < 1)
        {
            return;
        }

        try
        {
            var processed = tool == EditorTool.Mosaic
                ? ImageProcessing.Pixelate(source, region, Math.Max(ImageProcessing.MinimumBlockSize, region.Width / 14))
                : ImageProcessing.BoxBlur(source, region, Math.Max(ImageProcessing.MinimumBlurRadius, region.Width / 60));

            var visual = new Image
            {
                Source = processed,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapScalingMode(
                visual,
                tool == EditorTool.Mosaic ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            Canvas.SetLeft(visual, area.Left);
            Canvas.SetTop(visual, area.Top);
            visual.Width = area.Width;
            visual.Height = area.Height;
            RedactionCount++;
            CommitVisual(visual);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"无法处理该区域：{exception.Message}";
        }
    }

    private void CommitSequenceMarker(Point point)
    {
        var diameter = Math.Max(18, _fontSize * 1.5);
        var grid = new Grid
        {
            Width = diameter,
            Height = diameter,
            IsHitTestVisible = false,
        };
        grid.Children.Add(new Ellipse { Fill = _stroke });
        grid.Children.Add(new TextBlock
        {
            Text = _sequenceNumber.ToString(),
            Foreground = Brushes.White,
            FontSize = Math.Max(10, diameter * 0.5),
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Canvas.SetLeft(grid, point.X - diameter / 2);
        Canvas.SetTop(grid, point.Y - diameter / 2);
        _sequenceNumber++;
        CommitVisual(grid);
    }

    private void CreateTextDraft(Point point)
    {
        var box = new TextBox
        {
            Width = 280,
            MinHeight = 30,
            FontSize = _fontSize,
            Foreground = _stroke,
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = _stroke,
            BorderThickness = new Thickness(1),
            AcceptsReturn = false,
        };
        Canvas.SetLeft(box, point.X);
        Canvas.SetTop(box, point.Y);
        DrawingCanvas.Children.Add(box);
        _draft = box;
        box.KeyDown += TextBox_KeyDown;
        box.LostKeyboardFocus += TextBox_LostKeyboardFocus;
        box.Focus();
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FinishTextDraft();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelTextDraft();
            e.Handled = true;
        }
    }

    private void TextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FinishTextDraft();
    }

    private void FinishTextDraft()
    {
        if (_draft is not TextBox box)
        {
            return;
        }

        _draft = null;
        DrawingCanvas.Children.Remove(box);
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            return;
        }

        var text = new TextBlock
        {
            Text = box.Text,
            FontSize = box.FontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = box.Foreground,
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            Width = box.Width,
        };
        Canvas.SetLeft(text, Canvas.GetLeft(box));
        Canvas.SetTop(text, Canvas.GetTop(box));
        DrawingCanvas.Children.Add(text);
        CommitVisual(text);
    }

    private void CancelTextDraft()
    {
        if (_draft is TextBox box)
        {
            DrawingCanvas.Children.Remove(box);
            _draft = null;
        }
    }

    internal BitmapSource RenderImage()
    {
        FinishTextDraft();
        EditorSurface.Measure(new Size(EditorSurface.Width, EditorSurface.Height));
        EditorSurface.Arrange(new Rect(0, 0, EditorSurface.Width, EditorSurface.Height));
        EditorSurface.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(EditorSurface.Width)),
            Math.Max(1, (int)Math.Ceiling(EditorSurface.Height)),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(EditorSurface);
        bitmap.Freeze();

        // Beautification is applied to the flattened result, so every export path
        // (copy, save, save-as, pin, done) produces the same presented image.
        return _beautify is null ? bitmap : ImageBeautifier.Beautify(bitmap, _beautify);
    }

    /// <summary>Applies or clears the sharing presentation. Null restores the raw export.</summary>
    internal void SetBeautify(BeautifyOptions? options)
    {
        _beautify = options?.Normalize();
        BeautifyButton.Content = _beautify is null ? "美化" : "美化 ✓";
        // The toolbar style keys off Tag, so the active state survives a theme change
        // without a second copy of the colours in code.
        BeautifyButton.Tag = _beautify is null ? null : "active";
        StatusText.Text = _beautify is null
            ? "已取消美化，导出为原始尺寸。"
            : $"已启用美化，导出尺寸为原图加 {_beautify.Padding:0} 像素边距。";
    }

    /// <summary>True while a sharing presentation is applied; used by the desktop probe.</summary>
    internal bool HasBeautify => _beautify is not null;

    private void CommitVisual(UIElement visual)
    {
        Execute(new AddVisualCommand(visual));
    }

    private void Execute(IEditorCommand command)
    {
        command.Do(DrawingCanvas);
        _undo.Add(command);
        if (_undo.Count > MaximumUndoSteps)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        StatusText.Text = $"已添加{ToolName(_tool)}。";
    }

    internal void Undo()    {
        FinishTextDraft();
        if (_undo.Count == 0)
        {
            return;
        }

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo(DrawingCanvas);
        _redo.Add(command);
        if (_redo.Count > MaximumUndoSteps)
        {
            _redo.RemoveAt(0);
        }

        StatusText.Text = "已撤销上一次标注。";
    }

    internal void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Do(DrawingCanvas);
        _undo.Add(command);
        if (_undo.Count > MaximumUndoSteps)
        {
            _undo.RemoveAt(0);
        }

        StatusText.Text = "已重做标注。";
    }

    private void ApplyStroke(Brush brush)
    {
        _stroke = brush;
        if (ColorPreview is not null)
        {
            ColorPreview.Fill = brush;
        }

        // The colour ComboBox raises SelectionChanged while InitializeComponent is
        // still running, before the drawing surface exists. Only the field matters then.
        if (DrawingCanvas is null)
        {
            return;
        }

        // Apply to the existing annotations so the preview swatch and the drawn
        // annotation can never disagree.
        foreach (var child in DrawingCanvas.Children)
        {
            switch (child)
            {
                case Shape { Stroke: not null } shape:
                    shape.Stroke = brush;
                    break;
                case TextBlock text:
                    text.Foreground = brush;
                    break;
            }
        }
    }

    private void SelectFontSize(double size)
    {
        foreach (var item in FontSizePicker.Items.OfType<ComboBoxItem>())
        {
            if (item.Content is string text && double.TryParse(text, out var value)
                && Math.Abs(value - size) < 0.5)
            {
                FontSizePicker.SelectedItem = item;
                return;
            }
        }

        FontSizePicker.SelectedIndex = 2;
        _fontSize = 20;
    }

    private void UpdateStrokeWidthText()
    {
        if (StrokeWidthText is not null)
        {
            StrokeWidthText.Text = ((int)StrokeWidthSlider.Value).ToString();
        }
    }

    private void FitToWindow()
    {
        if (EditorSurface.Width <= 0 || EditorSurface.Height <= 0)
        {
            return;
        }

        var viewportWidth = SurfaceScroller.ViewportWidth > 0 ? SurfaceScroller.ViewportWidth : SurfaceScroller.ActualWidth;
        var viewportHeight = SurfaceScroller.ViewportHeight > 0 ? SurfaceScroller.ViewportHeight : SurfaceScroller.ActualHeight;
        if (viewportWidth <= 8 || viewportHeight <= 8)
        {
            return;
        }

        var scale = Math.Min(
            (viewportWidth - 24) / EditorSurface.Width,
            (viewportHeight - 24) / EditorSurface.Height);
        ApplyZoom(scale > 1 ? 1 : scale);
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomText.Text = $"{_zoom * 100:0}%";
    }

    private Brush CreateHighlightBrush()
    {
        var color = _stroke is SolidColorBrush solid ? solid.Color : Colors.Gold;
        var brush = new SolidColorBrush(Color.FromArgb(96, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        SolidColorBrush brush;
        try
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch (FormatException)
        {
            brush = new SolidColorBrush(Colors.Red);
        }
        catch (NotSupportedException)
        {
            brush = new SolidColorBrush(Colors.Red);
        }

        brush.Freeze();
        return brush;
    }

    private static string ToolName(EditorTool tool) => tool switch
    {
        EditorTool.Select => "选择",
        EditorTool.Rectangle => "矩形",
        EditorTool.Ellipse => "椭圆",
        EditorTool.Line => "直线",
        EditorTool.Arrow => "箭头",
        EditorTool.Pen => "画笔",
        EditorTool.Highlight => "高亮",
        EditorTool.Mosaic => "马赛克",
        EditorTool.Blur => "模糊",
        EditorTool.Sequence => "序号",
        EditorTool.Text => "文字",
        _ => "标注",
    };

    private static Geometry BuildLineGeometry(Point start, Point end, bool arrow)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(start, false, false);
        context.LineTo(end, true, false);

        if (arrow)
        {
            var direction = start - end;
            if (direction.Length > 0.1)
            {
                direction.Normalize();
                var left = end + Rotate(direction, 28) * 16;
                var right = end + Rotate(direction, -28) * 16;
                context.BeginFigure(left, false, false);
                context.LineTo(end, true, false);
                context.LineTo(right, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Vector Rotate(Vector value, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Vector(
            value.X * Math.Cos(radians) - value.Y * Math.Sin(radians),
            value.X * Math.Sin(radians) + value.Y * Math.Cos(radians));
    }

    private Int32Rect ToPixelRect(Rect area)
    {
        var image = _image ?? throw new InvalidOperationException("标注图像已关闭。");
        var x = Math.Clamp((int)Math.Floor(area.Left), 0, image.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(area.Top), 0, image.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(area.Right), x + 1, image.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(area.Bottom), y + 1, image.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private Point Clamp(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, DrawingCanvas.ActualWidth),
            Math.Clamp(point.Y, 0, DrawingCanvas.ActualHeight));
    }

    private interface IEditorCommand
    {
        void Do(Canvas canvas);

        void Undo(Canvas canvas);
    }

    private sealed class AddVisualCommand : IEditorCommand
    {
        private readonly UIElement _visual;

        public AddVisualCommand(UIElement visual)
        {
            _visual = visual;
        }

        public void Do(Canvas canvas)
        {
            if (!canvas.Children.Contains(_visual))
            {
                canvas.Children.Add(_visual);
            }
        }

        public void Undo(Canvas canvas)
        {
            canvas.Children.Remove(_visual);
        }
    }

    private sealed class ClearVisualsCommand : IEditorCommand
    {
        private readonly IReadOnlyList<UIElement> _visuals;

        public ClearVisualsCommand(IReadOnlyList<UIElement> visuals)
        {
            _visuals = visuals;
        }

        public void Do(Canvas canvas)
        {
            canvas.Children.Clear();
        }

        public void Undo(Canvas canvas)
        {
            foreach (var visual in _visuals)
            {
                if (!canvas.Children.Contains(visual))
                {
                    canvas.Children.Add(visual);
                }
            }
        }
    }

    private enum EditorTool
    {
        Select,
        Rectangle,
        Ellipse,
        Line,
        Arrow,
        Pen,
        Highlight,
        Mosaic,
        Blur,
        Sequence,
        Text,
    }
}
