using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelPin.Utilities;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// Windows.Graphics.Imaging and System.Windows.Media.Imaging both define BitmapDecoder
// and BitmapFrame. Aliasing the WinRT side keeps the collision local to this file instead
// of forcing fully qualified names through the rest of the project.
using WinRtBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace PixelPin.Services;

/// <summary>Outcome of one recognition attempt.</summary>
public sealed record OcrOutcome(
    bool Succeeded,
    string Text,
    string Reason,
    IReadOnlyList<string> Lines,
    string LanguageTag)
{
    public static OcrOutcome Failure(string reason) => new(false, string.Empty, reason, [], string.Empty);

    /// <summary>True when recognition ran but found no text, which is not an error.</summary>
    public bool IsEmpty => Succeeded && Text.Length == 0;
}

/// <summary>
/// Text recognition through the Windows OCR engine. The platform engine needs an
/// installed recognizer language pack, so every entry point reports whether it can run
/// and why it cannot, rather than returning an empty result that looks like a success.
/// </summary>
public static class OcrService
{
    /// <summary>
    /// The recognizer needs a reasonable amount of text to work with. Very small images
    /// are upscaled first, because the engine downsamples internally and tiny glyphs are
    /// otherwise lost.
    /// </summary>
    public const int MinimumRecognizableWidth = 40;

    /// <summary>Upscale factor applied to small captures before recognition.</summary>
    public const double SmallImageUpscale = 2.0;

    private static IReadOnlyList<OcrLanguage>? _cachedLanguages;

    /// <summary>Installed recognizer languages, or an empty list when the engine is absent.</summary>
    public static IReadOnlyList<OcrLanguage> AvailableLanguages
    {
        get
        {
            if (_cachedLanguages is not null)
            {
                return _cachedLanguages;
            }

            var found = new List<OcrLanguage>();
            try
            {
                foreach (var language in OcrEngine.AvailableRecognizerLanguages)
                {
                    var name = string.IsNullOrWhiteSpace(language.DisplayName)
                        ? language.LanguageTag
                        : language.DisplayName;
                    found.Add(new OcrLanguage(language.LanguageTag, name));
                }
            }
            catch (Exception)
            {
                // A missing or broken OCR component must surface as "unavailable", not
                // as a crash on a settings page.
                _cachedLanguages = [];
                return _cachedLanguages;
            }

            _cachedLanguages = found;
            return _cachedLanguages;
        }
    }

    /// <summary>True when the platform type loaded and at least one recognizer exists.</summary>
    public static bool IsAvailable => AvailableLanguages.Count > 0;

    /// <summary>Why recognition cannot run, or null when it can.</summary>
    public static string? UnavailableReason
    {
        get
        {
            var languages = AvailableLanguages;
            return OcrText.UnavailableReason(platformAvailable: true, languages.Count);
        }
    }

    /// <summary>
    /// Recognizes text in an image. <paramref name="languageTag"/> selects a recognizer;
    /// null or an unknown tag falls back to the user's profile languages.
    /// </summary>
    public static async Task<OcrOutcome> RecognizeAsync(BitmapSource image, string? languageTag = null)
    {
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            return OcrOutcome.Failure("没有可识别的图像。");
        }

        var reason = UnavailableReason;
        if (reason is not null)
        {
            return OcrOutcome.Failure(reason);
        }

        var engine = CreateEngine(languageTag);
        if (engine is null)
        {
            return OcrOutcome.Failure($"无法为语言“{languageTag}”创建 OCR 引擎，请确认该语言的识别组件已安装。");
        }

        try
        {
            var prepared = PrepareForRecognition(image);
            var softwareBitmap = await ToSoftwareBitmapAsync(prepared);

            // The engine requires a bitmap in a supported format; BGRA8 premultiplied is
            // what the decoder produces, but the conversion is explicit so a future
            // decoder change cannot silently produce an unsupported format.
            var converted = softwareBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? softwareBitmap
                : SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var result = await engine.RecognizeAsync(converted);

            // The recognizer reports a line's text with its words space-separated, which
            // is wrong for Chinese: one character per "word" would copy as "截 图 贴 图".
            // Rebuilding each line from its words lets the script pick the separator.
            var lines = new List<string>();
            foreach (var line in result.Lines)
            {
                var words = new List<string>();
                foreach (var word in line.Words)
                {
                    words.Add(word.Text);
                }

                lines.Add(OcrText.JoinWords(words));
            }

            // Lines themselves are joined by the same rule, so a Chinese paragraph has no
            // invented separators while an English one keeps them.
            var separator = OcrText.SeparatorFor(lines);
            var text = OcrText.JoinLines(lines, separator);

            return new OcrOutcome(
                true,
                text,
                string.Empty,
                lines,
                engine.RecognizerLanguage.LanguageTag);
        }
        catch (Exception exception)
        {
            return OcrOutcome.Failure($"文字识别失败：{exception.Message}");
        }
    }

    /// <summary>Picks a recognizer for the requested language, or the profile default.</summary>
    private static OcrEngine? CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
                if (engine is not null)
                {
                    return engine;
                }
            }
            catch (Exception)
            {
                // An invalid tag falls through to the profile languages below.
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }

    /// <summary>
    /// Upscales images that are too small for the recognizer. The engine resamples
    /// internally, so a few dozen pixels of text would otherwise be unreadable.
    /// </summary>
    internal static BitmapSource PrepareForRecognition(BitmapSource image)
    {
        if (image.PixelWidth >= MinimumRecognizableWidth)
        {
            return image;
        }

        var scale = SmallImageUpscale;
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>
    /// Converts a WPF bitmap into the Windows Runtime bitmap the recognizer accepts. The
    /// image is encoded to an in-memory stream and decoded by the WinRT decoder, which is
    /// the only supported bridge between the two bitmap worlds.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(BitmapSource image)
    {
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(image));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var randomAccessStream = stream.AsRandomAccessStream();
        var decoder = await WinRtBitmapDecoder.CreateAsync(randomAccessStream);
        return await decoder.GetSoftwareBitmapAsync();
    }
}
