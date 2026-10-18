using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PixelPin.Interop;

namespace PixelPin.Services;

/// <summary>How the application picks its colours.</summary>
public enum AppTheme
{
    /// <summary>Follow the Windows apps setting and react when it changes.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Swaps the token dictionary at runtime and tells the window manager what the
/// application decided.
/// </summary>
/// <remarks>
/// The application ships two dictionaries with identical keys. Switching is a single
/// replacement of the first merged dictionary, which is why every window refers to
/// its colours with <c>DynamicResource</c>: a <c>StaticResource</c> would keep the
/// brush it resolved at parse time and the window would stay light.
/// </remarks>
public static class ThemeService
{
    /// <summary>
    /// Qualified with the assembly name on purpose. A relative source resolves
    /// against the entry assembly, so a host that merely references PixelPin (the
    /// desktop probe, the render harness) would look for the dictionary in its own
    /// resources and fail to find it.
    /// </summary>
    private const string LightSource = "pack://application:,,,/PixelPin;component/Themes/Tokens.Light.xaml";
    private const string DarkSource = "pack://application:,,,/PixelPin;component/Themes/Tokens.Dark.xaml";

    private static bool _watchingSystem;
    private static bool _hooked;

    /// <summary>Raised after the token dictionary has been replaced.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>The preference the user chose, which may be <see cref="AppTheme.System"/>.</summary>
    public static AppTheme Preference { get; private set; } = AppTheme.System;

    /// <summary>The theme actually in effect after resolving <see cref="Preference"/>.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Applies a preference and, when it is <see cref="AppTheme.System"/>, starts
    /// following the OS setting. Safe to call repeatedly; it only rebuilds the
    /// dictionary when the resolved theme actually changes.
    /// </summary>
    public static void Apply(AppTheme preference)
    {
        Preference = preference;

        if (preference == AppTheme.System)
        {
            StartWatchingSystem();
        }

        var dark = preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        if (dark == IsDark && FindTokenDictionary() is not null)
        {
            // Already showing this theme; still make sure every open window has the
            // right title bar, because a window can be created after the last apply.
            ApplyChromeToOpenWindows();
            return;
        }

        IsDark = dark;
        ReplaceTokens(dark);
        ApplyChromeToOpenWindows();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Parses a stored setting, falling back to <see cref="AppTheme.System"/>.</summary>
    public static AppTheme Parse(string? value)
    {
        return Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) ? theme : AppTheme.System;
    }

    /// <summary>Formats the preference for the settings file.</summary>
    public static string Format(AppTheme theme) => theme.ToString();

    /// <summary>Display name for the settings window.</summary>
    public static string Describe(AppTheme theme) => theme switch
    {
        AppTheme.Light => "浅色",
        AppTheme.Dark => "深色",
        _ => "跟随系统",
    };

    /// <summary>
    /// Reads the Windows apps theme. Anything unexpected is treated as light, which
    /// is the safer failure: a light window on a dark desktop is ugly, but a dark
    /// window on a light desktop with unreadable text is a bug report.
    /// </summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void StartWatchingSystem()
    {
        if (_watchingSystem)
        {
            return;
        }

        _watchingSystem = true;
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)
                || Preference != AppTheme.System)
            {
                return;
            }

            var application = Application.Current;
            if (application is null)
            {
                return;
            }

            // The registry value is written before the notification arrives, but not
            // always by the same thread; hop to the UI thread before touching visuals.
            application.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        };
    }

    private static ResourceDictionary? FindTokenDictionary()
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        foreach (var dictionary in application.Resources.MergedDictionaries)
        {
            var source = dictionary.Source?.OriginalString ?? string.Empty;
            if (source.EndsWith("Tokens.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Tokens.Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return dictionary;
            }
        }

        return null;
    }

    private static void ReplaceTokens(bool dark)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var merged = application.Resources.MergedDictionaries;
        var existing = FindTokenDictionary();
        var replacement = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute),
        };

        if (existing is null)
        {
            merged.Insert(0, replacement);
            return;
        }

        var index = merged.IndexOf(existing);
        merged[index] = replacement;
    }

    private static void ApplyChromeToOpenWindows()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        EnsureWindowHook(application);
        foreach (Window window in application.Windows)
        {
            ApplyWindowChrome(window);
        }
    }

    /// <summary>
    /// Hooks every window as it is created, so a window opened later gets the same
    /// title bar without each call site remembering to ask for it. Loaded is used
    /// rather than SourceInitialized because only Loaded is a routed event that a
    /// class handler can observe, and by then the window handle exists.
    /// </summary>
    private static void EnsureWindowHook(Application application)
    {
        if (_hooked)
        {
            return;
        }

        _hooked = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                // Loaded bubbles from every child, so only act once per window.
                if (sender is Window window && _chromed.TryAdd(window, null))
                {
                    ApplyWindowChrome(window);
                }
            }));
    }

    /// <summary>
    /// Windows that already had their non-client area painted. A weak table keeps
    /// closed stickers from being retained for the lifetime of the process.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object?> _chromed = new();

    /// <summary>
    /// Paints the non-client area to match the theme. The DWM ignores unknown
    /// attributes, so the same call works on Windows 10 (where only the dark title
    /// bar exists) and Windows 11 (where the frame can also be rounded and tinted).
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var dark = IsDark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }

        var round = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(
            handle, NativeMethods.DwmwaWindowCornerPreference, ref round, sizeof(int));

        // Caption and border colours are Windows 11 only. Passing the theme's own
        // surface keeps the title bar from looking like a separate, lighter panel.
        if (window.ResizeMode == ResizeMode.NoResize || window.WindowStyle == WindowStyle.None)
        {
            return;
        }

        if (TryColor("ToolbarBackgroundBrush", out var caption))
        {
            NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DwmwaCaptionColor, ref caption, sizeof(int));
        }

        if (TryColor("TextPrimaryBrush", out var text))
        {
            NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DwmwaTextColor, ref text, sizeof(int));
        }

        if (TryColor("DividerBrush", out var border))
        {
            NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DwmwaBorderColor, ref border, sizeof(int));
        }
    }

    /// <summary>
    /// Converts a token brush into the COLORREF the DWM expects, which is 0x00BBGGRR
    /// rather than the 0xRRGGBB used everywhere else.
    /// </summary>
    private static bool TryColor(string key, out int colorRef)
    {
        colorRef = 0;
        if (Application.Current?.TryFindResource(key) is not SolidColorBrush brush)
        {
            return false;
        }

        var color = brush.Color;
        colorRef = color.R | (color.G << 8) | (color.B << 16);
        return true;
    }
}
