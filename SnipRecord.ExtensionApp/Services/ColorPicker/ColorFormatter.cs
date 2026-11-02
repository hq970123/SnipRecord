using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using static SnipRecord.ExtensionApp.Services.ColorPicker.NativeMethods;

namespace SnipRecord.ExtensionApp.Services.ColorPicker;

/// <summary>Formats an RGB colour as "#RRGGBB", "rgb(r, g, b)" or "hsl(h, s%, l%)".</summary>
public static class ColorFormatter
{
    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static string ToRgb(byte r, byte g, byte b) =>
        string.Create(CultureInfo.InvariantCulture, $"rgb({r}, {g}, {b})");

    public static string ToHsl(byte r, byte g, byte b)
    {
        var (h, s, l) = RgbToHsl(r, g, b);
        return string.Create(CultureInfo.InvariantCulture, $"hsl({h}, {s}%, {l}%)");
    }

    /// <summary><paramref name="format"/>: AppSettings.ColorFormat ("HEX" / "RGB" / "HSL"); anything else = HEX.</summary>
    public static string Format(byte r, byte g, byte b, string? format) =>
        (format ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            AppSettings.ColorFormatRgb => ToRgb(r, g, b),
            AppSettings.ColorFormatHsl => ToHsl(r, g, b),
            _ => ToHex(r, g, b),
        };

    /// <summary>Hue 0–359°, saturation and lightness 0–100 %, rounded.</summary>
    public static (int H, int S, int L) RgbToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var l = (max + min) / 2.0;
        double h = 0, s = 0;
        var d = max - min;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == rd) h = ((gd - bd) / d) + (gd < bd ? 6 : 0);
            else if (max == gd) h = ((bd - rd) / d) + 2;
            else h = ((rd - gd) / d) + 4;
            h *= 60;
        }
        var hi = (int)Math.Round(h, MidpointRounding.AwayFromZero) % 360;
        return (hi, (int)Math.Round(s * 100, MidpointRounding.AwayFromZero), (int)Math.Round(l * 100, MidpointRounding.AwayFromZero));
    }
}

/// <summary>Plain Win32 CF_UNICODETEXT clipboard writer (works on any thread; retries if the clipboard is busy).</summary>
internal static class Win32Clipboard
{
    public static bool SetText(string text, IntPtr owner = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner))
            {
                try
                {
                    if (!EmptyClipboard()) return false;
                    var bytes = (text.Length + 1) * 2;
                    var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes);
                    if (hMem == IntPtr.Zero) return false;
                    var ptr = GlobalLock(hMem);
                    if (ptr == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                        Marshal.WriteInt16(ptr, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hMem);
                    }
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem); // ownership not transferred
                        return false;
                    }
                    return true; // the system owns hMem now
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(30);
        }
        Debug.WriteLine("[SnipRecord] clipboard busy, colour not copied");
        return false;
    }
}
