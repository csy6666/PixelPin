using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using PixelPin.Interop;

namespace PixelPin.Windows;

public partial class StickyWindow : Window
{
    private readonly BitmapSource _image;
    private readonly Action<BitmapSource> _copy;
    private readonly Action<BitmapSource> _save;
    private readonly Action<BitmapSource> _createSticker;
    private readonly Action<BitmapSource> _saveJpeg;
    private readonly double _aspectRatio;
    private double _rotation;
    private bool _hasShadow = true;

    public StickyWindow(
        BitmapSource image,
        double opacity,
        Action<BitmapSource> copy,
        Action<BitmapSource> save,
        Action<BitmapSource> createSticker,
        Action<BitmapSource> saveJpeg)
    {
        InitializeComponent();
        _image = image;
        _copy = copy;
        _save = save;
        _createSticker = createSticker;
        _saveJpeg = saveJpeg;
        _aspectRatio = image.PixelWidth / (double)Math.Max(1, image.PixelHeight);

        StickerImage.Source = image;
        OpacitySlider.Value = opacity;
        Opacity = opacity;

        var width = Math.Clamp(image.PixelWidth, 180, 560);
        Width = width;
        Height = Math.Max(80, width / _aspectRatio);
        Left = Math.Max(12, SystemParameters.WorkArea.Right - Width - 28);
        Top = Math.Max(12, SystemParameters.WorkArea.Bottom - Height - 60);

        Frame.Effect = new DropShadowEffect
        {
            BlurRadius = 10,
            ShadowDepth = 2,
            Opacity = 0.38,
        };
    }

    public bool IsMouseThrough { get; private set; }

    public Guid Id { get; } = Guid.NewGuid();

    public string GroupName { get; private set; } = "未分组";

    public bool IsAlwaysOnTop => Topmost;

    public string DisplayName => $"{_image.PixelWidth} x {_image.PixelHeight} 贴图";

    public string ManagerDescription => $"{GroupName} | {(IsAlwaysOnTop ? "置顶" : "普通层级")} | {(IsMouseThrough ? "鼠标穿透" : "可交互")}";

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
        if (IsButtonClick(e.OriginalSource as DependencyObject) || IsMouseThrough)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // A drag can race a close request; the window remains usable.
        }
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
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C)
        {
            _copy(_image);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)
        {
            _save(_image);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.W)
        {
            Close();
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
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Up)
        {
            OpacitySlider.Value = Math.Min(1, OpacitySlider.Value + 0.05);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Down)
        {
            OpacitySlider.Value = Math.Max(0.15, OpacitySlider.Value - 0.05);
            e.Handled = true;
        }
    }

    private void ScaleDown_Click(object sender, RoutedEventArgs e) => ScaleBy(1 / 1.15);

    private void ScaleUp_Click(object sender, RoutedEventArgs e) => ScaleBy(1.15);

    private void Rotate_Click(object sender, RoutedEventArgs e) => Rotate();

    private void Shadow_Click(object sender, RoutedEventArgs e) => ToggleShadow();

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e) => SetAlwaysOnTop(!Topmost);

    private void Copy_Click(object sender, RoutedEventArgs e) => _copy(_image);

    private void Save_Click(object sender, RoutedEventArgs e) => _save(_image);

    private void SaveJpeg_Click(object sender, RoutedEventArgs e) => _saveJpeg(_image);

    private void MouseThrough_Click(object sender, RoutedEventArgs e) => SetMouseThrough(!IsMouseThrough);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Opacity = e.NewValue;
    }

    private void ScaleBy(double factor)
    {
        var newWidth = Math.Clamp(Width * factor, 80, 2600);
        Width = newWidth;
        Height = Math.Max(40, newWidth / CurrentAspectRatio);
    }

    private void Rotate()
    {
        var centerX = Left + Width / 2;
        var centerY = Top + Height / 2;
        var previousWidth = Width;
        Width = Height;
        Height = previousWidth;
        Left = centerX - Width / 2;
        Top = centerY - Height / 2;
        _rotation = (_rotation + 90) % 360;
        StickerImage.RenderTransform = new RotateTransform(_rotation);
    }

    private void ToggleShadow()
    {
        _hasShadow = !_hasShadow;
        Frame.Effect = _hasShadow
            ? new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.38 }
            : null;
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

    private static bool IsButtonClick(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button || source is Slider || source is MenuItem)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private double CurrentAspectRatio => _rotation % 180 == 0 ? _aspectRatio : 1 / _aspectRatio;

    private void NotifyStateChanged() => StickerStateChanged?.Invoke(this, EventArgs.Empty);
}
