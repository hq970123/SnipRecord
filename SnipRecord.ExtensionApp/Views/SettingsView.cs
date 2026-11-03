using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SnipRecord.ExtensionApp.Services;
using SnipRecord.ExtensionApp.Services.Gif;
using SnipRecord.ExtensionApp.Services.Hotkeys;
using WidBar.SDK;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>Settings page hosted by WidBar (it adds Save / Cancel). Every change is pushed as a draft.</summary>
public sealed class SettingsView : StackPanel
{
    private readonly IWidgetSettingsContext _context;
    private readonly SnipRecordPlugin _plugin;
    private readonly AppSettings _draft;
    private readonly Dictionary<HotkeyAction, TextBlock> _hotkeyStatus = new();
    private readonly List<Control> _hotkeyControls = new();

    private static readonly int[] GifFpsChoices = { 5, 10, 15, 20 };
    private static readonly (int Value, string Label)[] GifWidthChoices =
    {
        (480, "480 像素"),
        (640, "640 像素"),
        (800, "800 像素"),
        (1080, "1080 像素"),
        (0, "原始大小"),
    };

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

    public SettingsView(IWidgetSettingsContext context, SnipRecordPlugin plugin)
    {
        _context = context;
        _plugin = plugin;
        _draft = AppSettings.FromJson(context.SettingsJson);
        // The flyout toggles only persist to the shared settings.json; the plugin holds the current values.
        _draft.RecordMicrophone = plugin.Settings.RecordMicrophone;
        _draft.RecordAsGif = plugin.Settings.RecordAsGif;
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

        var openEditor = new ToggleSwitch
        {
            Header = "截图后打开标注",
            IsOn = _draft.OpenEditorAfterCapture,
            OnContent = "开",
            OffContent = "关",
        };
        openEditor.Toggled += (_, _) => { _draft.OpenEditorAfterCapture = openEditor.IsOn; Save(); };
        Children.Add(openEditor);

        var fullScreen = new ComboBox { Header = "全屏截图默认范围", MinWidth = 220 };
        fullScreen.Items.Add("当前屏幕（鼠标所在）");
        fullScreen.Items.Add("所有屏幕");
        fullScreen.SelectedIndex = _draft.FullScreenAllMonitors ? 1 : 0;
        fullScreen.SelectionChanged += (_, _) =>
        {
            if (fullScreen.SelectedIndex < 0) return;
            _draft.FullScreenAllMonitors = fullScreen.SelectedIndex == 1;
            Save();
        };
        Children.Add(fullScreen);

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

        // ---- GIF
        Children.Add(UiHelpers.Caption("GIF"));
        var fps = new ComboBox { Header = "帧率", MinWidth = 220 };
        foreach (var f in GifFpsChoices) fps.Items.Add($"{f} 帧/秒");
        fps.SelectedIndex = Math.Max(0, Array.IndexOf(GifFpsChoices, GifOptions.SnapFps(_draft.GifFps)));
        fps.SelectionChanged += (_, _) =>
        {
            if (fps.SelectedIndex < 0) return;
            _draft.GifFps = GifFpsChoices[fps.SelectedIndex];
            Save();
        };
        Children.Add(fps);

        var widths = GifWidthChoices.ToList();
        if (!widths.Any(w => w.Value == _draft.GifMaxWidth))
        {
            widths.Add((_draft.GifMaxWidth, $"{_draft.GifMaxWidth} 像素")); // hand-edited value
        }
        var maxWidth = new ComboBox { Header = "最大宽度（更宽的会等比缩小）", MinWidth = 220 };
        foreach (var (_, label) in widths) maxWidth.Items.Add(label);
        maxWidth.SelectedIndex = Math.Max(0, widths.FindIndex(w => w.Value == _draft.GifMaxWidth));
        maxWidth.SelectionChanged += (_, _) =>
        {
            if (maxWidth.SelectedIndex < 0) return;
            _draft.GifMaxWidth = widths[maxWidth.SelectedIndex].Value;
            Save();
        };
        Children.Add(maxWidth);

        var recordAsGif = new ToggleSwitch
        {
            Header = "录成 GIF（录屏结束后自动转换，并删除 MP4）",
            IsOn = _draft.RecordAsGif,
            OnContent = "开",
            OffContent = "关",
        };
        recordAsGif.Toggled += (_, _) => { _draft.RecordAsGif = recordAsGif.IsOn; Save(); };
        Children.Add(recordAsGif);
        Children.Add(new TextBlock
        {
            Text = "也可以在最近记录里点击录屏旁的“GIF”按钮单独导出（保留 MP4）。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });

        // ---- 快捷键
        BuildHotkeys();

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

    private void BuildHotkeys()
    {
        Children.Add(UiHelpers.Caption("快捷键"));

        var enabled = new ToggleSwitch
        {
            Header = "启用全局快捷键",
            IsOn = _draft.HotkeysEnabled,
            OnContent = "开",
            OffContent = "关",
        };
        enabled.Toggled += (_, _) =>
        {
            _draft.HotkeysEnabled = enabled.IsOn;
            foreach (var c in _hotkeyControls) c.IsEnabled = enabled.IsOn;
            Save();
        };
        Children.Add(enabled);

        var list = new StackPanel { Spacing = 6 };
        var bindings = _draft.GetHotkeys();
        foreach (var action in HotkeyBindings.AllActions)
        {
            var act = action;
            var label = new TextBlock
            {
                Text = HotkeyBindings.GetDisplayName(act),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var box = new HotkeyRecorderBox { Hotkey = bindings[act], MinWidth = 150 };
            box.HotkeyChanged += (_, hk) => SetHotkey(act, hk);

            var reset = new Button { Content = "重置", VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(reset, "恢复默认：" + HotkeyBindings.GetDefault(act).ToDisplayString());
            reset.Click += (_, _) =>
            {
                var def = HotkeyBindings.GetDefault(act);
                box.Hotkey = def;
                SetHotkey(act, def);
            };

            var status = new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = UiHelpers.TryBrush("SystemFillColorCriticalBrush") ?? new SolidColorBrush(UiHelpers.RecordRed),
                Visibility = Visibility.Collapsed,
            };
            _hotkeyStatus[act] = status;
            _hotkeyControls.Add(box);
            _hotkeyControls.Add(reset);
            box.IsEnabled = reset.IsEnabled = _draft.HotkeysEnabled;

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(label);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            Grid.SetColumn(reset, 2);
            grid.Children.Add(reset);
            Grid.SetRow(status, 1);
            Grid.SetColumn(status, 1);
            Grid.SetColumnSpan(status, 2);
            grid.Children.Add(status);
            list.Children.Add(grid);
        }
        Children.Add(list);
        Children.Add(new TextBlock
        {
            Text = "点击输入框后按下新的组合键（需包含 Ctrl、Alt 或 Win，或单独的 F1–F24）；Backspace 清除，Esc 取消。" +
                   "Ctrl+Alt+A 常与 QQ / 微信的截图快捷键冲突，被占用时可以在这里改成其他组合。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
        });

        UpdateHotkeyStatus();
        Loaded += (_, _) => { _plugin.HotkeyStatusChanged -= UpdateHotkeyStatus; _plugin.HotkeyStatusChanged += UpdateHotkeyStatus; UpdateHotkeyStatus(); };
        Unloaded += (_, _) => _plugin.HotkeyStatusChanged -= UpdateHotkeyStatus;
    }

    private void SetHotkey(HotkeyAction action, Hotkey hotkey)
    {
        var bindings = _draft.GetHotkeys();
        if (bindings[action] == hotkey) return;
        bindings[action] = hotkey;
        _draft.SetHotkeys(bindings);
        Save(); // the plugin re-registers and raises HotkeyStatusChanged
    }

    /// <summary>Red text under each binding that the plugin could not register (UI thread).</summary>
    private void UpdateHotkeyStatus()
    {
        var status = _draft.HotkeysEnabled ? _plugin.HotkeyStatus : null;
        foreach (var (action, text) in _hotkeyStatus)
        {
            if (status is not null && status.TryGetValue(action, out var r)
                && r.Status is not (HotkeyRegistrationStatus.Registered or HotkeyRegistrationStatus.Disabled))
            {
                text.Text = r.StatusText;
                text.Visibility = Visibility.Visible;
            }
            else
            {
                text.Visibility = Visibility.Collapsed;
            }
        }
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
