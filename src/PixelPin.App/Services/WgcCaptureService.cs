using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelPin.Interop;
using PixelPin.Utilities;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PixelPin.Services;

/// <summary>
/// Captures a window through Windows.Graphics.Capture, which reads the window's own
/// composed surface instead of asking it to draw itself.
/// <para>
/// This is what makes a covered window capturable without PrintWindow: the result is the
/// window's real content no matter what is stacked on top of it, hardware-accelerated
/// windows are captured correctly, and the frame carries the window's true alpha so rounded
/// corners stay rounded. It is also the only route that can reach a window that extends past
/// the edge of the desktop.
/// </para>
/// <para>
/// Everything here is optional: when the platform or the GPU cannot support it the service
/// reports a specific reason and the caller falls back to a GDI render. It never invents a
/// result.
/// </para>
/// </summary>
public static class WgcCaptureService
{
    /// <summary>How long to wait for the first frame before giving up on this route.</summary>
    public const int FrameTimeoutMilliseconds = 1200;

    private static readonly object Gate = new();

    private static ID3D11Device? _device;
    private static ID3D11DeviceContext? _context;
    private static IDirect3DDevice? _winrtDevice;
    private static IGraphicsCaptureItemInterop? _interop;
    private static string? _setupFailure;
    private static bool _attempted;

    /// <summary>Whether this machine can capture windows through Windows.Graphics.Capture.</summary>
    public static bool IsSupported
    {
        get
        {
            EnsureSetup();
            return _setupFailure is null;
        }
    }

    /// <summary>Why the route is unavailable, or null when it is available.</summary>
    public static string? UnavailableReason
    {
        get
        {
            EnsureSetup();
            return _setupFailure;
        }
    }

    /// <summary>
    /// Whether the yellow capture border was successfully suppressed on the most recent
    /// capture. Exposed because the border never reaches the frame, so a frame-only check
    /// cannot tell whether the suppression actually took effect.
    /// </summary>
    public static bool BorderSuppressionActive { get; private set; }

    /// <summary>How long the most recent capture took, for latency diagnostics.</summary>
    public static TimeSpan LastCaptureDuration { get; private set; }

    /// <summary>
    /// Captures one window. Returns a failure reason instead of an image when the window
    /// cannot be captured, so the caller can move on to the next route.
    /// </summary>
    public static WindowCaptureResult TryCapture(nint handle)
    {
        if (handle == nint.Zero || !NativeMethods.IsWindow(handle))
        {
            return new WindowCaptureResult(null, WindowCaptureMethod.GraphicsCapture, "窗口已关闭。");
        }

        if (NativeMethods.IsIconic(handle))
        {
            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                "窗口已最小化，Windows 不会为最小化的窗口提供画面。");
        }

