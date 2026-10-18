using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace PixelPin.Models;

public readonly record struct HotKeyDefinition(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? input, out HotKeyDefinition result, out string error)
    {
        result = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "必须填写快捷键。";
            return false;
        }

        var modifiers = ModifierKeys.None;
        var key = Key.None;
        foreach (var rawPart in input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (!TryParseKeyName(part, out var parsedKey))
                    {
                        error = $"“{rawPart}”不是支持的按键。";
                        return false;
                    }

                    if (key != Key.None)
                    {
                        error = "快捷键只能包含一个非修饰键。";
                        return false;
                    }

                    key = parsedKey;
                    break;
            }
        }

        if (key == Key.None)
        {
            error = "快捷键必须包含一个按键，例如 F1 或 P。";
            return false;
        }

        result = new HotKeyDefinition(modifiers, key);
        return true;
    }

    /// <summary>
    /// Resolves one key name. This accepts exactly what <see cref="FriendlyKeyName"/>
    /// produces, so anything the settings page records can be read back.
    /// </summary>
    private static bool TryParseKeyName(string part, out Key key)
    {
        key = Key.None;
        if (part.Length == 0)
        {
            return false;
        }

        // Digits map to the number row. They must never reach Enum.TryParse, which reads
        // a numeric string as the enum's underlying value: "1" would become Key.Cancel.
        if (part.Length == 1 && part[0] is >= '0' and <= '9')
        {
            key = Key.D0 + (part[0] - '0');
            return true;
        }

        const string numpadPrefix = "小键盘";
        if (part.StartsWith(numpadPrefix, StringComparison.Ordinal)
            && part.Length == numpadPrefix.Length + 1
            && part[^1] is >= '0' and <= '9')
        {
            key = Key.NumPad0 + (part[^1] - '0');
            return true;
        }

        switch (part)
        {
            case "+": key = Key.OemPlus; return true;
            case "-": key = Key.OemMinus; return true;
            case ",": key = Key.OemComma; return true;
            case ".": key = Key.OemPeriod; return true;
            case ";": key = Key.Oem1; return true;
            case "/": key = Key.Oem2; return true;
            case "`": key = Key.Oem3; return true;
            case "[": key = Key.Oem4; return true;
            case "\\": key = Key.Oem5; return true;
            case "]": key = Key.Oem6; return true;
            case "'": key = Key.Oem7; return true;
        }

        // Any other numeric text ("-1", "1.5") would also be read as a raw enum value.
        if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        if (!Enum.TryParse<Key>(part, true, out var parsed) || IsModifierKey(parsed))
        {
            return false;
        }

        key = parsed;
        return true;
    }

    /// <summary>
    /// Validates every configured shortcut and reports the first problem. Used by
    /// the settings page so a bad combination is rejected before Windows is asked
    /// to register it.
    /// </summary>
    public static string? ValidateAll(IReadOnlyList<(string Label, string Value)> slots)
    {
        foreach (var (label, value) in slots)
        {
            if (!TryParse(value, out _, out var error))
            {
                return $"{label}：{error}";
            }
        }

        return FindDuplicate(slots);
    }

    /// <summary>Returns a message when two features share one shortcut, otherwise null.</summary>
    public static string? FindDuplicate(IReadOnlyList<(string Label, string Value)> slots)
    {
        var duplicate = slots
            .GroupBy(slot => slot.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        return duplicate is null
            ? null
            : $"快捷键“{duplicate.Key}”被 {string.Join("、", duplicate.Select(slot => slot.Label))} 同时使用，请改成不同的按键。";
    }

    /// <summary>
    /// Builds a shortcut from a key press captured in the settings page. A modifier-only
    /// press is rejected so the user can hold Ctrl down and then press the real key.
    /// </summary>
    public static bool TryCreate(ModifierKeys modifiers, Key key, out HotKeyDefinition result, out string error)
    {
        result = default;
        error = string.Empty;

        if (IsModifierKey(key))
        {
            error = "请再按一个非修饰键，例如 F1 或 P。";
            return false;
        }

        result = new HotKeyDefinition(modifiers, key);
        return true;
    }

    /// <summary>True for keys that only qualify a combination and never stand alone.</summary>
    public static bool IsModifierKey(Key key)
        => key is Key.None or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;

    /// <summary>
    /// Display text for a captured shortcut, e.g. "Ctrl+Shift+A". A modifier-only press
    /// renders as "Ctrl+…" so the settings page can show what is being held.
    /// </summary>
    public static string Describe(ModifierKeys modifiers, Key key)
        => new HotKeyDefinition(modifiers, key).ToString();

    /// <summary>
    /// Readable name for a key. The raw enum names are poor user-facing text: digits
    /// render as "D1", the numpad as "NumPad1", and punctuation as "OemPlus".
    /// </summary>
    private static string FriendlyKeyName(Key key)
    {
        var value = (int)key;
        if (value >= (int)Key.D0 && value <= (int)Key.D9)
        {
            return (value - (int)Key.D0).ToString();
        }

        if (value >= (int)Key.NumPad0 && value <= (int)Key.NumPad9)
        {
            return "小键盘" + (value - (int)Key.NumPad0);
        }

        return key switch
        {
            Key.OemPlus => "+",
            Key.OemComma => ",",
            Key.OemMinus => "-",
            Key.OemPeriod => ".",
            Key.Oem1 => ";",
            Key.Oem2 => "/",
            Key.Oem3 => "`",
            Key.Oem4 => "[",
            Key.Oem5 => "\\",
            Key.Oem6 => "]",
            Key.Oem7 => "'",
            Key.Space => "Space",
            _ => key.ToString(),
        };
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key == Key.None ? "…" : FriendlyKeyName(Key));
        return string.Join('+', parts);
    }
}
