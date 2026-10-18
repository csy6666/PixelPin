using System;
using System.Collections.Generic;

namespace PixelPin.Utilities;

/// <summary>
/// Why two consecutive frames cannot be related by a scroll. Every value other than
/// <see cref="None"/> means the stitched image would be a guess, so the capture stops
/// instead of appending content that might be duplicated, missing, or out of order.
/// </summary>
public enum ScrollStepFailure
{
    None = 0,

    /// <summary>
    /// The frames are identical. Either the content has reached its end, or the target
    /// window does not respond to the wheel at all. Both cases must stop the capture, and
    /// neither can be told apart from pixels alone.
    /// </summary>
    NoMovement,

    /// <summary>
    /// Several unrelated offsets match equally well. Repeating content (a table of
    /// identical rows) and uniform content (a blank page) both produce this, and picking
    /// the highest-scoring offset would silently misalign the stitch.
    /// </summary>
    Ambiguous,

    /// <summary>
    /// No offset matches well enough, so the content changed rather than scrolled.
    /// </summary>
    Unmatched,

    /// <summary>
    /// The content moved further than the region is tall, so almost nothing overlaps and
    /// continuity cannot be proven.
    /// </summary>
    TooLarge,
}

/// <summary>
/// The measured scroll between two frames, or the reason it could not be measured. The
/// scores are carried even on success because they are the only way to tell, from a user's
/// report of "it stopped", whether the content barely matched or matched nowhere.
/// </summary>
public readonly record struct ScrollStepResult(
    int Offset,
    ScrollStepFailure Failure,
    string? Reason,
    double BestFraction = 0,
    double BestDifference = 0)
{
    /// <summary>True when a positive offset was proven, so the frame can be appended.</summary>
    public bool IsUsable => Failure == ScrollStepFailure.None && Offset > 0;

    /// <summary>True when the content stopped moving, which ends the capture normally.</summary>
    public bool IsFinished => Failure == ScrollStepFailure.NoMovement;

    /// <summary>True when the capture must stop because the frames could not be related.</summary>
    public bool IsRefusal =>
        Failure is ScrollStepFailure.Ambiguous or ScrollStepFailure.Unmatched or ScrollStepFailure.TooLarge;

    /// <summary>
    /// True when reading again could give a different answer. Frames that match nowhere are
    /// usually mid-animation rather than genuinely unrelated: an application animating a
    /// multi-notch scroll can still be moving when the first read happens, so the read is
    /// repeated. Ambiguity and an over-large scroll are properties of the pixels themselves
    /// and will not change, so they are not worth repeating.
    /// </summary>
    public bool IsTransient => Failure == ScrollStepFailure.Unmatched;
}

/// <summary>
/// Tuning for the overlap match. The defaults are chosen for screen content, which is
/// pixel-exact apart from antialiasing, and they are exposed so tests can state the
/// behaviour they rely on instead of inheriting it.
/// </summary>
public sealed record ScrollMatchSettings
{
    /// <summary>Offsets below this are treated as no movement; sub-pixel jitter is not a scroll.</summary>
    public int MinimumOffset { get; init; } = 3;

    /// <summary>
    /// Rows that must still overlap for an offset to be trusted. An offset that leaves
    /// fewer than this is reported as <see cref="ScrollStepFailure.TooLarge"/>.
    /// </summary>
    public int MinimumOverlapRows { get; init; } = 16;

    /// <summary>Per-channel difference still counted as the same pixel.</summary>
    public int ChannelTolerance { get; init; } = 2;

    /// <summary>
    /// Fraction of sampled pixels that must match for an offset to be considered at all.
    /// Deliberately not near 1: a real screen region almost always contains something that
    /// does not scroll - a scrollbar, a fixed header, a focus ring - and a threshold tight
    /// enough to exclude those also excludes perfectly good content. Measured content that
    /// scrolls rigidly scores about 0.95 rather than 1.00 for exactly this reason, so the
    /// gate sits well below it and the mean difference does the actual discrimination.
    /// </summary>
    public double RequiredMatchFraction { get; init; } = 0.85;

    /// <summary>
    /// Per-channel difference above which a pixel counts as fully different. Capping keeps a
    /// small region that legitimately never matches - the moving scrollbar - from dominating
    /// the average, which is what lets the average pick a unique winner.
    /// </summary>
    public int DifferenceCap { get; init; } = 8;

    /// <summary>
    /// How much worse a rival offset must be than the best one, measured in mean capped
    /// difference. Content that repeats scores identically at two positions and is refused.
    /// </summary>
    public double AmbiguityMargin { get; init; } = 0.35;

