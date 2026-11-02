using SnipRecord.ExtensionApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WidBar.SDK;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>Settings page hosted by WidBar (it adds Save / Cancel). TODO: real options.</summary>
public sealed class SettingsView : StackPanel
{
    private readonly AppSettings _draft;

    public SettingsView(IWidgetSettingsContext context)
    {
        _draft = AppSettings.FromJson(context.SettingsJson);
        Spacing = 12;

        var width = new NumberBox
        {
            Header = "任务栏宽度",
            Minimum = AppSettings.MinWidth,
            Maximum = AppSettings.MaxWidth,
            Value = _draft.PreviewWidth,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        width.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            _draft.PreviewWidth = (int)Math.Clamp(e.NewValue, AppSettings.MinWidth, AppSettings.MaxWidth);
            context.SaveSettings(_draft.ToJson());
        };
        Children.Add(width);
        Children.Add(new TextBlock { Text = "TODO: 保存目录、录屏格式、麦克风、快捷键…", Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
    }
}
