using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PixelPin.Utilities;

namespace PixelPin.Windows;

/// <summary>
/// Chooses the sharing presentation for an export. The preview is produced by the same
/// renderer the export uses, so what the user approves is exactly what gets saved.
/// </summary>
public partial class BeautifyWindow : Window
{
    private readonly BitmapSource _source;
    private readonly double _scale;
    private bool _loading = true;

    /// <summary>Chosen options, or null when the user removed beautification.</summary>
    public BeautifyOptions? Result { get; private set; }

    /// <summary>True when the dialog was confirmed rather than cancelled.</summary>
    public bool Applied { get; private set; }

    public BeautifyWindow(BitmapSource source, BeautifyOptions? initial)
    {
        InitializeComponent();
        _source = source;

        // Preview at a comfortable size: large screenshots are downscaled for the
        // preview only, and the real export always runs at full resolution.
        var largest = Math.Max(source.PixelWidth, source.PixelHeight);
        _scale = largest > 900 ? 900.0 / largest : 1.0;

        PresetList.ItemsSource = BeautifyPresets.All;
        var start = initial ?? BeautifyPresets.All[0].Options;
        PresetList.SelectedIndex = FindClosestPreset(start);

        PaddingSlider.Value = Math.Clamp(start.Padding * _scale, PaddingSlider.Minimum, PaddingSlider.Maximum);
        RadiusSlider.Value = Math.Clamp(start.CornerRadius * _scale, RadiusSlider.Minimum, RadiusSlider.Maximum);
        ShadowSlider.Value = Math.Clamp(start.ShadowBlur * _scale, ShadowSlider.Minimum, ShadowSlider.Maximum);

        _loading = false;
        Refresh();
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Refresh();
    }

    private void Option_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            Refresh();
        }
    }

    private BeautifyOptions CurrentOptions()
    {
        var preset = PresetList.SelectedItem as BeautifyPreset;
        var basis = preset?.Options ?? BeautifyPresets.All[0].Options;

        // Slider values are in preview units, so they are converted back to source
        // pixels before they reach the renderer.
        var scale = _scale <= 0 ? 1 : _scale;
        return (basis with
        {
            Padding = PaddingSlider.Value / scale,
            CornerRadius = RadiusSlider.Value / scale,
            ShadowBlur = ShadowSlider.Value / scale,
        }).Normalize();
    }

    private void Refresh()
    {
        var options = CurrentOptions();
        PaddingText.Text = $"{options.Padding:0} 像素";
        RadiusText.Text = $"{options.CornerRadius:0} 像素";
        ShadowText.Text = options.ShadowBlur <= 0 ? "关闭" : $"{options.ShadowBlur:0} 像素";

        try
        {
            var previewSource = _scale < 1
                ? new TransformedBitmap(_source, new System.Windows.Media.ScaleTransform(_scale, _scale))
                : _source;

            var layout = ImageBeautifier.ComputeLayout(previewSource.PixelWidth, previewSource.PixelHeight, options);
            PreviewImage.Source = ImageBeautifier.Beautify(previewSource, options);
            SizeText.Text = $"导出尺寸：{Math.Round(layout.OutputWidth / _scale)} x {Math.Round(layout.OutputHeight / _scale)} 像素"
                + $"（原图 {_source.PixelWidth} x {_source.PixelHeight}）";
        }
        catch (Exception exception)
        {
            SizeText.Text = $"无法生成预览：{exception.Message}";
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Result = CurrentOptions();
        Applied = true;
        Close();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        Applied = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Applied = false;
        Close();
    }

    private static int FindClosestPreset(BeautifyOptions options)
    {
        for (var index = 0; index < BeautifyPresets.All.Count; index++)
        {
            var candidate = BeautifyPresets.All[index].Options;
            if (string.Equals(candidate.BackgroundStart, options.BackgroundStart, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.BackgroundEnd, options.BackgroundEnd, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }
}
