using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SnipRecord.ExtensionApp.Views.Editor;

/// <summary>Small UI helpers for the editor (kept local so Views/Editor has no dependency on other view files).</summary>
internal static class EditorUi
{
    public static FontFamily IconFont { get; } = new("Segoe Fluent Icons,Segoe MDL2 Assets");
    public static readonly Windows.UI.Color ErrorRed = Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x11, 0x23);

    public static FontIcon Icon(string glyph, double size) => new() { Glyph = glyph, FontSize = size, FontFamily = IconFont };

    public static Button IconButton(string glyph, double size, double iconSize, string tooltip)
    {
        var b = new Button
        {
            Width = size, Height = size, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            Content = Icon(glyph, iconSize), VerticalAlignment = VerticalAlignment.Center,
        };
        MakeBorderless(b);
        ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    public static void MakeBorderless(Button button)
    {
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled", "ButtonBackground", "ButtonBackgroundDisabled" })
            button.Resources[key] = clear;
    }

    public static Style? TryStyle(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var v) == true ? v as Style : null;

    public static Brush? TryBrush(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var v) == true ? v as Brush : null;
}
