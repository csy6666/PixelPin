using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace PixelPin.Utilities;

/// <summary>
/// Appearance of a beautified export: the picture is inset on a gradient background,
/// with rounded corners and a soft shadow, the way PixPin and Snipaste present a
/// screenshot for sharing.
/// </summary>
public sealed record BeautifyOptions
{
    public const double MaximumPadding = 400;
    public const double MaximumCornerRadius = 200;
    public const double MaximumShadowBlur = 120;

    /// <summary>Space between the picture and the output edge, in source pixels.</summary>
    public double Padding { get; init; } = 56;

    public double CornerRadius { get; init; } = 14;

    public double ShadowBlur { get; init; } = 26;

    public double ShadowDepth { get; init; } = 9;

    public double ShadowOpacity { get; init; } = 0.34;

    public string BackgroundStart { get; init; } = "#F7F8FA";

    public string BackgroundEnd { get; init; } = "#E6EAF0";

    /// <summary>
    /// Clamps every value into a range the renderer can handle. Called before both
    /// measuring and drawing so the preview and the export can never disagree.
    /// </summary>
    public BeautifyOptions Normalize() => this with
    {
        Padding = Math.Clamp(Padding, 0, MaximumPadding),
        CornerRadius = Math.Clamp(CornerRadius, 0, MaximumCornerRadius),
        ShadowBlur = Math.Clamp(ShadowBlur, 0, MaximumShadowBlur),
        ShadowDepth = Math.Clamp(ShadowDepth, 0, MaximumShadowBlur),
        ShadowOpacity = Math.Clamp(ShadowOpacity, 0, 1),
    };
}

/// <summary>
/// Everything the renderer needs, including the shadow values it must actually use.
/// The blur and depth can be smaller than requested, because they have to fit inside
/// the padding; the renderer reads them back from here so the preview and the export
/// can never disagree.
/// </summary>
public readonly record struct BeautifyLayout(
    int OutputWidth,
    int OutputHeight,
    Rect ImageRect,
    double CornerRadius,
    double ShadowBlur,
    double ShadowDepth);

/// <summary>
/// Adds the sharing presentation to a capture. The geometry is computed by
/// <see cref="ComputeLayout"/>, which is pure and unit tested; the drawing step needs a
/// WPF render context and is covered by the desktop probe instead.
/// </summary>
public static class ImageBeautifier
{
    /// <summary>
    /// Output size, picture placement, and effective shadow values for a source of the
    /// given pixel size. Kept free of WPF visuals so the arithmetic is testable without
    /// a desktop.
    /// </summary>
    public static BeautifyLayout ComputeLayout(int imageWidth, int imageHeight, BeautifyOptions options)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "图片尺寸必须大于零。");
        }

        var normalized = options.Normalize();
        var padding = normalized.Padding;

        // The radius cannot exceed half of the shorter side, or the rounded rectangle
        // degenerates and WPF throws while building the geometry.
        var radius = Math.Min(normalized.CornerRadius, Math.Min(imageWidth, imageHeight) / 2.0);

        // The shadow is cast outwards from the picture, so it has to fit inside the
        // padding or it would be cut off at the output edge. The padding is the value
        // the user chose, so the shadow is what gives way.
        var blur = normalized.ShadowBlur;
        var depth = normalized.ShadowDepth;
        var requested = blur + depth;
        if (requested > padding)
        {
            var scale = requested <= 0 ? 0 : padding / requested;
            blur *= scale;
            depth *= scale;
        }

        return new BeautifyLayout(
            (int)Math.Round(imageWidth + (padding * 2)),
            (int)Math.Round(imageHeight + (padding * 2)),
            new Rect(padding, padding, imageWidth, imageHeight),
            radius,
            blur,
            depth);
    }

    /// <summary>Renders the beautified image. Must run on a thread with a WPF render context.</summary>
    public static BitmapSource Beautify(BitmapSource source, BeautifyOptions options)
    {
        var layout = ComputeLayout(source.PixelWidth, source.PixelHeight, options);
        var normalized = options.Normalize();

        var root = new Grid
        {
            Width = layout.OutputWidth,
            Height = layout.OutputHeight,
            Background = CreateBackgroundBrush(normalized),
        };

        var imageSize = new Size(source.PixelWidth, source.PixelHeight);

        // The shadow caster is a solid rounded rectangle exactly under the picture. The
        // picture is drawn on top of it, so only the blurred shadow shows around the
        // edges. Without this layer the picture would simply cover the shadow.
        if (layout.ShadowBlur > 0 || layout.ShadowDepth > 0)
        {
            root.Children.Add(new Border
            {
                Width = imageSize.Width,
                Height = imageSize.Height,
                CornerRadius = new CornerRadius(layout.CornerRadius),
                Background = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    BlurRadius = layout.ShadowBlur,
                    ShadowDepth = layout.ShadowDepth,
                    Direction = 270,
                    Opacity = normalized.ShadowOpacity,
                    Color = Colors.Black,
                    RenderingBias = RenderingBias.Quality,
                },
            });
        }

        // A Border does not clip its child to the corner radius, so the rounding is
        // applied as an explicit geometry clip on the picture host.
        root.Children.Add(new Border
        {
            Width = imageSize.Width,
            Height = imageSize.Height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Image { Source = source, Stretch = Stretch.Fill },
            Clip = new RectangleGeometry(new Rect(new Point(0, 0), imageSize), layout.CornerRadius, layout.CornerRadius),
        });

        root.Measure(new Size(layout.OutputWidth, layout.OutputHeight));
        root.Arrange(new Rect(0, 0, layout.OutputWidth, layout.OutputHeight));
        root.UpdateLayout();

        var rendered = new RenderTargetBitmap(
            layout.OutputWidth,
            layout.OutputHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        rendered.Render(root);
        rendered.Freeze();
        return rendered;
    }

    /// <summary>Parses "#RRGGBB" into a colour, falling back when the text is unusable.</summary>
    public static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return ColorConverter.ConvertFromString(value.Trim()) is Color color ? color : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static Brush CreateBackgroundBrush(BeautifyOptions options)
    {
        var start = ParseColor(options.BackgroundStart, Colors.White);
        var end = ParseColor(options.BackgroundEnd, start);

        if (start == end)
        {
            var solid = new SolidColorBrush(start);
            solid.Freeze();
            return solid;
        }

        var gradient = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();
        return gradient;
    }
}

