using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PixelPin.Utilities;
using ShapePath = System.Windows.Shapes.Path;

namespace PixelPin.Windows;

public partial class ImageEditorWindow : Window
{
    private readonly BitmapSource _image;
    private readonly Action<BitmapSource> _copy;
    private readonly Action<BitmapSource> _save;
    private readonly Action<BitmapSource> _pin;
    private readonly Action<BitmapSource> _persist;
    private readonly List<IEditorCommand> _undo = [];
    private readonly List<IEditorCommand> _redo = [];

    private EditorTool _tool = EditorTool.Rectangle;
    private Brush _stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private Point _start;
    private UIElement? _draft;
    private bool _drawing;

    public ImageEditorWindow(
        BitmapSource image,
        Action<BitmapSource> copy,
        Action<BitmapSource> save,
        Action<BitmapSource> pin,
        Action<BitmapSource> persist)
    {
        InitializeComponent();
        _image = image;
        _copy = copy;
        _save = save;
        _pin = pin;
        _persist = persist;

        BaseImage.Source = image;
        EditorSurface.Width = image.PixelWidth;
        EditorSurface.Height = image.PixelHeight;
        DrawingCanvas.Width = image.PixelWidth;
        DrawingCanvas.Height = image.PixelHeight;
        StatusText.Text = "Choose an annotation tool and drag over the image. Ctrl+Z and Ctrl+Y undo and redo.";
    }

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<EditorTool>(name, out var tool))
        {
            FinishTextDraft();
            _tool = tool;
            StatusText.Text = $"{name} tool selected.";
        }
    }

    private void ColorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorPicker.SelectedItem is not ComboBoxItem { Tag: string hex })
        {
            return;
        }

        _stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        _stroke.Freeze();
    }

    private void DrawingCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool == EditorTool.Select)
        {
            return;
        }

        FinishTextDraft();
        _start = Clamp(e.GetPosition(DrawingCanvas));
        if (_tool == EditorTool.Text)
        {
            CreateTextDraft(_start);
            e.Handled = true;
            return;
        }

        _drawing = true;
        DrawingCanvas.CaptureMouse();
        _draft = CreateDraft(_tool, _start);
        if (_draft is not null)
        {
            DrawingCanvas.Children.Add(_draft);
        }
        e.Handled = true;
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _draft is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateDraft(_draft, _tool, _start, Clamp(e.GetPosition(DrawingCanvas)));
    }

    private void DrawingCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
        if (_draft is not null)
        {
            UpdateDraft(_draft, _tool, _start, Clamp(e.GetPosition(DrawingCanvas)));
            CommitVisual(_draft);
            _draft = null;
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        FinishTextDraft();
        if (DrawingCanvas.Children.Count == 0)
        {
            return;
        }

        Execute(new ClearVisualsCommand(DrawingCanvas.Children.Cast<UIElement>().ToList()));
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => _copy(RenderImage());

    private void Save_Click(object sender, RoutedEventArgs e) => _save(RenderImage());

    private void Pin_Click(object sender, RoutedEventArgs e) => _pin(RenderImage());

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "OCR is intentionally unavailable until a Windows OCR language component is configured. The editor has not produced a guessed or partial result.",
            "PixelPin OCR",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _persist(RenderImage());
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C)
        {
            _copy(RenderImage());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)
        {
            _save(RenderImage());
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
            EditorTool.Mosaic => CreateMosaicDraft(start),
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

            case EditorTool.Mosaic:
                if (visual is Image mosaic)
                {
                    UpdateMosaicDraft(mosaic, area);
                }
                break;
        }
    }

    private void CreateTextDraft(Point point)
    {
        var box = new TextBox
        {
            Width = 260,
            MinHeight = 28,
            FontSize = 18,
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

    private Image CreateMosaicDraft(Point point)
    {
        var image = new Image
        {
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, point.X);
        Canvas.SetTop(image, point.Y);
        return image;
    }

    private void UpdateMosaicDraft(Image visual, Rect area)
    {
        if (area.Width < 2 || area.Height < 2)
        {
            return;
        }

        var pixels = ToPixelRect(area);
        if (pixels.Width < 1 || pixels.Height < 1)
        {
            return;
        }

        var crop = new CroppedBitmap(_image, pixels);
        var targetWidth = Math.Max(1, pixels.Width / 12);
        var targetHeight = Math.Max(1, pixels.Height / 12);
        var reduced = new TransformedBitmap(crop, new ScaleTransform(
            (double)targetWidth / pixels.Width,
            (double)targetHeight / pixels.Height));
        reduced.Freeze();

        visual.Source = reduced;
        Canvas.SetLeft(visual, area.Left);
        Canvas.SetTop(visual, area.Top);
        visual.Width = area.Width;
        visual.Height = area.Height;
    }

    private BitmapSource RenderImage()
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
        return bitmap;
    }

    private void CommitVisual(UIElement visual)
    {
        Execute(new AddVisualCommand(visual));
    }

    private void Execute(IEditorCommand command)
    {
        command.Do(DrawingCanvas);
        _undo.Add(command);
        _redo.Clear();
        StatusText.Text = $"{_tool} annotation added.";
    }

    private void Undo()
    {
        FinishTextDraft();
        if (_undo.Count == 0)
        {
            return;
        }

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo(DrawingCanvas);
        _redo.Add(command);
        StatusText.Text = "Undid last annotation.";
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Do(DrawingCanvas);
        _undo.Add(command);
        StatusText.Text = "Redid annotation.";
    }

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
        var x = Math.Clamp((int)Math.Floor(area.Left), 0, _image.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(area.Top), 0, _image.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(area.Right), x + 1, _image.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(area.Bottom), y + 1, _image.PixelHeight);
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
        Mosaic,
        Text,
    }
}