        EnsureSetup();
        if (_setupFailure is not null)
        {
            return new WindowCaptureResult(null, WindowCaptureMethod.GraphicsCapture, _setupFailure);
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = Capture(handle);
            LastCaptureDuration = stopwatch.Elapsed;
            return result;
        }
        catch (Exception exception)
        {
            // A lost device is recoverable, so it is discarded and rebuilt on the next call.
            if (exception is SharpGen.Runtime.SharpGenException or COMException)
            {
                ResetDevice();
            }

            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                $"图形捕获失败：{exception.Message}");
        }
    }

    private static WindowCaptureResult Capture(nint handle)
    {
        var device = _device!;
        var context = _context!;

        var item = CreateItem(handle);
        if (item is null)
        {
            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                "Windows 拒绝为该窗口建立图形捕获会话。");
        }

        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                "窗口当前没有可截取的区域（可能已最小化）。");
        }

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice!,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            size);
        using var session = pool.CreateCaptureSession(item);

        // The yellow border is drawn by the system around a window while it is being
        // captured. It does not appear in the frame, but it flashes on screen, so it is
        // suppressed where the platform allows it.
        BorderSuppressionActive = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            try
            {
                session.IsBorderRequired = false;
                BorderSuppressionActive = true;
            }
            catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException)
            {
                // Not fatal: the capture still works, the user just sees the border.
            }
        }

        // A screenshot is of the window, not of where the pointer happened to be.
        try
        {
            session.IsCursorCaptureEnabled = false;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException)
        {
            // Older builds always include the cursor; not worth failing the capture over.
        }

        Direct3D11CaptureFrame? frame = null;
        using var arrived = new ManualResetEventSlim(false);
        pool.FrameArrived += (sender, _) =>
        {
            if (Volatile.Read(ref frame) is not null)
            {
                return;
            }

            try
            {
                frame = sender.TryGetNextFrame();
            }
            catch
            {
                // Reported as a timeout below, which carries the same meaning to the caller.
            }

            if (frame is not null)
            {
                arrived.Set();
            }
        };

        session.StartCapture();

        // FrameArrived runs on a pool thread because the pool is free-threaded, so waiting
        // here cannot deadlock on the UI thread's message pump.
        if (!arrived.Wait(FrameTimeoutMilliseconds))
        {
            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                $"窗口在 {FrameTimeoutMilliseconds} 毫秒内没有产生画面（可能受保护内容或正在最小化）。");
        }

        using var captured = Volatile.Read(ref frame);
        if (captured is null)
        {
            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                "图形捕获没有返回画面。");
        }

        return ReadFrame(device, context, captured);
    }

    private static WindowCaptureResult ReadFrame(
        ID3D11Device device,
        ID3D11DeviceContext context,
        Direct3D11CaptureFrame frame)
    {
        using var surface = frame.Surface;
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var textureId = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        var texturePointer = access.GetInterface(ref textureId);
        if (texturePointer == nint.Zero)
        {
            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                "图形捕获没有返回可读的表面。");
        }

        using var source = new ID3D11Texture2D(texturePointer);
        var description = source.Description;
        var width = (int)description.Width;
        var height = (int)description.Height;
        if (width <= 0 || height <= 0)
        {
            return new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                "图形捕获返回了空画面。");
        }

        // The GPU surface cannot be read directly, so it is copied into a staging texture
        // the CPU is allowed to map.
        var stagingDescription = description;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDescription.MiscFlags = ResourceOptionFlags.None;
        using var staging = device.CreateTexture2D(stagingDescription);
        context.CopyResource(staging, source);

        var stride = width * 4;
        var buffer = new byte[stride * height];
        var mapped = context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
        try
        {
            // RowPitch is usually larger than the row itself, so rows are copied one at a
            // time rather than as one block.
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(mapped.DataPointer + (row * (int)mapped.RowPitch), buffer, row * stride, stride);
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        // Windows.Graphics.Capture hands back premultiplied BGRA, which is exactly what
        // Pbgra32 means, so no per-pixel conversion is needed. The alpha is kept rather
        // than forced opaque: it is the window's real shape, so rounded corners stay
        // rounded instead of gaining black square corners.
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
        image.Freeze();

        var quality = CaptureQuality.Inspect(image);
        return quality == CaptureRejection.None
            ? new WindowCaptureResult(image, WindowCaptureMethod.GraphicsCapture, null)
            : new WindowCaptureResult(
                null,
                WindowCaptureMethod.GraphicsCapture,
                CaptureQuality.Describe(quality) ?? "窗口没有返回可用的画面。");
    }

    private static GraphicsCaptureItem? CreateItem(nint handle)
    {
        var interop = _interop;
        if (interop is null)
        {
            return null;
        }

        var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var pointer = interop.CreateForWindow(handle, ref itemId);
        if (pointer == nint.Zero)
        {
            return null;
        }

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static void EnsureSetup()
    {
        if (_attempted)
        {
            return;
        }

        lock (Gate)
        {
            if (_attempted)
            {
                return;
            }

            _attempted = true;
            _setupFailure = BuildDevice();
        }
    }

    /// <summary>
    /// Creates the D3D11 device and the activation factory the capture item needs. Each
    /// failure is reported with the specific step that failed so an unsupported machine is
    /// diagnosable rather than just "unavailable".
    /// </summary>
    private static string? BuildDevice()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return "Windows.Graphics.Capture 需要 Windows 10 2004 或更高版本。";
        }

        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                return "系统报告不支持窗口图形捕获（通常是在受限制的远程会话或旧显卡驱动下）。";
            }
        }
        catch (Exception exception)
        {
            return $"无法查询图形捕获支持：{exception.Message}";
        }

        var deviceFailure = CreateDevice();
        if (deviceFailure is not null)
        {
            return deviceFailure;
        }

        try
        {
            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var reference = (IObjectReference)factory;
            var interopId = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(reference.ThisPtr, ref interopId, out var interopPointer));
            try
            {
                _interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPointer);
            }
            finally
            {
                Marshal.Release(interopPointer);
            }
        }
        catch (Exception exception)
        {
            return $"无法取得窗口捕获工厂：{exception.Message}";
        }

        return null;
    }

    private static string? CreateDevice()
    {
        // A hardware device is preferred; WARP keeps the route working in software when the
        // GPU cannot be used, which is common over remote desktop.
        foreach (var driverType in new[] { DriverType.Hardware, DriverType.Warp })
        {
            try
            {
                FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

                // Explicitly typed locals pick the overload that also returns a context;
                // with `out var` the compiler cannot choose between the two overloads.
                ID3D11Device device;
                ID3D11DeviceContext context;
                var result = D3D11.D3D11CreateDevice(
                    null,
                    driverType,
                    DeviceCreationFlags.BgraSupport,
                    levels,
                    out device,
                    out context);

                if (result.Failure || device is null || context is null)
                {
                    continue;
                }

                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable);
                if (hr != 0 || inspectable == nint.Zero)
                {
                    device.Dispose();
                    context.Dispose();
                    continue;
                }

                try
                {
                    _device = device;
                    _context = context;
                    _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    return null;
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or DllNotFoundException or COMException)
            {
                // Try the next driver type.
            }
        }

        return "无法创建 Direct3D 设备，图形捕获不可用。";
    }

    private static void ResetDevice()
    {
        lock (Gate)
        {
            _winrtDevice = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _attempted = false;
            _setupFailure = null;
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);

        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(ref Guid iid);
    }
}
