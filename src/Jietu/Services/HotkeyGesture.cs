using System.Globalization;

namespace Jietu;

public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string Display)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>
    /// 常见非字母数字键的 VK 与显示名。
    /// 显示名里不能带 '+'，否则会被修饰键分隔符拆开，Format / TryParse 无法往返。
    /// </summary>
    private static readonly (uint VirtualKey, string Name)[] NamedKeys =
    [
        (0x20u, "Space"),
        (0x09u, "Tab"),
        (0x0Du, "Enter"),
        (0x08u, "Backspace"),
        (0x2Du, "Insert"),
        (0x2Eu, "Delete"),
        (0x24u, "Home"),
        (0x23u, "End"),
        (0x21u, "PageUp"),
        (0x22u, "PageDown"),
        (0x26u, "Up"),
        (0x28u, "Down"),
        (0x25u, "Left"),
        (0x27u, "Right"),
        (0xBAu, "Semicolon"),
        (0xBBu, "Equals"),
        (0xBCu, "Comma"),
        (0xBDu, "Minus"),
        (0xBEu, "Period"),
        (0xBFu, "Slash"),
        (0xC0u, "Backtick"),
        (0xDBu, "LeftBracket"),
        (0xDCu, "Backslash"),
        (0xDDu, "RightBracket"),
        (0xDEu, "Quote"),
    ];

    private static readonly Dictionary<uint, string> KeyDisplayNames =
        NamedKeys.ToDictionary(pair => pair.VirtualKey, pair => pair.Name);

    private static readonly Dictionary<string, uint> NamedVirtualKeys =
        NamedKeys.ToDictionary(pair => pair.Name, pair => pair.VirtualKey, StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键不能为空。";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "请至少带上 Alt、Ctrl 或 Win。";
            return false;
        }

        uint mods = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "alt":
                    mods |= ModAlt;
                    break;
                case "ctrl":
                case "control":
                    mods |= ModControl;
                    break;
                case "shift":
                    mods |= ModShift;
                    break;
                case "win":
                case "windows":
                    mods |= ModWin;
                    break;
                default:
                    error = $"无法识别修饰键 {parts[i]}。";
                    return false;
            }
        }

        if ((mods & (ModAlt | ModControl | ModWin)) == 0)
        {
            error = "请至少带上 Alt、Ctrl 或 Win。";
            return false;
        }

        if (!TryParseKey(parts[^1], out var vk))
        {
            error = $"无法识别按键 {parts[^1]}。";
            return false;
        }

        gesture = new HotkeyGesture(mods, vk, Format(mods, vk));
        return true;
    }

    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(virtualKey));
        return string.Join("+", parts);
    }

    public static string? DuplicateMessage(IEnumerable<HotkeyGesture> gestures)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gesture in gestures)
        {
            if (!seen.Add(gesture.Display))
            {
                return $"热键 {gesture.Display} 重复了。";
            }
        }

        return null;
    }

    private static bool TryParseKey(string text, out uint virtualKey)
    {
        virtualKey = 0;
        var key = text.Trim();
        if (key.Length == 0)
        {
            return false;
        }

        if (NamedVirtualKeys.TryGetValue(key, out virtualKey))
        {
            return true;
        }

        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (key.Length is >= 2 and <= 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x6F + fn);
            return true;
        }

        // 兼容旧版配置里的十六进制写法（旧版本对未命名的键输出 "0x20" 这种形式）。
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            && hex is > 0u and <= 0xFFu)
        {
            virtualKey = hex;
            return true;
        }

        return false;
    }

    private static string KeyName(uint virtualKey)
    {
        if (KeyDisplayNames.TryGetValue(virtualKey, out var name))
        {
            return name;
        }

        if (virtualKey is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x6F);
        }

        return "0x" + virtualKey.ToString("X");
    }
}
