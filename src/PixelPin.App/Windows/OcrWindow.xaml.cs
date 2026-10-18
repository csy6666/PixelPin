using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PixelPin.Services;
using PixelPin.Utilities;

namespace PixelPin.Windows;

/// <summary>
/// Shows recognised text and lets the user copy it. The engine's own output is kept
/// editable, because recognition of a small or low-contrast capture can misread a
/// character and the user is the only one who knows what it should say.
/// </summary>
public partial class OcrWindow : Window
{
    private readonly BitmapSource _source;
    private readonly Action<string> _copy;
    private bool _loading = true;
    private bool _recognizing;

    public OcrWindow(BitmapSource source, Action<string> copy)
    {
        InitializeComponent();
        _source = source;
        _copy = copy;

        var languages = OcrService.AvailableLanguages;
        LanguagePicker.ItemsSource = languages;
        if (languages.Count > 0)
        {
            // Prefer a recognizer that matches the interface language, which is the most
            // likely intent for a Chinese-language desktop.
            LanguagePicker.SelectedItem = languages.FirstOrDefault(language =>
                language.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ?? languages[0];
        }

        _loading = false;
        Loaded += (_, _) => Recognize();
    }

    /// <summary>Text currently shown, including any edits the user made.</summary>
    public string RecognizedText => ResultBox.Text;

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IsLoaded)
        {
            Recognize();
        }
    }

    private void Recognize_Click(object sender, RoutedEventArgs e) => Recognize();

    private async void Recognize()
    {
        if (_recognizing)
        {
            return;
        }

        var reason = OcrService.UnavailableReason;
        if (reason is not null)
        {
            StatusText.Text = "无法识别";
            ResultBox.Text = reason;
            ResultBox.IsReadOnly = true;
            HintText.Text = "系统缺少 OCR 组件或语言包，PixelPin 不会生成猜测的结果。";
            return;
        }

        _recognizing = true;
        StatusText.Text = "正在识别...";
        HintText.Text = "识别中，请稍候。";

        try
        {
            var tag = (LanguagePicker.SelectedItem as OcrLanguage)?.Tag;
            var outcome = await OcrService.RecognizeAsync(_source, tag);

            if (!outcome.Succeeded)
            {
                StatusText.Text = "识别失败";
                ResultBox.Text = outcome.Reason;
                ResultBox.IsReadOnly = true;
                HintText.Text = "识别未完成，请检查语言组件后重试。";
                return;
            }

            ResultBox.IsReadOnly = false;
            ResultBox.Text = outcome.Text;
            StatusText.Text = outcome.IsEmpty
                ? $"未在图中找到文字（语言 {outcome.LanguageTag}）"
                : $"已识别 {outcome.Text.Length} 个字符，{outcome.Lines.Count} 行（语言 {outcome.LanguageTag}）";
            HintText.Text = outcome.IsEmpty
                ? "这张图里没有可识别的文字。可以换一种识别语言再试。"
                : "识别结果可以直接编辑，修改后再复制。";
        }
        catch (Exception exception)
        {
            StatusText.Text = "识别失败";
            ResultBox.Text = $"识别过程中出现异常：{exception.Message}";
            ResultBox.IsReadOnly = true;
        }
        finally
        {
            _recognizing = false;
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => Copy(ResultBox.Text, multiline: true);

    private void CopySingleLine_Click(object sender, RoutedEventArgs e)
        => Copy(OcrText.Normalize(ResultBox.Text), multiline: false);

    private void Copy(string text, bool multiline)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "没有可复制的内容。";
            return;
        }

        try
        {
            _copy(text);
            StatusText.Text = multiline ? "已复制全部文字。" : "已复制为一行。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制失败：{exception.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
