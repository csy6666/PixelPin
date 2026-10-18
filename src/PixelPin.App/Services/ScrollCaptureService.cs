using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using PixelPin.Interop;
using PixelPin.Utilities;

namespace PixelPin.Services;

/// <summary>Progress of a running scrolling capture, for the status window.</summary>
public sealed record ScrollCaptureProgress(int Frames, int StitchedHeight, string Message);

/// <summary>
/// The result of a scrolling capture. A partial image is returned alongside a failure when
/// the capture had to stop early, because the frames already stitched are real content; the
/// reason travels with them so the caller can say why it stopped instead of implying the
/// image is the whole thing.
/// </summary>
public sealed record ScrollCaptureOutcome(
    BitmapSource? Image,
    int Frames,
    int StitchedHeight,
    string? Failure)
{
    public bool Succeeded => Image is not null && Failure is null;

    /// <summary>True when some content was captured but the capture did not finish cleanly.</summary>
    public bool IsPartial => Image is not null && Failure is not null;
}

/// <summary>
/// Captures a region while scrolling it, stitching the frames it can prove are consecutive.
///
/// The design constraint is that a stitched screenshot which is subtly duplicated or has a
/// gap looks entirely plausible and is worse than no feature at all. So the offset is never
/// assumed from the wheel delta: it is measured from the pixels, and the capture stops with
/// a stated reason whenever the pixels cannot prove what happened.
/// </summary>
public sealed class ScrollCaptureService
{
    /// <summary>Regions smaller than this cannot show enough content to align frames.</summary>
    public const int MinimumRegionSize = 64;

    private const int DefaultMaximumHeight = 20000;

    /// <summary>
    /// Frame cap. At roughly one screenful per frame this is the limit that actually binds
    /// on a long page, well before the height cap, so it is generous enough not to truncate
    /// an ordinary long article while still bounding a capture that would never end.
    /// </summary>
    private const int DefaultMaximumFrames = 120;

    private readonly ScreenCaptureService _capture;

    public ScrollCaptureService(ScreenCaptureService capture)
    {
        _capture = capture;
    }

    /// <summary>How long to let the application settle after a wheel event before reading pixels.</summary>
    public int SettleMilliseconds { get; set; } = 220;

    /// <summary>Extra waits used when the first read after a wheel shows no movement yet.</summary>
    public int RetryMilliseconds { get; set; } = 220;

    /// <summary>Total stitched height at which the capture stops, so it cannot run away.</summary>
    public int MaximumHeight { get; set; } = DefaultMaximumHeight;

    /// <summary>Frame count at which the capture stops.</summary>
    public int MaximumFrames { get; set; } = DefaultMaximumFrames;

    /// <summary>
    /// Optional per-step trace. A scrolling capture can stop for reasons that are invisible
    /// from outside - the application moved further than one notch, or barely moved at all -
    /// so the numbers behind each decision are made available to whoever is diagnosing it.
    /// </summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Fraction of the region height that one step should advance. A third keeps two thirds
    /// of the frame as overlap, which is far more than the stitcher needs, while making a
    /// long page take a third as many frames as a single wheel notch would.
    /// </summary>
    private const int StepTargetDivisor = 3;

    /// <summary>
    /// Upper bound on the notches sent per step. Deliberately small: applications animate
    /// each notch, so a wide step is still moving long after the settle delay and the frame
    /// read catches it mid-flight, where no offset matches. Three notches keeps the step
    /// meaningfully faster than one while staying inside what a settle can absorb.
    /// </summary>
    private const int MaximumNotchesPerStep = 3;

    /// <summary>
    /// Gap between the separate wheel events of one step. Sent back to back they can be
    /// coalesced or dropped, and an application's own scroll animation needs a moment to
    /// begin before the next notch is meaningful.
    /// </summary>
    private const int InterNotchMilliseconds = 18;

    /// <summary>
    /// Extra settle time per additional notch in a step. Applications animate each notch, so
    /// a five notch step is still in flight long after a one notch step has landed; without
    /// this the read catches a moving frame, which matches no offset at all.
    /// </summary>
    private const int AdditionalSettlePerNotch = 120;