    /// <summary>Offsets this close to the best are treated as the same answer, not a rival.</summary>
    public int AmbiguityNeighbourhood { get; init; } = 8;

    /// <summary>Sample every Nth column. Row sampling stays at one, because rows are the axis being matched.</summary>
    public int ColumnStep { get; init; } = 4;
}

/// <summary>
/// Measures how far a scroll region moved between two frames and appends the new rows.
/// Pure arithmetic over pixel buffers, so the whole decision - including when to refuse -
/// is unit tested without a desktop, a wheel, or a target application.
/// </summary>
public static class ScrollStitcher
{
    /// <summary>
    /// Finds the vertical offset that maps <paramref name="previous"/> onto
    /// <paramref name="current"/>, where an offset of d means
    /// current[y] == previous[y + d]: the content moved up by d pixels.
    /// </summary>
    public static ScrollStepResult Measure(
        byte[] previous,
        byte[] current,
        int width,
        int height,
        int stride,
        ScrollMatchSettings? settings = null)
    {
        settings ??= new ScrollMatchSettings();

        if (previous is null || current is null)
        {
            return new ScrollStepResult(0, ScrollStepFailure.Unmatched, "没有可比较的画面。");
        }

        if (width < 1 || height < 1 || stride < width * 4)
        {
            return new ScrollStepResult(0, ScrollStepFailure.Unmatched, "选区的像素尺寸无效。");
        }

        if (previous.Length < (long)stride * height || current.Length < (long)stride * height)
        {
            return new ScrollStepResult(0, ScrollStepFailure.Unmatched, "画面缓冲区小于其声明的尺寸。");
        }

        if (FramesAreIdentical(previous, current, width, height, stride))
        {
            return new ScrollStepResult(
                0,
                ScrollStepFailure.NoMovement,
                "画面没有变化：内容已到末尾，或该窗口不响应滚轮。");
        }

        var maximumOffset = height - settings.MinimumOverlapRows;
        if (maximumOffset <= settings.MinimumOffset)
        {
            return new ScrollStepResult(
                0,
                ScrollStepFailure.Unmatched,
                $"选区高度只有 {height} 像素，不足以判断滚动幅度。");
        }

        var columns = ((width - 1) / settings.ColumnStep) + 1;
        var fractions = new double[maximumOffset + 1];
        var differences = new double[maximumOffset + 1];
        var bestOffset = -1;
        var bestDifference = double.MaxValue;

        for (var offset = settings.MinimumOffset; offset <= maximumOffset; offset++)
        {
            ScoreOffset(
                previous,
                current,
                stride,
                height - offset,
                columns,
                offset,
                settings,
                out var fraction,
                out var difference);

            fractions[offset] = fraction;
            differences[offset] = difference;

            // Only offsets where most pixels agree are candidates, and among those the
            // smallest mean difference wins. The fraction alone cannot separate offsets on
            // smooth content - neighbouring offsets all agree within tolerance - so the mean
            // difference is what makes the answer unique rather than merely plausible.
            if (fraction >= settings.RequiredMatchFraction && difference < bestDifference)
            {
                bestDifference = difference;
                bestOffset = offset;
            }
        }

        if (bestOffset < 0)
        {
            // Report the best score even though it failed, so a report of "it stopped here"
            // can be diagnosed without a debugger: a near miss means the region included
            // something that does not scroll, a distant one means the content changed.
            // The scoring pass prunes candidates that cannot reach the requirement and reports
            // zero for them, so the best score has to be recovered with a pass that does not
            // prune - otherwise this diagnostic would always claim that nothing matched.
            var relaxed = settings with { RequiredMatchFraction = 0 };
            var nearMissOffset = -1;
            var nearMissFraction = 0d;
            var nearMissDifference = 0d;
            for (var offset = settings.MinimumOffset; offset <= maximumOffset; offset++)
            {
                ScoreOffset(
                    previous,
                    current,
                    stride,
                    height - offset,
                    columns,
                    offset,
                    relaxed,
                    out var fraction,
                    out var difference);

                if (fraction > nearMissFraction)
                {
                    nearMissFraction = fraction;
                    nearMissOffset = offset;
                    nearMissDifference = difference;
                }
            }

            return new ScrollStepResult(
                0,
                ScrollStepFailure.Unmatched,
                $"前后两帧找不到一致的衔接位置（最佳吻合率 {nearMissFraction:0.00}，偏移 {nearMissOffset} 像素），"
                + "内容可能已经变化而不是滚动，或选区里包含了不随之滚动的区域。",
                nearMissFraction,
                nearMissDifference);
        }

        if (height - bestOffset <= settings.MinimumOverlapRows)
        {
            return new ScrollStepResult(
                0,
                ScrollStepFailure.TooLarge,
                $"滚动幅度达到 {bestOffset} 像素，接近选区高度 {height} 像素，重叠太少无法确认内容连续。");
        }

        // A rival offset far from the best one means the content repeats: the same picture
        // appears at two different positions. Both answers are equally consistent with the
        // pixels, so choosing either would be a guess.
        for (var offset = settings.MinimumOffset; offset <= maximumOffset; offset++)
        {
            if (Math.Abs(offset - bestOffset) <= settings.AmbiguityNeighbourhood)
            {
                continue;
            }

            if (fractions[offset] >= settings.RequiredMatchFraction
                && differences[offset] <= bestDifference + settings.AmbiguityMargin)
            {
                return new ScrollStepResult(
                    0,
                    ScrollStepFailure.Ambiguous,
                    $"内容存在重复图案：偏移 {bestOffset} 像素和 {offset} 像素同样吻合，无法确定真实位置。");
            }
        }

        return new ScrollStepResult(bestOffset, ScrollStepFailure.None, null, fractions[bestOffset], bestDifference);
    }

