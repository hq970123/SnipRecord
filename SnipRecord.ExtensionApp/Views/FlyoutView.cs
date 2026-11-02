using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipRecord.ExtensionApp.Services;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>Flyout. TODO: actions + recent captures list.</summary>
public sealed class FlyoutView : StackPanel
{
    private readonly RecentStore? _recent;
    private readonly TextBlock _status;

    public FlyoutView(RecentStore? recent)
    {
        _recent = recent;
        Spacing = 8;
        Padding = new Thickness(16);
        Children.Add(new TextBlock
        {
            Text = "截图录屏",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
        });
        _status = new TextBlock { Text = "TODO: 截图 / 录屏 / 取色 / 最近记录", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        Children.Add(_status);
        Refresh();
    }

    public void Refresh()
    {
        // TODO: render _recent items.
        _status.Text = $"TODO: 截图 / 录屏 / 取色 / 最近记录（{_recent?.Count ?? 0} 条）";
    }
}
