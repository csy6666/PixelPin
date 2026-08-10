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
            Description = "Choose the folder for exported screenshots.",
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
            Title = "Import PixelPin settings",
            Filter = "PixelPin settings|*.json|All files|*.*",
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
            StatusText.Text = "Settings loaded. Review them, then choose Save settings to apply.";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = $"Settings could not be imported: {exception.Message}";
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
            Title = "Export PixelPin settings",
            Filter = "JSON files|*.json|All files|*.*",
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
            StatusText.Text = $"Settings exported to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = $"Settings could not be exported: {exception.Message}";
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
            StatusText.Text = "History entries and cache limit must be whole numbers.";
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