    /// <summary>
    /// Appends the rows that scrolled into view. With an offset of d, the bottom d rows of
    /// <paramref name="next"/> are content that was not in <paramref name="stitched"/>.
    /// </summary>
    public static byte[] Append(
        byte[] stitched,
        int stitchedHeight,
        byte[] next,
        int nextHeight,
        int width,
        int stride,
        int offset)
    {
        ArgumentNullException.ThrowIfNull(stitched);
        ArgumentNullException.ThrowIfNull(next);

        if (offset <= 0 || offset > nextHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "The appended offset must be positive and no larger than the incoming frame.");
        }

        var rowBytes = width * 4;
        var result = new byte[(long)stride * (stitchedHeight + offset)];
        Array.Copy(stitched, 0, result, 0, (long)stride * stitchedHeight);

        for (var row = 0; row < offset; row++)
        {
            var sourceRow = nextHeight - offset + row;
            Array.Copy(
                next,
                (long)sourceRow * stride,
                result,
                (long)(stitchedHeight + row) * stride,
                rowBytes);
        }

        return result;
    }

    /// <summary>
    /// Scores one candidate offset two ways: the fraction of sampled pixels that agree, and
    /// the mean per-channel difference with each pixel capped. The fraction rejects offsets
    /// that are simply wrong; the mean difference separates offsets that all agree within
    /// tolerance, which is the only way to resolve smooth content. Pruned as soon as the
    /// candidate cannot reach the required fraction, which keeps the search affordable.
    /// </summary>
    private static void ScoreOffset(
        byte[] previous,
        byte[] current,
        int stride,
        int rows,
        int columns,
        int offset,
        ScrollMatchSettings settings,
        out double fraction,
        out double difference)
    {
        if (rows <= 0)
        {
            fraction = 0;
            difference = double.MaxValue;
            return;
        }

        var tolerance = settings.ChannelTolerance;
        var cap = settings.DifferenceCap;
        var total = (long)rows * columns;
        var required = (long)Math.Ceiling(total * settings.RequiredMatchFraction);
        var matching = 0L;
        var differenceSum = 0L;

        for (var row = 0; row < rows; row++)
        {
            var previousRow = (long)(row + offset) * stride;
            var currentRow = (long)row * stride;

            for (var column = 0; column < columns; column++)
            {
                var index = column * settings.ColumnStep * 4;
                var previousIndex = (int)(previousRow + index);
                var currentIndex = (int)(currentRow + index);

                var blue = Math.Abs(previous[previousIndex] - current[currentIndex]);
                var green = Math.Abs(previous[previousIndex + 1] - current[currentIndex + 1]);
                var red = Math.Abs(previous[previousIndex + 2] - current[currentIndex + 2]);

                if (blue <= tolerance && green <= tolerance && red <= tolerance)
                {
                    matching++;
                }

                differenceSum += Math.Min(blue, cap) + Math.Min(green, cap) + Math.Min(red, cap);
            }

            // Even a perfect score from here on cannot reach the requirement.
            if (matching + ((long)(rows - row - 1) * columns) < required)
            {
                fraction = 0;
                difference = double.MaxValue;
                return;
            }
        }

        fraction = (double)matching / total;
        difference = (double)differenceSum / (total * 3);
    }

    private static bool FramesAreIdentical(byte[] previous, byte[] current, int width, int height, int stride)
    {
        var rowBytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            var rowStart = (long)row * stride;
            for (var index = 0; index < rowBytes; index++)
            {
                if (previous[rowStart + index] != current[rowStart + index])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
