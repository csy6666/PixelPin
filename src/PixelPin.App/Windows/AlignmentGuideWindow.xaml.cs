using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PixelPin.Interop;
using PixelPin.Utilities;

namespace PixelPin.Windows;

/// <summary>
/// A click-through sheet covering the whole virtual desktop that draws the alignment
/// guides while a sticker is being dragged. It never takes focus or input: the
/// WS_EX_TRANSPARENT and WS_EX_NOACTIVATE styles are applied on creation, so showing
/// it cannot interrupt the drag that owns the mouse capture.
/// </summary>
public partial class AlignmentGuideWindow : Window
{
    private static readonly Brush GuideBrush = CreateGuideBrush();

    public AlignmentGuideWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLong(
                handle,
                NativeMethods.GwlExStyle,
                style | (int)NativeMethods.WsExTransparent | (int)NativeMethods.WsExNoActivate);
        };
    }

    /// <summary>Replaces the drawn guides. Coordinates are in virtual-desktop DIPs.</summary>
    public void ShowGuides(IReadOnlyList<AlignmentGuide> guides)
    {
        GuideCanvas.Children.Clear();

        // The window origin is the virtual screen origin, so subtracting it maps
        // virtual-desktop coordinates onto this window's own canvas. A vertical guide
        // is positioned by X and spans Y; a horizontal guide is the other way round.
        foreach (var guide in guides)
        {
            var line = new Line
            {
                Stroke = GuideBrush,
                StrokeThickness = 1,
                SnapsToDevicePixels = true,
            };

            if (guide.IsVertical)
            {
                line.X1 = guide.Position - Left;
                line.X2 = line.X1;
                line.Y1 = guide.Start - Top;
                line.Y2 = guide.End - Top;
            }
            else
            {
                line.Y1 = guide.Position - Top;
                line.Y2 = line.Y1;
                line.X1 = guide.Start - Left;
                line.X2 = guide.End - Left;
            }

            GuideCanvas.Children.Add(line);
        }
    }

    /// <summary>Number of guides currently drawn; used by the desktop probe.</summary>
    internal int GuideCount => GuideCanvas.Children.Count;

    private static Brush CreateGuideBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x8A));
        brush.Freeze();
        return brush;
    }
}
