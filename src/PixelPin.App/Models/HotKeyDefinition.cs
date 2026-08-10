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
            error = "A shortcut is required.";
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
                        error = $"'{rawPart}' is not a supported key.";
                        return false;
                    }

                    if (key != Key.None)
                    {
                        error = "A shortcut may contain only one non-modifier key.";
                        return false;
                    }

                    key = parsedKey;
                    break;
            }
        }

        if (key == Key.None)
        {
            error = "A shortcut must include a key such as F1 or P.";
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