    public ScrollCaptureOutcome Capture(
        Int32Rect region,
        IProgress<ScrollCaptureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (region.Width < MinimumRegionSize || region.Height < MinimumRegionSize)
        {
            return Fail($"选区太小：滚动截图至少需要 {MinimumRegionSize}×{MinimumRegionSize} 像素，当前为 {region.Width}×{region.Height}。");
        }

        // A wheel event only reaches a window that can receive input, and the system routes
        // it to whatever is under the cursor. Both facts are checked before anything is
        // scrolled, because failing here costs nothing while failing mid-capture would leave
        // a half-stitched image behind.
        var centre = new NativePoint(
            region.X + (region.Width / 2),
            region.Y + (region.Height / 2));

        var target = NativeMethods.WindowFromPoint(centre);
        if (target == nint.Zero)
        {
            return Fail("选区位置没有可以接收滚轮的窗口。");
        }

        var root = NativeMethods.GetAncestor(target, NativeMethods.GaRoot);
        if (root != nint.Zero)
        {
            NativeMethods.SetForegroundWindow(root);
        }

        if (!NativeMethods.GetCursorPos(out var originalCursor))
        {
            originalCursor = centre;
        }

        try
        {
            if (!NativeMethods.SetCursorPos(centre.X, centre.Y))
            {
                return Fail("无法把指针移到选区内，滚轮事件会发送到别处。");
            }

            return CaptureCore(region, progress, cancellationToken);
        }
        finally
        {
            // The pointer was borrowed to route the wheel; put it back where the user left it.
            NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private ScrollCaptureOutcome CaptureCore(
        Int32Rect region,
        IProgress<ScrollCaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        var first = _capture.CaptureRegion(region);
        if (first is null)
        {
            return Fail("无法截取选区内容。");
        }

        var stride = first.PixelWidth * 4;
        var width = first.PixelWidth;
        var height = first.PixelHeight;

        var stitched = BitmapUtilities.ToBgraBuffer(first, out var sourceStride, out _, out _);
        if (sourceStride != stride)
        {
            stitched = Repack(stitched, sourceStride, stride, height);
        }

        var stitchedHeight = height;
        var previous = stitched;
        var frames = 1;

        progress?.Report(new ScrollCaptureProgress(frames, stitchedHeight, "已截取首帧，开始滚动。"));

        var stopReason = default(string);
        var reachedTheEnd = false;

        // A single wheel notch scrolls about three lines, which is a fraction of a screenful:
        // a long page would need well over a hundred frames at that rate. The distance per
        // notch is measured from the first successful step and the step is then widened to
        // about a third of the region, so the frame budget is spent on progress instead of
        // on redundant overlap.
        var notches = 1;
        var offsetPerNotch = 0d;

        while (frames < MaximumFrames && stitchedHeight < MaximumHeight)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Widening the step is a speed optimisation and must never be the reason a capture
            // fails. When a widened step produces frames that cannot be related - which real
            // applications do, because several queued notches can land somewhere the pixels no
            // longer prove - the step is narrowed and the same scroll retried before anything
            // is reported to the user.
            var stepNotches = notches;
            ScrollRead step;
            while (true)
            {
                SendWheel(stepNotches);
                Wait(SettleMilliseconds + ((stepNotches - 1) * AdditionalSettlePerNotch), cancellationToken);

                step = ReadStep(region, previous, width, height, stride, cancellationToken);
                if (step.Result.IsUsable
                    || step.Result.IsFinished
                    || (step.Result.IsRefusal && !step.Result.IsTransient)
                    || stepNotches <= 1)
                {
                    break;
                }

                // Let whatever the failed attempt started finish before adding more scroll:
                // retrying on top of a running animation compounds it instead of undoing it.
                Wait(RetryMilliseconds, cancellationToken);
                stepNotches = Math.Max(1, stepNotches / 2);
                Trace?.Invoke($"步长收窄到 {stepNotches} 格后重试。");
            }

            notches = stepNotches;

            if (step.Result.IsFinished)
            {
                stopReason = null;
                reachedTheEnd = true;
                break;
            }

            if (step.Result.IsRefusal || !step.Result.IsUsable)
            {
                stopReason = step.Result.Reason ?? "无法确认滚动幅度。";
                break;
            }

            var next = step.Frame!;
            stitched = ScrollStitcher.Append(stitched, stitchedHeight, next, height, width, stride, step.Result.Offset);
            stitchedHeight += step.Result.Offset;
            previous = next;
            frames++;

            // Widen the step from what the application actually did, never from an assumption
            // about how far a notch should scroll.
            var measuredPerNotch = (double)step.Result.Offset / Math.Max(1, stepNotches);
            if (measuredPerNotch >= 1)
            {
                offsetPerNotch = measuredPerNotch;
            }

            if (offsetPerNotch >= 1)
            {
                var target = Math.Max(height / StepTargetDivisor, offsetPerNotch);
                notches = Math.Clamp((int)Math.Round(target / offsetPerNotch), 1, MaximumNotchesPerStep);
            }

            Trace?.Invoke(
                $"步骤 {frames}: 滚轮 {stepNotches} 格 -> 偏移 {step.Result.Offset} 像素"
                + $"（每格 {measuredPerNotch:0.0}，吻合率 {step.Result.BestFraction:0.00}，差值 {step.Result.BestDifference:0.00}）");

            progress?.Report(new ScrollCaptureProgress(
                frames,
                stitchedHeight,
                $"已拼接 {frames} 帧，高度 {stitchedHeight} 像素。"));
        }

        if (frames < MaximumFrames && stitchedHeight >= MaximumHeight && stopReason is null)
        {
            stopReason = $"已达到 {MaximumHeight} 像素的高度上限，后面的内容没有截取。";
        }

        // A capture that ran out of frames must not be presented as having reached the end:
        // the page may well continue, and the user would have no way to tell. When the loop
        // stopped because the content genuinely ended, that is already stated as such.
        if (frames >= MaximumFrames && stopReason is null && !reachedTheEnd)
        {
            stopReason = $"已达到 {MaximumFrames} 帧的上限，后面的内容没有截取。";
        }

        var image = BitmapUtilities.FromBgraBuffer(stitched, width, stitchedHeight, stride);

        if (frames == 1)
        {
            // Nothing was appended, so this is an ordinary screenshot. Saying so is better
            // than presenting a single frame as a long capture.
            return new ScrollCaptureOutcome(
                image,
                1,
                stitchedHeight,
                stopReason ?? "没有检测到滚动：内容可能已到末尾，或该窗口不响应滚轮。");
        }

        return new ScrollCaptureOutcome(image, frames, stitchedHeight, stopReason);
    }

    /// <summary>A measured step together with the frame it was measured against.</summary>
    private readonly record struct ScrollRead(ScrollStepResult Result, byte[]? Frame);

    /// <summary>
    /// Reads the region until it either differs from the previous frame by a measurable
    /// offset, or consecutive reads agree that nothing is moving. The retry exists because
    /// applications animate their scroll, so the first read after a wheel event can land
    /// mid-flight and match nothing.
    /// </summary>
    private ScrollRead ReadStep(
        Int32Rect region,
        byte[] previous,
        int width,
        int height,
        int stride,
        CancellationToken cancellationToken)
    {
        var result = new ScrollStepResult(0, ScrollStepFailure.Unmatched, "滚动过程中无法截取选区内容。");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                Wait(RetryMilliseconds, cancellationToken);
            }

            var frame = _capture.CaptureRegion(region);
            if (frame is null)
            {
                return new ScrollRead(result, null);
            }

            var buffer = BitmapUtilities.ToBgraBuffer(frame, out var frameStride, out _, out _);
            if (frameStride != stride)
            {
                buffer = Repack(buffer, frameStride, stride, height);
            }

            result = ScrollStitcher.Measure(previous, buffer, width, height, stride);
            if (result.IsUsable || (result.IsRefusal && !result.IsTransient))
            {
                return new ScrollRead(result, buffer);
            }

            Trace?.Invoke(
                $"读取 {attempt + 1}: {result.Failure}，吻合率 {result.BestFraction:0.00}，"
                + $"平均亮度 {MeanBrightness(buffer, stride, height)}");

            if (result.IsTransient)
            {
                DumpFrame(previous, width, height, stride, "previous");
                DumpFrame(buffer, width, height, stride, "current");
            }
        }

