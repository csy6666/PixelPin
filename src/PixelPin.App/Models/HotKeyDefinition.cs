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
                    if (!Enum.TryParse<Key>(rawPart, true, out var parsedKey)
                        || parsedKey is Key.None or Key.LeftCtrl or Key.RightCtrl
                            or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                            or Key.LWin or Key.RWin)
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

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }
}
