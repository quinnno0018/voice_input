using System.Windows.Forms;

namespace VoiceInput.Desktop.Hotkey;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static HotkeyGesture Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var modifiers = HotkeyModifiers.None;
        Keys? key = null;

        foreach (var rawPart in value.Split(
                     '+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (key is not null
                        || !Enum.TryParse<Keys>(rawPart, true, out var parsed)
                        || parsed is Keys.None)
                    {
                        throw new FormatException(
                            $"Invalid hotkey '{value}'. Example: Ctrl+Alt+Space.");
                    }

                    key = parsed;
                    break;
            }
        }

        if (key is null)
        {
            throw new FormatException(
                $"Invalid hotkey '{value}'. A non-modifier key is required.");
        }

        return new HotkeyGesture(modifiers, (uint)key.Value);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }
        parts.Add(((Keys)VirtualKey).ToString());
        return string.Join("+", parts);
    }
}
