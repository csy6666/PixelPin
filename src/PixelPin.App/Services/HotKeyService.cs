using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using PixelPin.Interop;
using PixelPin.Models;

namespace PixelPin.Services;

public sealed class HotKeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0x5000;

    public HotKeyService()
    {
        var parameters = new HwndSourceParameters("PixelPinHotKeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = (int)NativeMethods.WsExToolWindow,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool TryRegister(HotKeyDefinition definition, Action handler, out string error)
    {
        var id = Interlocked.Increment(ref _nextId);
        var modifiers = NativeMethods.ModNoRepeat;
        if (definition.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= NativeMethods.ModAlt;
        if (definition.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= NativeMethods.ModControl;
        if (definition.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= NativeMethods.ModShift;
        if (definition.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= NativeMethods.ModWin;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(definition.Key);
        if (virtualKey == 0 || !NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        _handlers[id] = handler;
        error = string.Empty;
        return true;
    }

    public void Clear()
    {
        foreach (var id in _handlers.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _handlers.Clear();
    }

    public void Dispose()
    {
        Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotKey && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }

        return IntPtr.Zero;
    }
}
