using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using PixelPin.Interop;
using PixelPin.Models;

namespace PixelPin.Services;

public sealed class HotKeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotKeyRegistration> _handlers = new();
    private readonly Dictionary<int, long> _lastInvocationTicks = new();
    private readonly HashSet<uint> _pressedKeys = new();
    private readonly HashSet<uint> _suppressedKeys = new();
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardHookProc;
    private int _nextId = 0x5000;
    private nint _keyboardHook;

    private sealed class HotKeyRegistration
    {
        public required HotKeyDefinition Definition { get; init; }
        public required uint VirtualKey { get; init; }
        public required Action Handler { get; init; }
        public bool RegisteredWithWindows { get; init; }
    }

    public HotKeyService()
    {
        _keyboardHookProc = KeyboardHookProc;
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
        if (virtualKey == 0)
        {
            error = "无法识别该快捷键。";
            return false;
        }

        foreach (var existing in _handlers.Values)
        {
            if (existing.VirtualKey == virtualKey && existing.Definition.Modifiers == definition.Modifiers)
            {
                error = "该快捷键已被 PixelPin 的其他功能使用。";
                return false;
            }
        }

        var registeredWithWindows = virtualKey != 0
            && NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, virtualKey);
        var registrationError = registeredWithWindows
            ? string.Empty
            : new Win32Exception(Marshal.GetLastWin32Error()).Message;

        _handlers[id] = new HotKeyRegistration
        {
            Definition = definition,
            VirtualKey = virtualKey,
            Handler = handler,
            RegisteredWithWindows = registeredWithWindows,
        };

        // Some apps consume keyboard input before RegisterHotKey can deliver WM_HOTKEY.
        // Keep a low-level hook as a compatibility path while retaining RegisterHotKey
        // for the normal case. The invocation guard below prevents double triggers.
        if (!EnsureKeyboardHook(out var hookError) && !registeredWithWindows)
        {
            _handlers.Remove(id);
            error = $"{registrationError}（兼容键盘钩子也无法启用：{hookError}）";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public void Clear()
    {
        foreach (var pair in _handlers)
        {
            if (pair.Value.RegisteredWithWindows)
            {
                NativeMethods.UnregisterHotKey(_source.Handle, pair.Key);
            }
        }

        _handlers.Clear();
        _lastInvocationTicks.Clear();
        _pressedKeys.Clear();
        _suppressedKeys.Clear();

        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
    }

    public void Dispose()
    {
        Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotKey && _handlers.TryGetValue(wParam.ToInt32(), out var registration))
        {
            handled = true;
            Invoke(wParam.ToInt32(), registration);
        }

        return IntPtr.Zero;
    }

    private bool EnsureKeyboardHook(out string error)
    {
        if (_keyboardHook != 0)
        {
            error = string.Empty;
            return true;
        }

        var moduleHandle = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _keyboardHookProc,
            moduleHandle,
            0);

        if (_keyboardHook != 0)
        {
            error = string.Empty;
            return true;
        }

        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return false;
    }

    private nint KeyboardHookProc(int nCode, nint wParam, nint lParam)
    {
        var suppressKey = false;
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
            var isKeyDown = wParam == NativeMethods.WmKeyDown || wParam == NativeMethods.WmSysKeyDown;
            var isKeyUp = wParam == NativeMethods.WmKeyUp || wParam == NativeMethods.WmSysKeyUp;

            if (isKeyDown && _pressedKeys.Add(data.VkCode))
            {
                foreach (var pair in _handlers)
                {
                    var registration = pair.Value;
                    if (registration.VirtualKey != data.VkCode || !MatchesModifiers(registration.Definition.Modifiers))
                    {
                        continue;
                    }

                    var id = pair.Key;
                    _source.Dispatcher.BeginInvoke(new Action(() => Invoke(id, registration)));
                    _suppressedKeys.Add(data.VkCode);
                    suppressKey = true;
                }
            }
            else if (isKeyUp)
            {
                _pressedKeys.Remove(data.VkCode);
                suppressKey = _suppressedKeys.Remove(data.VkCode);
            }
        }

        // The shortcut belongs to PixelPin; do not leak a matching keypress into
        // the target application (for example opening its Help window on F1).
        if (suppressKey)
        {
            return (nint)1;
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool MatchesModifiers(ModifierKeys modifiers)
    {
        var controlDown = IsAnyKeyDown(NativeMethods.VkLControl, NativeMethods.VkRControl);
        var altDown = IsAnyKeyDown(NativeMethods.VkLMenu, NativeMethods.VkRMenu);
        var shiftDown = IsAnyKeyDown(NativeMethods.VkLShift, NativeMethods.VkRShift);
        var windowsDown = IsAnyKeyDown(NativeMethods.VkLWin, NativeMethods.VkRWin);

        return controlDown == modifiers.HasFlag(ModifierKeys.Control)
            && altDown == modifiers.HasFlag(ModifierKeys.Alt)
            && shiftDown == modifiers.HasFlag(ModifierKeys.Shift)
            && windowsDown == modifiers.HasFlag(ModifierKeys.Windows);
    }

    private static bool IsAnyKeyDown(int firstKey, int secondKey)
    {
        return (NativeMethods.GetAsyncKeyState(firstKey) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(secondKey) & 0x8000) != 0;
    }

    private void Invoke(int id, HotKeyRegistration registration)
    {
        if (!_handlers.TryGetValue(id, out var current) || !ReferenceEquals(current, registration))
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastInvocationTicks.TryGetValue(id, out var previous)
            && now - previous < Stopwatch.Frequency / 5)
        {
            return;
        }

        _lastInvocationTicks[id] = now;
        registration.Handler();
    }
}
