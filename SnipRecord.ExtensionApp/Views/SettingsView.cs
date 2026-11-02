using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SnipRecord.ExtensionApp.Services;
using WidBar.SDK;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>Settings page hosted by WidBar (it adds Save / Cancel). Every change is pushed as a draft.</summary>
public sealed class SettingsView : StackPanel
{
    private readonly IWidgetSettingsContext _context;
    private readonly AppSettings _draft;

    private static readonly (SnipMode Mode, string Label)[] SnipModes =
    {
        (SnipMode.Rectangle, "矩形"),
        (SnipMode.Window, "窗口"),
        (SnipMode.FreeForm, "自由形状"),
    };

    private static readonly (string Value, string Label)[] ColorFormats =
    {
        (AppSettings.ColorFormatHex, "HEX（#1E90FF）"),
        (AppSettings.ColorFormatRgb, "RGB（rgb(30, 144, 255)）"),
        (AppSettings.ColorFormatHsl, "HSL（hsl(210, 100%, 56%)）"),
    };

    public SettingsView(IWidgetSettingsContext context)
    {
        _context = context;
        _draft = AppSettings.FromJson(context.SettingsJson);
        Spacing = 14;

        // ---- 截图
        Children.Add(UiHelpers.Caption("截图"));

        var mode = new ComboBox { Header = "任务栏截图按钮的默认模式", MinWidth = 220 };
        foreach (var (_, label) in SnipModes) mode.Items.Add(label);
        mode.SelectedIndex = Math.Max(0, Array.FindIndex(SnipModes, m => m.Mode == _draft.DefaultSnipMode));
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex < 0) return;
            _draft.DefaultSnipMode = SnipModes[mode.SelectedIndex].Mode;
            Save();
        };
        Children.Add(mode);

        var delay = new ComboBox { Header = "延时（截图和矩形录屏）", MinWidth = 220 };
        foreach (var d in AppSettings.AllowedDelays) delay.Items.Add(d == 0 ? "不延时" : $"{d} 秒");
        delay.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.AllowedDelays, _draft.DelaySeconds));
        delay.SelectionChanged += (_, _) =>
        {
            if (delay.SelectedIndex < 0) return;
            _draft.DelaySeconds = AppSettings.AllowedDelays[delay.SelectedIndex];
            Save();
        };
        Children.Add(delay);

        var autoSave = new ToggleSwitch
        {
            Header = "截图工具同时自动保存（截图工具自己的保存位置）",
            IsOn = _draft.SnipAutoSave,
            OnContent = "开",
            OffContent = "关",
        };
        autoSave.Toggled += (_, _) => { _draft.SnipAutoSave = autoSave.IsOn; Save(); };
        Children.Add(autoSave);

        Children.Add(FolderRow("截图保存位置", () => _draft.ImageSaveFolder, v => _draft.ImageSaveFolder = v,
            () => _draft.GetImageFolder()));

        // ---- 录屏
        Children.Add(UiHelpers.Caption("录屏"));
        Children.Add(FolderRow("录屏保存位置", () => _draft.SaveFolder, v => _draft.SaveFolder = v,
            () => _draft.GetVideoFolder()));

        var mic = new ToggleSwitch
        {
            Header = "窗口录屏时同时录制麦克风",
            IsOn = _draft.RecordMicrophone,
            OnContent = "开",
            OffContent = "关",
        };
        mic.Toggled += (_, _) => { _draft.RecordMicrophone = mic.IsOn; Save(); };
        Children.Add(mic);
        Children.Add(new TextBlock
        {
            Text = "窗口录屏会录制该窗口所属程序的声音（需 Windows 11）。矩形录屏由系统截图工具完成，声音选项在截图工具里设置。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });

        // ---- 取色
        Children.Add(UiHelpers.Caption("取色"));
        var format = new ComboBox { Header = "复制的颜色格式", MinWidth = 220 };
        foreach (var (_, label) in ColorFormats) format.Items.Add(label);
        format.SelectedIndex = Math.Max(0, Array.FindIndex(ColorFormats, f => f.Value == _draft.ColorFormat));
        format.SelectionChanged += (_, _) =>
        {
            if (format.SelectedIndex < 0) return;
            _draft.ColorFormat = ColorFormats[format.SelectedIndex].Value;
            Save();
        };
        Children.Add(format);

        // ---- 外观
        Children.Add(UiHelpers.Caption("任务栏"));
        var width = new NumberBox
        {
            Header = "任务栏宽度",
            Minimum = AppSettings.MinWidth,
            Maximum = AppSettings.MaxWidth,
            Value = _draft.PreviewWidth,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 160,
        };
        width.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            _draft.PreviewWidth = (int)Math.Clamp(e.NewValue, AppSettings.MinWidth, AppSettings.MaxWidth);
            Save();
        };
        Children.Add(width);
    }

    private void Save()
    {
        _draft.Normalize();
        _context.SaveSettings(_draft.ToJson()); // also pushes the draft to the plugin (OnSettingsDraftChanged)
        _draft.SaveShared();
    }

    /// <summary>Folder path box + 浏览 + 默认. Empty = default folder.</summary>
    private UIElement FolderRow(string header, Func<string?> get, Action<string?> set, Func<string> effective)
    {
        var box = new TextBox
        {
            Header = header,
            Text = get() ?? "",
            PlaceholderText = effective(),
            MinWidth = 240,
        };
        box.LostFocus += (_, _) =>
        {
            var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            if (value == get()) return;
            set(value);
            Save();
        };

        var browse = new Button { Content = "浏览…", VerticalAlignment = VerticalAlignment.Bottom };
        browse.Click += async (_, _) =>
        {
            var path = await PickFolderAsync();
            if (string.IsNullOrEmpty(path)) return;
            box.Text = path;
            set(path);
            Save();
        };

        var reset = new Button { Content = "默认", VerticalAlignment = VerticalAlignment.Bottom };
        reset.Click += (_, _) =>
        {
            box.Text = "";
            set(null);
            Save();
            box.PlaceholderText = effective();
        };

        var open = new Button { Content = "打开", VerticalAlignment = VerticalAlignment.Bottom };
        open.Click += (_, _) => UiHelpers.RevealInFolder(effective());

        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(reset, 2);
        Grid.SetColumn(open, 3);
        grid.Children.Add(browse);
        grid.Children.Add(reset);
        grid.Children.Add(open);
        return grid;
    }

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var windowId = XamlRoot?.ContentIslandEnvironment?.AppWindowId ?? default;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId);
            var result = await picker.PickSingleFolderAsync();
            return result?.Path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] folder picker failed: {ex}");
            return null;
        }
    }
}
