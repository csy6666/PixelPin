using System.Globalization;
using System.Text;

namespace PixelPin.Utilities;

/// <summary>One installed OCR recognizer, as shown in the language picker.</summary>
public sealed record OcrLanguage(string Tag, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Text shaping for OCR output. The Windows recognizer returns lines with their own
/// spacing, and Chinese text arrives without separators while Latin text needs them, so
/// the joining rule is not simply "concatenate". This is kept free of Windows Runtime
/// types so it can be unit tested without an OCR language pack.
/// </summary>
public static class OcrText
{
    /// <summary>Joins recognised lines into a single line, dropping empties.</summary>
    public static string JoinLines(IReadOnlyList<string>? lines, string separator = " ")
    {
        if (lines is null || lines.Count == 0)
        {
            return string.Empty;
        }

        var kept = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                kept.Add(trimmed);
            }
        }

        return string.Join(separator, kept);
    }

    /// <summary>
    /// Joins the words of one recognised line, inserting a space only where the text
    /// itself had one. The recognizer splits Chinese into one word per character and
    /// reports the line with spaces between them, so using its word text verbatim would
    /// copy "截 图 贴 图" instead of "截图贴图". Latin words genuinely need the space.
    /// <para>
    /// The decision is made per adjacent pair rather than per line, because a line of
    /// mixed script has no single right answer: "截图 tool" must lose the space between the
    /// two Chinese characters while keeping none before the Latin word, which a per-line
    /// threshold cannot express.
    /// </para>
    /// </summary>
    public static string JoinWords(IReadOnlyList<string>? words)
    {
        if (words is null || words.Count == 0)
        {
            return string.Empty;
        }

        var kept = new List<string>();
        foreach (var word in words)
        {
            var trimmed = word?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                kept.Add(trimmed);
            }
        }

        if (kept.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < kept.Count; index++)
        {
            if (index > 0 && NeedsSeparator(kept[index - 1], kept[index]))
            {
                builder.Append(' ');
            }

            builder.Append(kept[index]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when two adjacent words were separated in the original text. A separator is
    /// only certain when both sides are non-CJK: two Chinese characters are never written
    /// with a space between them, and the boundary between scripts is left as the
    /// recognizer found it.
    /// </summary>
    private static bool NeedsSeparator(string previous, string next)
        => previous.Length > 0
            && next.Length > 0
            && !IsCjk(previous[^1])
            && !IsCjk(next[0]);

    /// <summary>Assembles recognised lines into a paragraph, dropping blank lines.</summary>
    public static string Assemble(IReadOnlyList<string>? lines)
        => JoinLines(lines, Environment.NewLine);

    /// <summary>
    /// True when the text contains CJK characters. Chinese and Japanese recognisers do
    /// not put spaces between words, so joining their lines with a space would insert
    /// separators the user never wrote.
    /// </summary>
    public static bool IsCjkDominated(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var cjk = 0;
        var letters = 0;
        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                cjk++;
                letters++;
            }
            else if (char.IsLetterOrDigit(character))
            {
                letters++;
            }
        }

        // Half is a deliberate threshold: mixed text still reads better without added
        // spaces, while a mostly-Latin capture keeps them.
        return letters > 0 && cjk * 2 >= letters;
    }

    /// <summary>Chooses the separator that suits the script the text is written in.</summary>
    public static string SeparatorFor(IReadOnlyList<string>? lines)
        => IsCjkDominated(JoinLines(lines, string.Empty)) ? string.Empty : " ";

    /// <summary>Collapses runs of whitespace and trims, for comparing recognised text.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Compares recognised text against expected text, ignoring whitespace, case, and
    /// full-width punctuation differences. Used by the desktop probe to assert that a
    /// rendered string survives the round trip through the real recognizer.
    /// </summary>
    public static bool Matches(string? recognized, string? expected)
    {
        var left = StripForComparison(recognized);
        var right = StripForComparison(expected);
        return left.Length > 0 && left == right;
    }

    /// <summary>
    /// True when the recognised text contains the expected text, ignoring the same
    /// differences as <see cref="Matches"/>. Recognition of a small image can drop a
    /// trailing character, so containment is the fairer assertion for a probe.
    /// </summary>
    public static bool Contains(string? recognized, string? expected)
    {
        var left = StripForComparison(recognized);
        var right = StripForComparison(expected);
        return right.Length > 0 && left.Contains(right, StringComparison.Ordinal);
    }

    /// <summary>Fraction of expected characters present in the recognised text, 0 to 1.</summary>
    public static double Similarity(string? recognized, string? expected)
    {
        var left = StripForComparison(recognized);
        var right = StripForComparison(expected);
        if (right.Length == 0)
        {
            return left.Length == 0 ? 1 : 0;
        }

        // Longest common subsequence, so a single misread character in the middle does
        // not collapse the score the way a prefix comparison would.
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        foreach (var outer in left)
        {
            for (var index = 1; index <= right.Length; index++)
            {
                current[index] = outer == right[index - 1]
                    ? previous[index - 1] + 1
                    : Math.Max(previous[index], current[index - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[right.Length] / (double)right.Length;
    }

    private static string StripForComparison(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            // Full-width forms and punctuation differ between what is typed and what a
            // recognizer reports, so both sides are folded to their canonical form.
            var normalized = character.ToString().Normalize(NormalizationForm.FormKC);
            foreach (var folded in normalized)
            {
                if (char.IsWhiteSpace(folded))
                {
                    continue;
                }

                builder.Append(char.ToUpperInvariant(folded));
            }
        }

        return builder.ToString();
    }

    private static bool IsCjk(char character)
        => character is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uAC00' and <= '\uD7AF'
            or >= '\uF900' and <= '\uFAFF';

    /// <summary>Formats the language list for a diagnostics or settings readout.</summary>
    public static string DescribeLanguages(IReadOnlyList<OcrLanguage>? languages)
        => languages is null || languages.Count == 0
            ? "未安装任何 OCR 识别语言包"
            : string.Join("、", languages.Select(language => language.DisplayName));

    /// <summary>
    /// Human-readable reason OCR cannot run, given whether the platform type loaded and
    /// how many recognizers exist. Returning a specific reason is what keeps the feature
    /// from silently degrading into a fake result.
    /// </summary>
    public static string? UnavailableReason(bool platformAvailable, int languageCount)
    {
        if (!platformAvailable)
        {
            return "当前系统没有可用的 Windows OCR 组件，无法识别文字。";
        }

        return languageCount == 0
            ? "系统尚未安装任何 OCR 识别语言包。请在“设置 → 时间和语言 → 语言和区域”中为需要的语言添加“可选功能 → 光学字符识别”。"
            : null;
    }
}