/// <summary>A named background style offered in the editor's beautify panel.</summary>
public sealed record BeautifyPreset(string Name, BeautifyOptions Options)
{
    public override string ToString() => Name;

    /// <summary>
    /// The preset's own gradient, for the swatch in the picker. Built here rather than
    /// in XAML because binding a colour string onto a <see cref="GradientStop"/> does
    /// not survive: GradientStop is a Freezable, the template freezes the brush, and
    /// the binding is dropped, leaving every swatch blank. Binding a Brush onto
    /// Border.Background has no such problem.
    /// </summary>
    public Brush PreviewBrush
    {
        get
        {
            var start = ImageBeautifier.ParseColor(Options.BackgroundStart, Colors.White);
            var end = ImageBeautifier.ParseColor(Options.BackgroundEnd, start);
            var brush = start == end
                ? (Brush)new SolidColorBrush(start)
                : new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }
    }
}

public static class BeautifyPresets
{
    public static IReadOnlyList<BeautifyPreset> All { get; } =
    [
        new("浅灰", new BeautifyOptions
        {
            BackgroundStart = "#F7F8FA",
            BackgroundEnd = "#E6EAF0",
        }),
        new("纯白", new BeautifyOptions
        {
            BackgroundStart = "#FFFFFF",
            BackgroundEnd = "#FFFFFF",
            ShadowOpacity = 0.22,
        }),
        new("暖阳", new BeautifyOptions
        {
            BackgroundStart = "#FFD3A5",
            BackgroundEnd = "#FD6585",
            ShadowOpacity = 0.28,
        }),
        new("深海", new BeautifyOptions
        {
            BackgroundStart = "#4E65C8",
            BackgroundEnd = "#2B5876",
            ShadowOpacity = 0.38,
        }),
        new("午夜", new BeautifyOptions
        {
            BackgroundStart = "#2B3440",
            BackgroundEnd = "#12181F",
            ShadowOpacity = 0.5,
        }),
    ];

    /// <summary>Formats a preset for the settings file, so the choice survives a restart.</summary>
    public static string Format(BeautifyOptions options)
    {
        var normalized = options.Normalize();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{normalized.Padding}|{normalized.CornerRadius}|{normalized.ShadowBlur}|{normalized.ShadowDepth}|{normalized.ShadowOpacity:0.###}|{normalized.BackgroundStart}|{normalized.BackgroundEnd}");
    }

    /// <summary>Parses the text written by <see cref="Format"/>, or null when unusable.</summary>
    public static BeautifyOptions? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('|');
        if (parts.Length != 7)
        {
            return null;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var padding)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var radius)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var blur)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var depth)
            || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
        {
            return null;
        }

        return new BeautifyOptions
        {
            Padding = padding,
            CornerRadius = radius,
            ShadowBlur = blur,
            ShadowDepth = depth,
            ShadowOpacity = opacity,
            BackgroundStart = parts[5],
            BackgroundEnd = parts[6],
        }.Normalize();
    }
}
