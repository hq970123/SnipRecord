using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SnipRecord.ExtensionApp.Services;
using SnipRecord.ExtensionApp.Services.FullScreen;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>
/// Taskbar preview: three Snipping-Tool style icon buttons — 截图 / 录屏 / 取色.
/// 截图 opens a menu (截图 / 全屏当前屏幕 / 全屏所有屏幕), 录屏 a menu (矩形 / 窗口).
/// While a window recording runs, the record button turns red with the elapsed time and stops on click.
/// </summary>
public sealed class PreviewView : Grid
{
    private const double ButtonSize = 32;
    private const double IconSize = 16;

    private readonly SnipRecordPlugin _plugin;
    private readonly Button _snip;
    private readonly Button _record;
    private readonly Button _pick;
    private readonly FontIcon _recordIcon;
    private readonly TextBlock _recordTime;
    private readonly MenuFlyout _recordMenu;
    private readonly MenuFlyout _snipMenu;
    private bool _showingRecording;

    public PreviewView(SnipRecordPlugin plugin)
    {
        _plugin = plugin;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Padding = new Thickness(2, 0, 2, 0);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Clicking the buttons does not open the flyout (the button handles the pointer).
        _snip = UiHelpers.IconButton(UiHelpers.GlyphCamera, ButtonSize, IconSize, "截图");
        _snipMenu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        var snipDefault = new MenuFlyoutItem { Text = "截图", Icon = UiHelpers.Icon(UiHelpers.GlyphCamera, 14) };
        snipDefault.Click += async (_, _) => await _plugin.SnipAsync();
        var fullCurrent = new MenuFlyoutItem { Text = "全屏（当前屏幕）", Icon = UiHelpers.Icon(UiHelpers.GlyphFullScreen, 14) };
        fullCurrent.Click += async (_, _) => await _plugin.FullScreenAsync(FullScreenTarget.CurrentMonitor);
        var fullAll = new MenuFlyoutItem { Text = "全屏（所有屏幕）", Icon = UiHelpers.Icon(UiHelpers.GlyphMonitors, 14) };
        fullAll.Click += async (_, _) => await _plugin.FullScreenAsync(FullScreenTarget.AllMonitors);
        _snipMenu.Items.Add(snipDefault);
        _snipMenu.Items.Add(fullCurrent);
        _snipMenu.Items.Add(fullAll);
        _snip.Click += (_, _) => _snipMenu.ShowAt(_snip);

        _recordIcon = UiHelpers.Icon(UiHelpers.GlyphVideo, IconSize);
        _recordTime = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(3, 0, 0, 0),
        };
        var recordContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        recordContent.Children.Add(_recordIcon);
        recordContent.Children.Add(_recordTime);
        _record = UiHelpers.IconButton(UiHelpers.GlyphVideo, ButtonSize, IconSize, "录屏");
        _record.Content = recordContent;
        _record.Padding = new Thickness(0);

        _recordMenu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        var rect = new MenuFlyoutItem { Text = "矩形录屏（截图工具）", Icon = UiHelpers.Icon(UiHelpers.GlyphRectangle, 14) };
        rect.Click += async (_, _) => await _plugin.RecordRectangleAsync();
        var window = new MenuFlyoutItem { Text = "窗口录屏", Icon = UiHelpers.Icon(UiHelpers.GlyphWindow, 14) };
        window.Click += async (_, _) => await _plugin.RecordWindowAsync();
        _recordMenu.Items.Add(rect);
        _recordMenu.Items.Add(window);

        _record.Click += async (_, _) =>
        {
            if (_plugin.IsWindowRecording)
            {
                await _plugin.StopRecordingAsync();
            }
            else
            {
                _recordMenu.ShowAt(_record);
            }
        };

        _pick = UiHelpers.IconButton(UiHelpers.GlyphEyedropper, ButtonSize, IconSize, "取色");
        _pick.Click += async (_, _) => await _plugin.PickColorAsync();

        row.Children.Add(_snip);
        row.Children.Add(_record);
        row.Children.Add(_pick);
        Children.Add(row);

        Refresh();
    }

    /// <summary>Re-reads recording state from the plugin (UI thread).</summary>
    public void Refresh()
    {
        var recording = _plugin.IsWindowRecording;
        if (recording)
        {
            _recordTime.Text = UiHelpers.FormatElapsed(_plugin.RecordingElapsed);
            if (!_showingRecording)
            {
                _showingRecording = true;
                var red = new SolidColorBrush(UiHelpers.RecordRed);
                _recordIcon.Glyph = UiHelpers.GlyphStop;
                _recordIcon.FontSize = 12;
                _recordIcon.Foreground = red;
                _recordTime.Foreground = red;
                _recordTime.Visibility = Visibility.Visible;
                _record.Padding = new Thickness(6, 0, 6, 0);
                ToolTipService.SetToolTip(_record, "正在录制窗口，点击停止");
            }
        }
        else if (_showingRecording)
        {
            _showingRecording = false;
            _recordIcon.Glyph = UiHelpers.GlyphVideo;
            _recordIcon.FontSize = IconSize;
            _recordIcon.ClearValue(IconElement.ForegroundProperty);
            _recordTime.ClearValue(TextBlock.ForegroundProperty);
            _recordTime.Visibility = Visibility.Collapsed;
            _record.Padding = new Thickness(0);
            ToolTipService.SetToolTip(_record, "录屏");
        }
    }
}
