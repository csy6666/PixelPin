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
        _logService.Info("应用程序", "控制器已初始化。");
    }

    public AppSettings Settings { get; private set; }

    public void Start(IReadOnlyCollection<string>? launchArguments = null)
    {
        AppPaths.EnsureDirectories();
        _logService.Info("应用程序", "PixelPin 已启动。");
        _hotKeys = new HotKeyService();
        var skipHotKeys = launchArguments?.Contains("--no-hotkeys", StringComparer.OrdinalIgnoreCase) == true;
        if (!skipHotKeys && !RegisterHotKeys(Settings, out var hotKeyError))
        {
            ShowError("PixelPin 快捷键设置", hotKeyError);
        }
        else if (skipHotKeys)
        {
            _logService.Info("应用程序", "已通过启动参数禁用全局快捷键。");
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
        _logService.Error("未处理的异常", exception.Message, exception);
    }

    public string? ApplySettings(AppSettings candidate)
    {
        candidate.Normalize();

        if (!HotKeyDefinition.TryParse(candidate.CaptureHotKey, out _, out var captureError))
        {
            return $"截图快捷键：{captureError}";
        }

        if (!HotKeyDefinition.TryParse(candidate.ToggleMouseThroughHotKey, out _, out var mouseThroughError))
        {
            return $"鼠标穿透快捷键：{mouseThroughError}";
        }

        var previous = Settings;
        _hotKeys?.Clear();
        if (!RegisterHotKeys(candidate, out var registrationError))
        {
            _hotKeys?.Clear();
            RegisterHotKeys(previous, out _);
            return $"无法注册全局快捷键，可能已被其他程序占用。Windows 返回：{registrationError}";
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
                ShowError("PixelPin 截图", "Windows 没有报告可截图的显示器。");
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
            ShowError("PixelPin 截图", exception.Message);
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
            ShowError("当前窗口截图", exception.Message);
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
                ShowError("粘贴图片", "剪贴板中没有图片。");
                return;
            }

            var image = Clipboard.GetImage();
            if (image is null)
            {
                ShowError("粘贴图片", "Windows 无法读取剪贴板中的图片。");
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
            ShowError("粘贴图片", exception.Message);
        }
    }

    public void OpenImageFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开图片作为贴图",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
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
                ShowError("打开图片", $"无法打开 {Path.GetFileName(file)}：{exception.Message}");
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
        _logService.Info("应用程序", "PixelPin 正在退出。");
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
            ShowError("PixelPin 历史记录", $"图片已截取，但无法更新本地历史记录：{exception.Message}");
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
            ShowError("剪贴板", $"无法复制图片：{exception.Message}");
        }
    }

    private void SaveImage(BitmapSource image, string? template = null)
    {
        try
        {
            var saved = BitmapUtilities.SavePng(image, Settings.SaveDirectory, template ?? Settings.FileNameTemplate);
            _tray?.ShowMessage("PixelPin", $"已保存 {Path.GetFileName(saved)}");
        }
        catch (Exception exception)
        {
            ShowError("保存截图", $"无法保存图片：{exception.Message}");
        }
    }

    private void SaveJpegImage(BitmapSource image)
    {
        try
        {
            var saved = BitmapUtilities.SaveJpeg(image, Settings.SaveDirectory, Settings.FileNameTemplate);
            _tray?.ShowMessage("PixelPin", $"已保存 {Path.GetFileName(saved)}");
        }
        catch (Exception exception)
        {
            ShowError("保存 JPEG", $"无法保存图片：{exception.Message}");
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
            ShowError("PixelPin 历史记录", $"图片已打开，但无法更新本地历史记录：{exception.Message}");
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
            _tray?.ShowMessage("PixelPin", "没有可切换的贴图。");
            return;
        }

        _lastSticker.SetMouseThrough(!_lastSticker.IsMouseThrough);
        _tray?.ShowMessage(
            "PixelPin",
            _lastSticker.IsMouseThrough
                ? "贴图已开启鼠标穿透。按设置的快捷键可恢复输入。"
                : "贴图输入已恢复。");
    }

    private void ShowScrollingCaptureUnavailable()
    {
        MessageBox.Show(
            "此版本未启用滚动截图。该功能需要针对应用测试滚动路径，无法验证时不会伪造拼接结果。请改用普通区域截图。",
            "PixelPin 滚动截图",
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
            _logService.Error("选择窗口截图", exception.Message, exception);
            ShowError("窗口截图", exception.Message);
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
            _tray?.ShowMessage("PixelPin", $"无法修改开机启动设置：{exception.Message}");
        }
    }

    private string BuildDiagnosticReport()
    {
        var entry = Assembly.GetEntryAssembly()?.GetName();
        var report = new StringBuilder();
        report.AppendLine("PixelPin 诊断信息");
        report.AppendLine($"版本：{entry?.Version?.ToString() ?? "未知"}");
        report.AppendLine($"运行时：{Environment.Version}");
        report.AppendLine($"操作系统：{Environment.OSVersion.VersionString}");
        report.AppendLine($"进程架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine($"截图快捷键：{Settings.CaptureHotKey}");
        report.AppendLine($"鼠标穿透快捷键：{Settings.ToggleMouseThroughHotKey}");
        report.AppendLine($"历史记录数量：{_historyService.Items.Count}");
        report.AppendLine($"历史缓存上限：{Settings.HistoryDiskLimitMegabytes} MB");
        report.AppendLine($"当前贴图数量：{_stickers.Count}");
        report.AppendLine($"本地数据：{AppPaths.Root}");
        report.AppendLine($"诊断日志：{_logService.FilePath}");
        report.AppendLine();
        report.AppendLine("显示器：");
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            report.AppendLine($"- {screen.DeviceName}：{screen.Bounds.Width} x {screen.Bounds.Height}，位置 {screen.Bounds.Left},{screen.Bounds.Top}；主显示器={screen.Primary}");
        }

        report.AppendLine();
        report.AppendLine("最近的本地诊断记录：");
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
            _logService.Error("打开本地数据", exception.Message, exception);
            ShowError("打开本地数据", exception.Message);
        }
    }

    private void ClearDiagnostics()
    {
        _logService.Clear();
        _logService.Info("诊断", "用户已清除本地诊断日志。");
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
