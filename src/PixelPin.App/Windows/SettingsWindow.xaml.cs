using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PixelPin.Models;

namespace PixelPin.Windows;

public partial class SettingsWindow : Window
{
    private readonly Func<AppSettings, string?> _save;
    private readonly Func<string, AppSettings> _import;
    private readonly Action<AppSettings, string> _export;
    private readonly Action _showDiagnostics;
    private string _annotationColor = AppSettings.FallbackAnnotationColor;

    /// <summary>
    /// The theme in effect when the window opened, so Cancel can put it back after
    /// the picker has already previewed another one.
    /// </summary>
    private readonly Services.AppTheme _originalTheme;

    /// <summary>Suppresses the live preview while the fields are being populated.</summary>
    private bool _populating;

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
        // Cancel undoes the live preview, so the value to restore is the theme that was
        // actually on screen when the window opened, not a re-parse of the saved setting.
        // In the app these are the same value; keeping the live one means the undo can
        // never apply a theme the user was not already looking at.
        _originalTheme = Services.ThemeService.Preference;

        Populate(settings);
    }

    private void Populate(AppSettings settings)
    {
        _populating = true;
        CaptureHotKeyBox.Text = settings.CaptureHotKey;
        CapturePinHotKeyBox.Text = settings.CapturePinHotKey;
        CaptureFullScreenHotKeyBox.Text = settings.CaptureFullScreenHotKey;
        PasteStickerHotKeyBox.Text = settings.PasteStickerHotKey;
        MouseThroughHotKeyBox.Text = settings.ToggleMouseThroughHotKey;
        CloseAllHotKeyBox.Text = settings.CloseAllStickersHotKey;
        SaveDirectoryBox.Text = settings.SaveDirectory;
        FileNameTemplateBox.Text = settings.FileNameTemplate;
        OpacitySlider.Value = settings.DefaultStickerOpacity;
        HistoryLimitBox.Text = settings.HistoryLimit.ToString();
        HistoryDiskLimitBox.Text = settings.HistoryDiskLimitMegabytes.ToString();
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        CopyAfterCaptureBox.IsChecked = settings.CopyAfterCapture;
        PinAfterCaptureBox.IsChecked = settings.PinAfterCapture;
        SnapToWindowEdgesBox.IsChecked = settings.SnapToWindowEdges;
        HighlightWindowOnHoverBox.IsChecked = settings.HighlightWindowOnHover;
        StickerKeepAspectBox.IsChecked = settings.StickerKeepAspectRatio;
        StickerDoubleClickCloseBox.IsChecked = settings.StickerCloseOnDoubleClick;
        StrokeWidthSlider.Value = settings.DefaultAnnotationStrokeWidth;
        SetAnnotationColor(settings.DefaultAnnotationColor);
        SelectTheme(settings.Theme);
        ShowOpacity();
        _populating = false;
    }

    /// <summary>Selects the stored theme without firing the live preview.</summary>
    private void SelectTheme(string? theme)
    {
        var wanted = Services.ThemeService.Parse(theme);
        foreach (var item in ThemePicker.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && Services.ThemeService.Parse(tag) == wanted)
            {
                ThemePicker.SelectedItem = item;
                return;
            }
        }

        ThemePicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Applies the picked theme immediately so the user sees the result on this very
    /// window. Nothing is written to disk here; Save persists the choice and Cancel
    /// restores the previous one.
    /// </summary>
    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || ThemePicker.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        Services.ThemeService.Apply(Services.ThemeService.Parse(tag));
    }

    /// <summary>Value each shortcut field held when it gained focus, for Esc to restore.</summary>
    private readonly Dictionary<TextBox, string> _hotKeyBackup = new();

    private void HotKeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            _hotKeyBackup[box] = box.Text;
        }
    }

    /// <summary>
    /// Records the pressed combination into the focused shortcut field. Every key is
    /// handled except Tab, so typed characters can never corrupt a shortcut name.
    /// </summary>
    private void HotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        // Tab and Shift+Tab must keep moving focus between fields.
        if (e.Key == Key.Tab)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                box.Text = _hotKeyBackup.TryGetValue(box, out var previous) ? previous : box.Text;
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                return;

            case Key.Back:
            case Key.Delete:
                box.Clear();
                e.Handled = true;
                return;
        }

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        box.Text = HotKeyDefinition.TryCreate(modifiers, key, out var definition, out _)
            ? definition.ToString()
            : HotKeyDefinition.Describe(modifiers, Key.None);

        e.Handled = true;
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

    private void PickAnnotationColor_Click(object sender, RoutedEventArgs e)
    {
        var current = ParseColor(_annotationColor);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        SetAnnotationColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        defaults.Normalize();
        defaults.SaveDirectory = SaveDirectoryBox.Text;
        Populate(defaults);
        StatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        StatusText.Text = "已填入默认值，保存文件夹保持不变。点击“保存设置”应用。";
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
            // The save was rejected, so the previewed theme must not stay in effect.
            Services.ThemeService.Apply(_originalTheme);
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            StatusText.Text = error;
            return;
        }

        // Settings is opened modelessly by AppController.ShowSettings(), so it has no
        // dialog result to set. Assigning DialogResult here would throw at runtime.
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
            StatusText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusText.Text = "设置已加载。请检查内容后点击“保存设置”应用。";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
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
            StatusText.Foreground = (Brush)FindResource("SuccessBrush");
            StatusText.Text = $"设置已导出到 {Path.GetFileName(dialog.FileName)}。";
        }
        catch (Exception exception)
        {
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
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
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            StatusText.Text = "历史记录数量和缓存上限必须是整数。";
            return null;
        }

        return new AppSettings
        {
            CaptureHotKey = CaptureHotKeyBox.Text,
            CapturePinHotKey = CapturePinHotKeyBox.Text,
            CaptureFullScreenHotKey = CaptureFullScreenHotKeyBox.Text,
            PasteStickerHotKey = PasteStickerHotKeyBox.Text,
            ToggleMouseThroughHotKey = MouseThroughHotKeyBox.Text,
            CloseAllStickersHotKey = CloseAllHotKeyBox.Text,
            SaveDirectory = SaveDirectoryBox.Text,
            FileNameTemplate = FileNameTemplateBox.Text,
            DefaultStickerOpacity = OpacitySlider.Value,
            DefaultAnnotationColor = _annotationColor,
            DefaultAnnotationStrokeWidth = StrokeWidthSlider.Value,
            HistoryLimit = historyLimit,
            HistoryDiskLimitMegabytes = historyDiskLimit,
            StartWithWindows = StartWithWindowsBox.IsChecked == true,
            CopyAfterCapture = CopyAfterCaptureBox.IsChecked == true,
            PinAfterCapture = PinAfterCaptureBox.IsChecked == true,
            SnapToWindowEdges = SnapToWindowEdgesBox.IsChecked == true,
            HighlightWindowOnHover = HighlightWindowOnHoverBox.IsChecked == true,
            StickerKeepAspectRatio = StickerKeepAspectBox.IsChecked == true,
            StickerCloseOnDoubleClick = StickerDoubleClickCloseBox.IsChecked == true,
            Theme = ThemePicker.SelectedItem is ComboBoxItem { Tag: string theme } ? theme : "System",
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // The picker previewed its selection as soon as it changed, so cancelling has
        // to undo that preview or the window would keep a theme that was never saved.
        Services.ThemeService.Apply(_originalTheme);
        Close();
    }

    private void ShowOpacity()
    {
        if (OpacityValue is not null)
        {
            OpacityValue.Text = $"{OpacitySlider.Value:P0}";
        }
    }

    private void SetAnnotationColor(string hex)
    {
        _annotationColor = hex;
        if (AnnotationColorPreview is not null)
        {
            AnnotationColorPreview.Fill = new SolidColorBrush(ParseColor(hex));
        }
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return Colors.Red;
        }
        catch (NotSupportedException)
        {
            return Colors.Red;
        }
    }
}
