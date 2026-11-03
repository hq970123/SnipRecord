namespace SnipRecord.ExtensionApp.Services.Hotkeys;

/// <summary>Modifier flags; values equal the Win32 MOD_* constants.</summary>
[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

public enum HotkeyAction { Snip, FullScreen, RecordRectangle, RecordWindow, ColorPick, OpenEditorOnLastCapture }

/// <summary>A global hotkey: modifiers + Win32 virtual-key code. <c>default</c> = none.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly Hotkey None = default;

    public bool IsEmpty => VirtualKey == 0;

    public static bool IsFunctionKey(int vk) => vk is >= 0x70 and <= 0x87; // F1–F24

    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>Registrable: a non-modifier key with Ctrl, Alt or Win (Shift alone is not enough), or any F1–F24.</summary>
    public bool IsValid =>
        VirtualKey is > 0 and < 0xFF && !IsModifierKey(VirtualKey) &&
        ((Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0 || IsFunctionKey(VirtualKey));

    /// <summary>"Ctrl+Alt+A"; "" when empty.</summary>
    public override string ToString()
    {
        if (IsEmpty) return string.Empty;
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>Like ToString but "未设置" when empty.</summary>
    public string ToDisplayString() => IsEmpty ? "未设置" : ToString();

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        var vk = 0;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length == 0)
            {
                // "Ctrl++" → the '+' key itself
                if (i == tokens.Length - 1 && vk == 0) { vk = 0xBB; continue; }
                if (i == tokens.Length - 2 && tokens[^1].Length == 0) continue;
                return false;
            }
            switch (t.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL" or "CTL": mods |= HotkeyModifiers.Ctrl; continue;
                case "ALT" or "MENU": mods |= HotkeyModifiers.Alt; continue;
                case "SHIFT": mods |= HotkeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS" or "META" or "SUPER": mods |= HotkeyModifiers.Win; continue;
            }
            if (vk != 0) return false;
            vk = ParseKey(t);
            if (vk == 0) return false;
        }
        if (vk == 0) return false;
        hotkey = new Hotkey(mods, vk);
        return true;
    }

    private static readonly Dictionary<int, string> Names = BuildNames();

    private static Dictionary<int, string> BuildNames()
    {
        var d = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x14] = "CapsLock", [0x1B] = "Esc",
            [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x2C] = "PrintScreen",
            [0x2D] = "Insert", [0x2E] = "Delete", [0x6A] = "NumMultiply", [0x6B] = "NumAdd", [0x6D] = "NumSubtract",
            [0x6E] = "NumDecimal", [0x6F] = "NumDivide", [0x90] = "NumLock", [0x91] = "ScrollLock",
            [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
            [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
        };
        for (var c = '0'; c <= '9'; c++) d[c] = c.ToString();
        for (var c = 'A'; c <= 'Z'; c++) d[c] = c.ToString();
        for (var i = 0; i <= 9; i++) d[0x60 + i] = "Num" + i;
        for (var i = 1; i <= 24; i++) d[0x6F + i] = "F" + i;
        return d;
    }

    public static string KeyName(int vk) => Names.TryGetValue(vk, out var n) ? n : $"VK{vk:X2}";

    private static int ParseKey(string token)
    {
        foreach (var (vk, name) in Names)
            if (string.Equals(name, token, StringComparison.OrdinalIgnoreCase)) return vk;
        var u = token.ToUpperInvariant();
        return u switch
        {
            "ESCAPE" => 0x1B, "RETURN" => 0x0D, "DEL" => 0x2E, "INS" => 0x2D, "PRTSC" or "PRINT" or "SNAPSHOT" => 0x2C,
            "PGUP" => 0x21, "PGDN" => 0x22, "PLUS" => 0xBB, "MINUS" => 0xBD, "COMMA" => 0xBC, "PERIOD" => 0xBE,
            _ when u.StartsWith("VK") && int.TryParse(u.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var v) && v is > 0 and < 0xFF => v,
            _ => 0,
        };
    }
}

/// <summary>Two or more actions bound to the same hotkey.</summary>
public sealed record HotkeyConflict(Hotkey Hotkey, IReadOnlyList<HotkeyAction> Actions);

/// <summary>Action → hotkey map (<see cref="Hotkey.None"/> = disabled).</summary>
public sealed class HotkeyBindings
{
    private readonly Dictionary<HotkeyAction, Hotkey> _map = new();

    public static IReadOnlyList<HotkeyAction> AllActions { get; } = Enum.GetValues<HotkeyAction>();

    public static Hotkey GetDefault(HotkeyAction action) => action switch
    {
        HotkeyAction.Snip => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'A'),
        HotkeyAction.FullScreen => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'F'),
        HotkeyAction.RecordRectangle => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'R'),
        HotkeyAction.RecordWindow => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'V'),
        HotkeyAction.ColorPick => new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'C'),
        _ => Hotkey.None,
    };

    /// <summary>A fresh copy of the default bindings.</summary>
    public static HotkeyBindings Defaults
    {
        get
        {
            var b = new HotkeyBindings();
            foreach (var a in AllActions) b[a] = GetDefault(a);
            return b;
        }
    }

    public Hotkey this[HotkeyAction action]
    {
        get => _map.TryGetValue(action, out var h) ? h : Hotkey.None;
        set => _map[action] = value;
    }

    public IReadOnlyDictionary<HotkeyAction, Hotkey> All => AllActions.ToDictionary(a => a, a => this[a]);

    public HotkeyBindings Clone()
    {
        var b = new HotkeyBindings();
        foreach (var (a, h) in _map) b[a] = h;
        return b;
    }

    /// <summary>Keys are <see cref="HotkeyAction"/> names. Missing key = default, "" = disabled, unparsable = default.</summary>
    public static HotkeyBindings FromSettingsMap(IReadOnlyDictionary<string, string>? map)
    {
        var b = Defaults;
        if (map is null) return b;
        foreach (var a in AllActions)
        {
            if (!map.TryGetValue(a.ToString(), out var text)) continue;
            if (string.IsNullOrWhiteSpace(text)) b[a] = Hotkey.None;
            else if (Hotkey.TryParse(text, out var h)) b[a] = h;
        }
        return b;
    }

    public Dictionary<string, string> ToSettingsMap() => AllActions.ToDictionary(a => a.ToString(), a => this[a].ToString());

    public IReadOnlyList<HotkeyConflict> FindConflicts() =>
        AllActions.Where(a => !this[a].IsEmpty).GroupBy(a => this[a]).Where(g => g.Count() > 1)
            .Select(g => new HotkeyConflict(g.Key, g.ToArray())).ToArray();

    public static string GetDisplayName(HotkeyAction action) => action switch
    {
        HotkeyAction.Snip => "截图",
        HotkeyAction.FullScreen => "全屏截图",
        HotkeyAction.RecordRectangle => "矩形录屏",
        HotkeyAction.RecordWindow => "窗口录屏",
        HotkeyAction.ColorPick => "屏幕取色",
        HotkeyAction.OpenEditorOnLastCapture => "标注最近截图",
        _ => action.ToString(),
    };
}
