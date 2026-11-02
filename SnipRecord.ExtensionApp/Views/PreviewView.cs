using SnipRecord.ExtensionApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>Taskbar preview. TODO: screenshot / record / color-picker buttons.</summary>
public sealed class PreviewView : Grid
{
    private readonly TextBlock _label;

    public PreviewView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Padding = new Thickness(4, 0, 4, 0);
        _label = new TextBlock
        {
            Text = "截图录屏",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 13,
        };
        Children.Add(_label);
    }

    public void ApplySettings(AppSettings settings)
    {
        // TODO: rebuild the button row from settings.
    }
}
