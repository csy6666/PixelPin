using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PixelPin.Models;
using PixelPin.Services;
using PixelPin.Utilities;
using PixelPin.Windows;

namespace PixelPin;

public sealed class AppController : IDisposable
{
    private readonly Application _application;
    private readonly SettingsService _settingsService = new();
    private readonly HistoryService _historyService = new();
    private readonly StartupRegistrationService _startupService = new();
    private readonly ScreenCaptureService _captureService = new();
    private readonly ApplicationLogService _logService = new();
    private readonly List<StickyWindow> _stickers = [];

    private HotKeyService? _hotKeys;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private WindowPickerWindow? _windowPicker;
    private StickerManagerWindow? _stickerManager;
    private StickyWindow? _lastSticker;
    private bool _captureInProgress;
    private bool _disposed;

    public AppController(Application application)
    {
        _application = application;
        Settings = _settingsService.Load();
        _historyService.Load();
        _historyService.TrimToLimits(Settings.HistoryLimit, Settings.HistoryDiskLimitMegabytes);
        _logService.Info("Application", "Controller initialized.");
    }

    public AppSettings Settings { get; private set; }

    public void Start(IReadOnlyCollection<string>? launchArguments = null)
    {
        AppPaths.EnsureDirectories();
        _logService.Info("Application", "PixelPin started.");
        _hotKeys = new HotKeyService();
        var skipHotKeys = launchArguments?.Contains("--no-hotkeys", StringComparer.OrdinalIgnoreCase) == true;
        if (!skipHotKeys && !RegisterHotKeys(Settings, out var hotKeyError))
        {
            ShowError("PixelPin hotkey setup", hotKeyError);
        }
        else if (skipHotKeys)
        {
            _logService.Info("Application", "Started with global hotkeys disabled by launch argument.");
        }

        TrySetStartupRegistration(Settings.StartWithWindows);

        _tray = new TrayService(
            () => RunOnUi(BeginRegionCapture),
            () => RunOnUi(CaptureActiveWindow),
            () => RunOnUi(ShowWindowPicker),
            () => RunOnUi(PasteImageAsSticker),
            () => RunOnUi(OpenImageFiles),
            () => RunOnUi(ShowHistory),
            () => RunOnUi(ShowStickerManager),
            () => RunOnUi(ShowSettings),
            () => RunOnUi(ShowDiagnostics),
            () => RunOnUi(CloseAllStickers),
            () => RunOnUi(ShowScrollingCaptureUnavailable),
            () => RunOnUi(Exit));

        if (launchArguments?.Contains("--settings", StringComparer.OrdinalIgnoreCase) == true)
        {
            _application.Dispatcher.BeginInvoke(ShowSettings);
        }
        else if (launchArguments?.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase) == true)
        {
            _application.Dispatcher.BeginInvoke(ShowDiagnostics);
        }
        else if (launchArguments?.Contains("--history", StringComparer.OrdinalIgnoreCase) == true)
        {
            _application.Dispatcher.BeginInvoke(ShowHistory);
        }
        else if (launchArguments?.Contains("--windows", StringComparer.OrdinalIgnoreCase) == true)
        {
            _application.Dispatcher.BeginInvoke(ShowWindowPicker);
        }
        else if (launchArguments?.Contains("--stickers", StringComparer.OrdinalIgnoreCase) == true)
        {
            _application.Dispatcher.BeginInvoke(ShowStickerManager);
        }
    }

    public void ShowError(string title, string message)
    {
        _logService.Error(title, message);
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ReportUnhandled(Exception exception)
    {
        _logService.Error("Unhandled exception", exception.Message, exception);
    }

    public string? ApplySettings(AppSettings candidate)
    {
        candidate.Normalize();

        if (!HotKeyDefinition.TryParse(candidate.CaptureHotKey, out _, out var captureError))
        {
            return $"Capture shortcut: {captureError}";
        }

        if (!HotKeyDefinition.TryParse(candidate.ToggleMouseThroughHotKey, out _, out var mouseThroughError))
        {
            return $"Mouse-through shortcut: {mouseThroughError}";
        }

        var previous = Settings;
        _hotKeys?.Clear();
        if (!RegisterHotKeys(candidate, out var registrationError))
        {
            _hotKeys?.Clear();
            RegisterHotKeys(previous, out _);
            return $"Could not register a global shortcut. It may already be in use. Windows said: {registrationError}";
        }

        try
        {
            _settingsService.Save(candidate);
            TrySetStartupRegistration(candidate.StartWithWindows);
            Settings = candidate;
            _historyService.TrimToLimits(Settings.HistoryLimit, Settings.HistoryDiskLimitMegabytes);
            return null;
        }
        catch (Exception exception)
        {
            _hotKeys?.Clear();
            RegisterHotKeys(previous, out _);
            return exception.Message;
        }
    }

    public void BeginRegionCapture()
    {
        if (_captureInProgress)
        {
            return;
        }

        try
        {
            _captureInProgress = true;
            var desktop = _captureService.CaptureDesktop();
            var overlays = CreateOverlays(desktop);
            if (overlays.Count == 0)
            {
                _captureInProgress = false;
                ShowError("PixelPin capture", "Windows did not report a capturable display.");
                return;
            }

            var completed = false;
            void CloseSession()
            {
                foreach (var overlay in overlays)
                {
                    overlay.CloseSilently();
                }
                _captureInProgress = false;
            }

            foreach (var overlay in overlays)
            {
                overlay.Completed += (image, action) =>
                {
                    if (completed)
                    {
                        return;
                    }

                    completed = true;
                    CloseSession();
                    HandleCapture(new CaptureOutcome(image), action);
                };
                overlay.Cancelled += () =>
                {
                    if (completed)
                    {
                        return;
                    }

                    completed = true;
                    CloseSession();
                };
            }

            foreach (var overlay in overlays)
            {
                overlay.Show();
            }
        }
        catch (Exception exception)
        {
            _captureInProgress = false;
            ShowError("PixelPin capture", exception.Message);
        }
    }

    public void CaptureActiveWindow()
    {
        try
        {
            HandleCapture(_captureService.CaptureForegroundWindow(), CaptureAction.Pin);
        }
        catch (Exception exception)
        {
            ShowError("Active window capture", exception.Message);
        }
    }

    public void ShowWindowPicker()
    {
        if (_windowPicker is { IsVisible: true })
        {
            _windowPicker.RefreshWindows();
            _windowPicker.Activate();
            return;
        }

        _windowPicker = new WindowPickerWindow(
            _captureService.EnumerateCaptureWindows,
            CaptureSelectedWindow);
        _windowPicker.Closed += (_, _) => _windowPicker = null;
        _windowPicker.Show();
        _windowPicker.Activate();
    }

    public void PasteImageAsSticker()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                ShowError("Paste image", "The clipboard does not contain an image.");
                return;
            }

            var image = Clipboard.GetImage();
            if (image is null)
            {
                ShowError("Paste image", "Windows could not read the clipboard image.");
                return;
            }

            if (!image.IsFrozen && image.CanFreeze)
            {
                image.Freeze();
            }

            CacheAndShowSticker(image);
        }
        catch (Exception exception)
        {
            ShowError("Paste image", exception.Message);
        }
    }

    public void OpenImageFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open images as stickers",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            try
            {
                CacheAndShowSticker(LoadImageFile(file));
            }
            catch (Exception exception)
            {
                ShowError("Open image", $"{Path.GetFileName(file)} could not be opened: {exception.Message}");
            }
        }
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            Settings,
            ApplySettings,
            _settingsService.Import,
            _settingsService.Export,
            ShowDiagnostics);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ShowHistory()
    {
        if (_historyWindow is { IsVisible: true })
        {
            _historyWindow.Refresh();
            _historyWindow.Activate();
            return;
        }

        _historyWindow = new HistoryWindow(
            _historyService,
            image => ShowSticker(image),
            image => CopyImage(image),
            image => SaveImage(image),
            image => OpenEditor(image));
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    public void ShowDiagnostics()
    {
        if (_diagnosticsWindow is { IsVisible: true })
        {
            _diagnosticsWindow.RefreshReport();
            _diagnosticsWindow.Activate();
            return;
        }

        _diagnosticsWindow = new DiagnosticsWindow(
            BuildDiagnosticReport,
            OpenLocalDataFolder,
            ClearDiagnostics);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
        _diagnosticsWindow.Activate();
    }

    public void ShowStickerManager()
    {
        if (_stickerManager is { IsVisible: true })
        {
            _stickerManager.Refresh();
            _stickerManager.Activate();
            return;
        }

        _stickerManager = new StickerManagerWindow(
            () => _stickers.ToList(),
            (sticker, group) => sticker.SetGroupName(group),
            sticker => sticker.BringToFront(),
            sticker => sticker.SetAlwaysOnTop(!sticker.IsAlwaysOnTop),
            sticker => sticker.SetMouseThrough(!sticker.IsMouseThrough),
            CloseSticker,
            CloseStickerGroup,
            CloseAllStickers);
        _stickerManager.Closed += (_, _) => _stickerManager = null;
        _stickerManager.Show();
        _stickerManager.Activate();
    }

    public void Exit()
    {
        if (_disposed)
        {
            return;
        }

        _application.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logService.Info("Application", "PixelPin is shutting down.");
        _tray?.Dispose();
        _hotKeys?.Dispose();

        foreach (var sticker in _stickers.ToArray())
        {
            sticker.Close();
        }

        _stickers.Clear();
    }

    private List<SelectionOverlayWindow> CreateOverlays(CapturedDesktop desktop)
    {
        var overlays = new List<SelectionOverlayWindow>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            var crop = new Int32Rect(
                bounds.Left - desktop.Bounds.Left,
                bounds.Top - desktop.Bounds.Top,
                bounds.Width,
                bounds.Height);

            if (crop.X < 0 || crop.Y < 0
                || crop.X + crop.Width > desktop.Image.PixelWidth
                || crop.Y + crop.Height > desktop.Image.PixelHeight)
            {
                continue;
            }

            overlays.Add(new SelectionOverlayWindow(
                BitmapUtilities.Crop(desktop.Image, crop),
                bounds));
        }

        return overlays;
    }

    private void HandleCapture(CaptureOutcome outcome, CaptureAction action)
    {
        try
        {
            AddToHistory(outcome.Image);
        }
        catch (Exception exception)
        {
            ShowError("PixelPin history", $"The image was captured, but local history could not be updated: {exception.Message}");
        }

        if (Settings.CopyAfterCapture || action == CaptureAction.Copy)
        {
            CopyImage(outcome.Image);
        }

        if (action == CaptureAction.Save)
        {
            SaveImage(outcome.Image, outcome.SuggestedFileName);
        }

        if (action == CaptureAction.Pin || Settings.PinAfterCapture)
        {
            ShowSticker(outcome.Image);
        }

        if (action == CaptureAction.Edit)
        {
            OpenEditor(outcome.Image);
        }
    }

    private void CopyImage(BitmapSource image)
    {
        try
        {
            BitmapUtilities.CopyToClipboard(image);
        }
        catch (Exception exception)
        {
            ShowError("Clipboard", $"The image could not be copied: {exception.Message}");
        }
    }

    private void SaveImage(BitmapSource image, string? template = null)
    {
        try
        {
            var saved = BitmapUtilities.SavePng(image, Settings.SaveDirectory, template ?? Settings.FileNameTemplate);
            _tray?.ShowMessage("PixelPin", $"Saved {Path.GetFileName(saved)}");
        }
        catch (Exception exception)
        {
            ShowError("Save capture", $"The image could not be saved: {exception.Message}");
        }
    }

    private void SaveJpegImage(BitmapSource image)
    {
        try
        {
            var saved = BitmapUtilities.SaveJpeg(image, Settings.SaveDirectory, Settings.FileNameTemplate);
            _tray?.ShowMessage("PixelPin", $"Saved {Path.GetFileName(saved)}");
        }
        catch (Exception exception)
        {
            ShowError("Save JPEG", $"The image could not be saved: {exception.Message}");
        }
    }

    private void OpenEditor(BitmapSource image)
    {
        var editor = new ImageEditorWindow(
            image,
            edited => CopyImage(edited),
            edited => SaveImage(edited),
            edited => ShowSticker(edited),
            AddToHistory);
        editor.Show();
        editor.Activate();
    }

    private void ShowSticker(BitmapSource image)
    {
        var sticker = new StickyWindow(
            image,
            Settings.DefaultStickerOpacity,
            CopyImage,
            stickerImage => SaveImage(stickerImage),
            CacheAndShowSticker,
            SaveJpegImage);
        sticker.Closed += (_, _) =>
        {
            _stickers.Remove(sticker);
            if (ReferenceEquals(_lastSticker, sticker))
            {
                _lastSticker = _stickers.LastOrDefault();
            }
            _stickerManager?.Refresh();
        };
        sticker.Activated += (_, _) => _lastSticker = sticker;
        sticker.StickerStateChanged += (_, _) => _stickerManager?.Refresh();
        _stickers.Add(sticker);
        _lastSticker = sticker;
        sticker.Show();
        sticker.Activate();
    }

    private void CacheAndShowSticker(BitmapSource image)
    {
        try
        {
            AddToHistory(image);
        }
        catch (Exception exception)
        {
            ShowError("PixelPin history", $"The image was opened, but local history could not be updated: {exception.Message}");
        }

        ShowSticker(image);
    }

    private void CloseAllStickers()
    {
        foreach (var sticker in _stickers.ToArray())
        {
            sticker.Close();
        }

        _stickers.Clear();
        _lastSticker = null;
        _stickerManager?.Refresh();
    }

    private void AddToHistory(BitmapSource image)
    {
        _historyService.Add(image, Settings.HistoryLimit, Settings.HistoryDiskLimitMegabytes);
    }

    private void CloseSticker(StickyWindow sticker)
    {
        if (_stickers.Contains(sticker))
        {
            sticker.Close();
        }
    }

    private void CloseStickerGroup(string group)
    {
        var normalized = string.IsNullOrWhiteSpace(group) ? "Ungrouped" : group.Trim();
        foreach (var sticker in _stickers
                     .Where(item => string.Equals(item.GroupName, normalized, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            sticker.Close();
        }

        _stickerManager?.Refresh();
    }

    private bool RegisterHotKeys(AppSettings settings, out string error)
    {
        error = string.Empty;
        if (_hotKeys is null)
        {
            return true;
        }

        if (!HotKeyDefinition.TryParse(settings.CaptureHotKey, out var capture, out error)
            || !HotKeyDefinition.TryParse(settings.ToggleMouseThroughHotKey, out var mouseThrough, out error))
        {
            return false;
        }

        if (!_hotKeys.TryRegister(capture, BeginRegionCapture, out error))
        {
            return false;
        }

        if (!_hotKeys.TryRegister(mouseThrough, ToggleLastStickerMouseThrough, out error))
        {
            _hotKeys.Clear();
            return false;
        }

        return true;
    }

    private void ToggleLastStickerMouseThrough()
    {
        if (_lastSticker is null)
        {
            _tray?.ShowMessage("PixelPin", "There is no sticker to toggle.");
            return;
        }

        _lastSticker.SetMouseThrough(!_lastSticker.IsMouseThrough);
        _tray?.ShowMessage(
            "PixelPin",
            _lastSticker.IsMouseThrough
                ? "Sticker is mouse-through. Press the configured shortcut to restore input."
                : "Sticker input restored.");
    }

    private void ShowScrollingCaptureUnavailable()
    {
        MessageBox.Show(
            "Scrolling capture is not enabled in this build. It requires an application-specific, tested scroll path and will not fabricate a stitched result. Use normal region capture instead.",
            "PixelPin scrolling capture",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CaptureSelectedWindow(CaptureWindowDescriptor descriptor)
    {
        try
        {
            HandleCapture(_captureService.CaptureWindow(descriptor), CaptureAction.Pin);
        }
        catch (Exception exception)
        {
            _logService.Error("Selected window capture", exception.Message, exception);
            ShowError("Window capture", exception.Message);
        }
    }

    private void TrySetStartupRegistration(bool enabled)
    {
        try
        {
            _startupService.SetEnabled(enabled);
        }
        catch (Exception exception)
        {
            _tray?.ShowMessage("PixelPin", $"Startup setting could not be changed: {exception.Message}");
        }
    }

    private string BuildDiagnosticReport()
    {
        var entry = Assembly.GetEntryAssembly()?.GetName();
        var report = new StringBuilder();
        report.AppendLine("PixelPin diagnostics");
        report.AppendLine($"Version: {entry?.Version?.ToString() ?? "unknown"}");
        report.AppendLine($"Runtime: {Environment.Version}");
        report.AppendLine($"OS: {Environment.OSVersion.VersionString}");
        report.AppendLine($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine($"Capture shortcut: {Settings.CaptureHotKey}");
        report.AppendLine($"Mouse-through shortcut: {Settings.ToggleMouseThroughHotKey}");
        report.AppendLine($"History items: {_historyService.Items.Count}");
        report.AppendLine($"History cache limit: {Settings.HistoryDiskLimitMegabytes} MB");
        report.AppendLine($"Active stickers: {_stickers.Count}");
        report.AppendLine($"Local data: {AppPaths.Root}");
        report.AppendLine($"Diagnostic log: {_logService.FilePath}");
        report.AppendLine();
        report.AppendLine("Displays:");
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            report.AppendLine($"- {screen.DeviceName}: {screen.Bounds.Width} x {screen.Bounds.Height} at {screen.Bounds.Left},{screen.Bounds.Top}; primary={screen.Primary}");
        }

        report.AppendLine();
        report.AppendLine("Recent local diagnostics:");
        report.Append(_logService.ReadRecent(6000));
        return report.ToString();
    }

    private void OpenLocalDataFolder()
    {
        try
        {
            AppPaths.EnsureDirectories();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _logService.Error("Open local data", exception.Message, exception);
            ShowError("Open local data", exception.Message);
        }
    }

    private void ClearDiagnostics()
    {
        _logService.Clear();
        _logService.Info("Diagnostics", "Local diagnostic log cleared by user.");
    }

    private void RunOnUi(Action action)
    {
        if (_application.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _application.Dispatcher.BeginInvoke(action);
    }

    private static BitmapSource LoadImageFile(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
