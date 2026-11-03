using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace SnipRecord.ExtensionApp.Views;

internal static class UiHelpers
{
    // Segoe Fluent Icons
    public const string GlyphCamera = "\uE722";
    public const string GlyphVideo = "\uE714";
    public const string GlyphEyedropper = "\uEF3C";
    public const string GlyphStop = "\uE71A";
    public const string GlyphCopy = "\uE8C8";
    public const string GlyphFolder = "\uE838";
    public const string GlyphMic = "\uE720";
    public const string GlyphWindow = "\uE737";
    public const string GlyphRectangle = "\uF407";
    public const string GlyphWarning = "\uE7BA";
    public const string GlyphDelete = "\uE74D";
    public const string GlyphFullScreen = "\uE740";
    public const string GlyphMonitors = "\uE7F4";
    public const string GlyphEdit = "\uE70F";

    public static readonly Windows.UI.Color RecordRed = Argb(0xFF, 0xE8, 0x11, 0x23);

    public static Windows.UI.Color Argb(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    public static FontFamily IconFont { get; } = new("Segoe Fluent Icons,Segoe MDL2 Assets");

    public static FontIcon Icon(string glyph, double size) =>
        new() { Glyph = glyph, FontSize = size, FontFamily = IconFont };

    /// <summary>A borderless icon button that keeps the theme's hover and pressed states.</summary>
    public static Button IconButton(string glyph, double size, double iconSize, string tooltip)
    {
        var button = new Button
        {
            MinWidth = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = Icon(glyph, iconSize),
        };
        MakeBorderless(button);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    public static void MakeBorderless(Button button)
    {
        var clear = new SolidColorBrush(Colors.Transparent);
        button.Resources["ButtonBorderBrush"] = clear;
        button.Resources["ButtonBorderBrushPointerOver"] = clear;
        button.Resources["ButtonBorderBrushPressed"] = clear;
        button.Resources["ButtonBackground"] = clear;
    }

    /// <summary>Large tile button: icon above a caption.</summary>
    public static Button TileButton(string glyph, string caption, string tooltip)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22, FontFamily = IconFont, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button
        {
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(4, 10, 4, 8),
            CornerRadius = new CornerRadius(6),
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    public static Style? TryStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Style : null;

    public static Brush? TryBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = TryStyle("BodyStrongTextBlockStyle"),
        Margin = new Thickness(0, 4, 0, 0),
    };

    public static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static void CopyText(string text)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy text failed: {ex.Message}");
        }
    }

    /// <summary>Copies the file itself (so it pastes into Explorer / chat apps); images also as bitmap.</summary>
    public static async Task<bool> CopyFileAsync(string path, bool isVideo)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetStorageItems(new IStorageItem[] { file });
            if (!isVideo)
            {
                data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            }
            Clipboard.SetContent(data);
            Clipboard.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy file failed: {ex.Message}");
            return false;
        }
    }

    public static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open failed: {ex.Message}");
        }
    }

    /// <summary>Opens Explorer with the file selected (or the folder itself).</summary>
    public static void RevealInFolder(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reveal failed: {ex.Message}");
        }
    }

    /// <summary>"#RRGGBB" → Color (opaque); null when malformed.</summary>
    public static Windows.UI.Color? ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var h = hex.TrimStart('#');
        if (h.Length != 6) return null;
        try
        {
            return Argb(0xFF,
                Convert.ToByte(h[..2], 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch
        {
            return null;
        }
    }
}
