using System.Windows.Threading;
using Microsoft.Win32;

namespace PixelPin.Services;

/// <summary>Desktop conditions PixelPin has to recover from.</summary>
public enum EnvironmentChange
{
    /// <summary>Monitors were added, removed, rearranged, or changed resolution.</summary>
    DisplaySettings,

    /// <summary>The machine came back from sleep or hibernation.</summary>
    Resumed,

    /// <summary>The session was unlocked, including a remote desktop reconnect.</summary>
    Unlocked,
}

/// <summary>
/// Watches the three desktop events that quietly break a tray application.
///
/// A process that stays running for days cannot assume the environment it started in
/// still exists. Two of these are not recoverable by doing nothing:
///
/// - <b>Display changes</b> move the origin of the desktop. A sticker keeps its
///   absolute coordinates, so unplugging the monitor it sat on leaves a window that
///   is still running, still holding the image, and unreachable.
/// - <b>Resume and unlock</b> can drop global hotkeys. <c>RegisterHotKey</c>
///   registrations survive a suspend, but a low-level keyboard hook that misses its
///   timeout is removed by Windows without telling anyone, and the hook is the
///   compatibility path for applications that swallow the shortcut. Coming back from
///   sleep with a dead hotkey is the failure users report as "it stopped working".
///
/// Events arrive on the SystemEvents thread, so every callback is marshalled onto the
/// dispatcher before it reaches a handler; window and hotkey APIs have thread affinity.
/// </summary>
public sealed class EnvironmentWatchService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public EnvironmentWatchService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Raised on the dispatcher thread after the environment changed.</summary>
    public event Action<EnvironmentChange>? Changed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args) =>
        Raise(EnvironmentChange.DisplaySettings);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        // Suspend is worth ignoring: nothing can be repaired while the machine sleeps,
        // and the resume notification that follows is the one with work to do.
        if (args.Mode == PowerModes.Resume)
        {
            Raise(EnvironmentChange.Resumed);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        // A remote desktop reconnect arrives as RemoteConnect, a local logon screen as
        // SessionUnlock; both mean "the desktop you left is the desktop you got back".
        if (args.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect)
        {
            Raise(EnvironmentChange.Unlocked);
        }
    }

    private void Raise(EnvironmentChange change)
    {
        if (_disposed)
        {
            return;
        }

        // SystemEvents raises on its own thread; the handler touches windows and
        // hotkeys, so it must run on the dispatcher.
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
            {
                Changed?.Invoke(change);
            }
        }));
    }
}
