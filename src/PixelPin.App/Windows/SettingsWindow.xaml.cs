using System.Windows;
using Microsoft.Win32;
using PixelPin.Models;

namespace PixelPin.Windows;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, string?> _save;
    private readonly Func<string, AppSettings> _import;
    private readonly Action<AppSettings, string> _export;
    private readonly Action _showDiagnostics;

    public SettingsWindow(
        AppSettings settings,
        Func<AppSettings, string?> save,
        Func<string, AppSettings> import,
        Action<AppSettings, string> export,
        Action showDiagnostics)
    {
        InitializeComponent();
        _save = save;
        _import = import;
        _export = export;
        _showDiagnostics = showDiagnostics;

        Populate(settings);
    }

    private void Populate(AppSettings settings)
    {
        CaptureHotKeyBox.Text = settings.CaptureHotKey;
        MouseThroughHotKeyBox.Text = settings.ToggleMouseThroughHotKey;
        SaveDirectoryBox.Text = settings.SaveDirectory;
        FileNameTemplateBox.Text = settings.FileNameTemplate;
        OpacitySlider.Value = settings.DefaultStickerOpacity;
        HistoryLimitBox.Text = settings.HistoryLimit.ToString();
        HistoryDiskLimitBox.Text = settings.HistoryDiskLimitMegabytes.ToString();
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        CopyAfterCaptureBox.IsChecked = settings.CopyAfterCapture;
        PinAfterCaptureBox.IsChecked = settings.PinAfterCapture;
        ShowOpacity();
    }

    private void BrowseSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            InitialDirectory = Directory.Exists(SaveDirectoryBox.Text) ? SaveDirectoryBox.Text : string.Empty,
            Description = "选择导出截图的文件夹。",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SaveDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowOpacity();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = BuildSettings();
        if (settings is null)
        {
            return;
        }

        var error = _save(settings);
        if (error is not null)
        {
            StatusText.Text = error;
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 PixelPin 设置",
            Filter = "PixelPin 设置|*.json|所有文件|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Populate(_import(dialog.FileName));
            StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
            StatusText.Text = "设置已加载。请检查内容后点击“保存设置”应用。";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = $"无法导入设置：{exception.Message}";
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var settings = BuildSettings();
        if (settings is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 PixelPin 设置",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            FileName = "pixelpin-settings.json",
            AddExtension = true,
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _export(settings, dialog.FileName);
            StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
            StatusText.Text = $"设置已导出到 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = $"无法导出设置：{exception.Message}";
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        _showDiagnostics();
    }

    private AppSettings? BuildSettings()
    {
        if (!int.TryParse(HistoryLimitBox.Text, out var historyLimit)
            || !int.TryParse(HistoryDiskLimitBox.Text, out var historyDiskLimit))
        {
            StatusText.Text = "历史记录数量和缓存上限必须是整数。";
            return null;
        }

        return new AppSettings
        {
            CaptureHotKey = CaptureHotKeyBox.Text,
            ToggleMouseThroughHotKey = MouseThroughHotKeyBox.Text,
            SaveDirectory = SaveDirectoryBox.Text,
            FileNameTemplate = FileNameTemplateBox.Text,
            DefaultStickerOpacity = OpacitySlider.Value,
            HistoryLimit = historyLimit,
            HistoryDiskLimitMegabytes = historyDiskLimit,
            StartWithWindows = StartWithWindowsBox.IsChecked == true,
            CopyAfterCapture = CopyAfterCaptureBox.IsChecked == true,
            PinAfterCapture = PinAfterCaptureBox.IsChecked == true,
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowOpacity()
    {
        if (OpacityValue is not null)
        {
            OpacityValue.Text = $"{OpacitySlider.Value:P0}";
        }
    }
}