        return new ScrollRead(result, null);
    }

    /// <summary>
    /// Sends the given number of wheel notches as separate events rather than one event with
    /// a multiplied delta. WPF's ScrollViewer reads only the sign of the delta and scrolls a
    /// fixed three lines, so a single event carrying three notches scrolls exactly as far as
    /// one notch - which silently defeats any attempt to take bigger steps. Separate events
    /// work for WPF and for applications that do honour the magnitude.
    /// </summary>
    private static void SendWheel(int notches)
    {
        for (var index = 0; index < notches; index++)
        {
            SendWheelNotch();
            if (index + 1 < notches)
            {
                Thread.Sleep(InterNotchMilliseconds);
            }
        }
    }

    private static void SendWheelNotch()
    {
        var inputs = new[]
        {
            new NativeInput
            {
                Type = NativeMethods.InputMouse,
                MouseFlags = NativeMethods.MouseEventWheel,
                MouseData = unchecked((uint)-NativeMethods.WheelDelta),
            },
        };

        var size = System.Runtime.InteropServices.Marshal.SizeOf<NativeInput>();
        var sent = NativeMethods.SendInput(1, inputs, size);
        if (sent != 1)
        {
            throw new InvalidOperationException(
                "无法向目标窗口发送滚轮事件，滚动截图已停止。");
        }
    }

    private static void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0)
        {
            return;
        }

        // Pump-free sleep: the caller runs this off the dispatcher, and the target
        // application needs the time to finish its own scroll.
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Diagnostic frame dump, enabled by setting PIXELPIN_SCROLL_DUMP to a directory. Written
    /// only when a read cannot be related, because that is the case where the pixels hold the
    /// answer and the report alone cannot distinguish a changed window from a measurement
    /// fault.
    /// </summary>
    private static void DumpFrame(byte[] buffer, int width, int height, int stride, string name)
    {
        var directory = Environment.GetEnvironmentVariable("PIXELPIN_SCROLL_DUMP");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var image = BitmapUtilities.FromBgraBuffer(buffer, width, height, stride);
            var path = Path.Combine(
                directory,
                $"{DateTime.Now:HHmmssfff}-{name}-{width}x{height}.png");
            BitmapUtilities.SavePngToPath(image, path);
        }
        catch
        {
            // A diagnostic must never be the reason a capture fails.
        }
    }

    /// <summary>
    /// Mean brightness of a frame, sampled coarsely. Only used for the diagnostic trace, where
    /// it distinguishes "the region went black" from "the content changed" - two failures that
    /// otherwise produce the identical report of no matching offset.
    /// </summary>
    private static int MeanBrightness(byte[] buffer, int stride, int height)
    {
        if (buffer.Length < 4 || stride <= 0 || height <= 0)
        {
            return 0;
        }

        long total = 0;
        var samples = 0;
        var step = Math.Max(4, (stride * height) / 4096 / 4 * 4);
        for (var index = 0; index + 3 < buffer.Length; index += step)
        {
            total += (buffer[index] + buffer[index + 1] + buffer[index + 2]) / 3;
            samples++;
        }

        return samples == 0 ? 0 : (int)(total / samples);
    }

    /// <summary>Rewrites rows into a buffer with the requested stride.</summary>
    private static byte[] Repack(byte[] buffer, int sourceStride, int targetStride, int height)
    {
        if (sourceStride == targetStride)
        {
            return buffer;
        }

        var result = new byte[(long)targetStride * height];
        var rowBytes = Math.Min(sourceStride, targetStride);
        for (var row = 0; row < height; row++)
        {
            Array.Copy(buffer, (long)row * sourceStride, result, (long)row * targetStride, rowBytes);
        }

        return result;
    }

    private static ScrollCaptureOutcome Fail(string reason) => new(null, 0, 0, reason);
}
