using System.Runtime.InteropServices;
using System.Text;

namespace PixelPin.Interop;

internal static class NativeMethods
{
    public const int WmHotKey = 0x0312;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const int WhKeyboardLl = 13;

    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const nint WsExTransparent = 0x00000020;
    public const nint WsExToolWindow = 0x00000080;
    public const nint WsExNoActivate = 0x08000000;
    public const nint WsExLayered = 0x00080000;
    public const int DwmwaCloaked = 14;

    /// <summary>Dark title bar. The value was 19 before Windows 10 2004 and 20 after.</summary>
    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaUseImmersiveDarkModeLegacy = 19;

    /// <summary>Window corner preference, Windows 11 22000 and later.</summary>
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwcpRound = 2;

    public const int DwmwaBorderColor = 34;
    public const int DwmwaCaptionColor = 35;
    public const int DwmwaTextColor = 36;

    /// <summary>Extended frame bounds, which exclude the invisible resize border.</summary>
    public const int DwmwaExtendedFrameBounds = 9;

    public const nint HwndTopmost = -1;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    /// <summary>
    /// PrintWindow flag that asks the window to render its full content rather than only
    /// the visible region. Without it an occluded window renders as whatever covers it.
    /// </summary>
    public const uint PwRenderFullContent = 0x00000002;

    public const uint DibRgbColors = 0;
    public const uint BiRgb = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader Header;

        /// <summary>Colour table. One entry is enough for 32-bit output.</summary>
        public uint Colour;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern nint SetFocus(nint hWnd);

    /// <summary>
    /// Takes the foreground for a window this process just showed. A plain SetForegroundWindow
    /// is refused when the calling process did not itself receive the input that triggered the
    /// window, which leaves a full-screen overlay visible and on top but without keyboard focus
    /// - every shortcut then goes to the application behind it. Attaching to the foreground
    /// thread's input queue first lifts that restriction, which is the documented way to do
    /// this and what every screenshot tool ends up doing.
    /// </summary>
    public static void ForceForegroundWindow(nint hWnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hWnd)
        {
            return;
        }

        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();

        if (foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true))
        {
            try
            {
                SetForegroundWindow(hWnd);
                SetFocus(hWnd);
            }
            finally
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }

            return;
        }

        SetForegroundWindow(hWnd);
        SetFocus(hWnd);
    }

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hWnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hWnd, int attribute, out int value, int size);

    /// <summary>Sets a DWM window attribute, used to darken the title bar and round the frame.</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(nint hWnd, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(nint hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(nint hWnd, int index, int newStyle);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint hObject);

    /// <summary>
    /// Asks a window to draw itself into the given device context. With
    /// <see cref="PwRenderFullContent"/> this returns the window's own content even when
    /// it is covered by another window.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(nint hWnd, nint hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern nint GetWindowDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(
        nint hdc,
        ref BitmapInfo info,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(
        nint hdc,
        nint hBitmap,
        uint start,
        uint lines,
        byte[]? bits,
        ref BitmapInfo info,
        uint usage);

    /// <summary>DWM extended frame bounds, which exclude the invisible resize border.</summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeRect(nint hWnd, int attribute, out NativeRect value, int size);

    /// <summary>
    /// The window directly under a screen point. Scrolling capture uses this to find the
    /// window that should receive the wheel, which is the one the user pointed at rather
    /// than the one PixelPin happens to consider active.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hWnd, uint flags);

    /// <summary>
    /// Injects an input event into the system queue. A wheel event is routed by the system
    /// to whatever window is under the cursor, so it reaches the application exactly as a
    /// real wheel turn would. Posting WM_MOUSEWHEEL to the window handle instead is ignored
    /// by many applications, which is why the input path is used.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    public const uint InputMouse = 0;
    public const uint MouseEventWheel = 0x0800;
    public const uint MouseEventHWheel = 0x1000;
    public const uint GaRoot = 2;
    public const int WheelDelta = 120;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;

    public NativePoint(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// INPUT, flattened to its mouse member. The padding fields are not decoration: on x64 the
/// union starts at offset 8, so a struct without them puts every field 4 bytes early and
/// SendInput rejects it by returning zero without setting a useful error. The layout is
/// asserted by a unit test rather than trusted.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeInput
{
    public uint Type;
    public uint Alignment;

    public int MouseX;
    public int MouseY;
    public uint MouseData;
    public uint MouseFlags;
    public uint Time;
    public uint TimeAlignment;

    public nint ExtraInfo;
}

internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;

    public int Height => Bottom - Top;
}
