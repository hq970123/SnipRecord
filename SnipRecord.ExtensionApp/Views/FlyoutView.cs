using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SnipRecord.ExtensionApp.Services;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>
/// Flyout: big 截图 / 录屏 / 取色 buttons with mode selectors, microphone + GIF toggles, recent captures
/// (标注 for images, 导出 GIF for videos) and recent colours.
/// </summary>
public sealed class FlyoutView : Grid
{
    private readonly SnipRecordPlugin _plugin;
    private readonly TextBlock _warning;
    private readonly Border _warningBox;
    private readonly TextBlock _status;
    private readonly Button _recordTile;
    private readonly TextBlock _recordCaption;
    private readonly FontIcon _recordGlyph;
    private readonly Segmented _snipMode;
    private readonly Segmented _recordMode;
    private readonly ToggleSwitch _mic;
    private readonly ToggleSwitch _gif;
    private readonly StackPanel _captures;
    private readonly TextBlock _capturesEmpty;
    private readonly StackPanel _colors;
    private readonly TextBlock _colorsEmpty;
    private bool _recordingShown;

    /// <summary>"导出 GIF" buttons of the rows currently rendered, by MP4 path.</summary>
    private readonly Dictionary<string, (Button Button, TextBlock Text)> _gifButtons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Last known export progress (0–1) per MP4, shared by every flyout instance: the flyout content is rebuilt
    /// (and rows re-rendered on every recent change) while an export keeps running.
    /// </summary>
    private static readonly Dictionary<string, double> GifProgress = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<WeakReference<FlyoutView>> Instances = new();

    private static readonly SnipMode[] SnipModes = { SnipMode.Rectangle, SnipMode.Window, SnipMode.FreeForm, SnipMode.FullScreen };

    public FlyoutView(SnipRecordPlugin plugin)
    {
        _plugin = plugin;
        Instances.RemoveAll(w => !w.TryGetTarget(out _));
        Instances.Add(new WeakReference<FlyoutView>(this));

        var root = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 16) };
        Children.Add(new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        root.Children.Add(new TextBlock { Text = "截图录屏", Style = UiHelpers.TryStyle("SubtitleTextBlockStyle") });

        // ---- warning (recorder warning / Snipping Tool unsupported)
        _warning = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var warnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        warnRow.Children.Add(new FontIcon { Glyph = UiHelpers.GlyphWarning, FontSize = 14, FontFamily = UiHelpers.IconFont, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        warnRow.Children.Add(_warning);
        _warningBox = new Border
        {
            Child = warnRow,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = UiHelpers.TryBrush("SystemFillColorCautionBackgroundBrush")
                ?? new SolidColorBrush(UiHelpers.Argb(0x33, 0xFF, 0xC1, 0x07)),
            Visibility = Visibility.Collapsed,
        };
        _warning.MaxWidth = 300;
        root.Children.Add(_warningBox);

        // ---- big buttons
        var tiles = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < 3; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var snipTile = UiHelpers.TileButton(UiHelpers.GlyphCamera, "截图", "用截图工具截图（模式见下方）");
        snipTile.Click += async (_, _) => await _plugin.SnipAsync(SnipModes[Math.Max(0, _snipMode!.SelectedIndex)]);

        _recordTile = UiHelpers.TileButton(UiHelpers.GlyphVideo, "录屏", "录屏（模式见下方）");
        var recPanel = (StackPanel)_recordTile.Content;
        _recordGlyph = (FontIcon)recPanel.Children[0];
        _recordCaption = (TextBlock)recPanel.Children[1];
        _recordTile.Click += async (_, _) =>
        {
            if (_plugin.IsWindowRecording) await _plugin.StopRecordingAsync();
            else if (_recordMode!.SelectedIndex == 1) await _plugin.RecordWindowAsync();
            else await _plugin.RecordRectangleAsync();
        };

        var pickTile = UiHelpers.TileButton(UiHelpers.GlyphEyedropper, "取色", "屏幕取色，结果自动复制");
        pickTile.Click += async (_, _) => await _plugin.PickColorAsync();

        Grid.SetColumn(snipTile, 0);
        Grid.SetColumn(_recordTile, 1);
        Grid.SetColumn(pickTile, 2);
        tiles.Children.Add(snipTile);
        tiles.Children.Add(_recordTile);
        tiles.Children.Add(pickTile);
        root.Children.Add(tiles);

        // ---- modes
        _snipMode = new Segmented(new[] { "矩形", "窗口", "自由形状", "全屏" },
            Math.Max(0, Array.IndexOf(SnipModes, _plugin.Settings.DefaultSnipMode)));
        root.Children.Add(LabeledRow("截图模式", _snipMode));

        _recordMode = new Segmented(new[] { "矩形", "窗口" }, 1);
        root.Children.Add(LabeledRow("录屏模式", _recordMode));

        _mic = SmallToggle(_plugin.Settings.RecordMicrophone);
        _mic.Toggled += (_, _) => _plugin.SetRecordMicrophone(_mic.IsOn);
        root.Children.Add(ToggleRow(UiHelpers.GlyphMic, "窗口录屏时录制麦克风", _mic));

        _gif = SmallToggle(_plugin.Settings.RecordAsGif);
        _gif.Toggled += (_, _) => _plugin.SetRecordAsGif(_gif.IsOn);
        var gifRow = ToggleRow(UiHelpers.GlyphVideo, "录成 GIF", _gif);
        ToolTipService.SetToolTip(gifRow, "录屏结束后自动转换为 GIF 并删除 MP4；帧率和最大宽度在设置里调整");
        root.Children.Add(gifRow);

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
        root.Children.Add(_status);

        // ---- recent captures
        root.Children.Add(HeaderWithClear("最近截图 / 录屏", () => _plugin.Recent.ClearCaptures()));
        _captures = new StackPanel { Spacing = 4 };
        _capturesEmpty = new TextBlock { Text = "还没有记录", Opacity = 0.6, FontSize = 12 };
        root.Children.Add(_capturesEmpty);
        root.Children.Add(_captures);

        // ---- recent colours
        root.Children.Add(HeaderWithClear("最近颜色", () => _plugin.Recent.ClearColors()));
        _colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _colorsEmpty = new TextBlock { Text = "还没有取过颜色", Opacity = 0.6, FontSize = 12 };
        root.Children.Add(_colorsEmpty);
        root.Children.Add(_colors);

        Refresh();
    }

    /// <summary>Full refresh: lists + state (UI thread).</summary>
    public void Refresh()
    {
        RefreshState();
        RenderCaptures();
        RenderColors();
    }

    /// <summary>Recording state, warnings and status only (cheap; called on every timer tick).</summary>
    public void RefreshState()
    {
        // Warning
        string? warning = null;
        if (SnippingToolLauncher.IsSupported == false)
        {
            warning = "当前截图工具版本不支持调用接口，截图和矩形录屏可能无法使用。请在 Microsoft Store 更新“截图工具”。";
        }
        if (!string.IsNullOrWhiteSpace(_plugin.RecorderWarning))
        {
            warning = warning is null ? _plugin.RecorderWarning : warning + "\n" + _plugin.RecorderWarning;
        }
        _warning.Text = warning ?? "";
        _warningBox.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;

        // Status
        if (_plugin.StatusMessage is { } msg)
        {
            _status.Text = msg;
            _status.Visibility = Visibility.Visible;
            if (_plugin.StatusIsError)
            {
                _status.Foreground = UiHelpers.TryBrush("SystemFillColorCriticalBrush") ?? new SolidColorBrush(UiHelpers.RecordRed);
            }
            else
            {
                _status.ClearValue(TextBlock.ForegroundProperty);
            }
        }
        else
        {
            _status.Visibility = Visibility.Collapsed;
        }

        if (_mic.IsOn != _plugin.Settings.RecordMicrophone) _mic.IsOn = _plugin.Settings.RecordMicrophone;
        if (_gif.IsOn != _plugin.Settings.RecordAsGif) _gif.IsOn = _plugin.Settings.RecordAsGif;

        // Record tile
        if (_plugin.IsWindowRecording)
        {
            _recordCaption.Text = "停止 " + UiHelpers.FormatElapsed(_plugin.RecordingElapsed);
            if (!_recordingShown)
            {
                _recordingShown = true;
                var red = new SolidColorBrush(UiHelpers.RecordRed);
                _recordGlyph.Glyph = UiHelpers.GlyphStop;
                _recordGlyph.Foreground = red;
                _recordCaption.Foreground = red;
                ToolTipService.SetToolTip(_recordTile, "正在录制窗口，点击停止");
            }
        }
        else if (_recordingShown)
        {
            _recordingShown = false;
            _recordCaption.Text = "录屏";
            _recordGlyph.Glyph = UiHelpers.GlyphVideo;
            _recordGlyph.ClearValue(IconElement.ForegroundProperty);
            _recordCaption.ClearValue(TextBlock.ForegroundProperty);
            ToolTipService.SetToolTip(_recordTile, "录屏（模式见下方）");
        }
    }

    private void RenderCaptures()
    {
        _captures.Children.Clear();
        _gifButtons.Clear();
        var items = _plugin.Recent.Items;
        _capturesEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items.Take(RecentStore.MaxCaptures))
        {
            _captures.Children.Add(CaptureRow(item));
        }
    }

    private UIElement CaptureRow(RecentItem item)
    {
        // Thumbnail / video icon
        FrameworkElement thumb;
        if (!item.IsVideo && File.Exists(item.Path))
        {
            var bmp = new BitmapImage { DecodePixelWidth = 112, DecodePixelType = DecodePixelType.Logical };
            try { bmp.UriSource = new Uri(item.Path); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
            thumb = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        }
        else
        {
            thumb = new FontIcon
            {
                Glyph = item.IsVideo ? UiHelpers.GlyphVideo : UiHelpers.GlyphCamera,
                FontSize = 18,
                FontFamily = UiHelpers.IconFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        var thumbBox = new Border
        {
            Width = 56,
            Height = 36,
            CornerRadius = new CornerRadius(4),
            Background = UiHelpers.TryBrush("ControlFillColorSecondaryBrush") ?? new SolidColorBrush(UiHelpers.Argb(0x20, 0x80, 0x80, 0x80)),
            Child = thumb,
        };

        var name = new TextBlock
        {
            Text = Path.GetFileName(item.Path),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var when = new TextBlock
        {
            Text = (item.IsVideo ? "录屏 · " : "截图 · ") + item.CreatedAt.ToLocalTime().ToString("M月d日 HH:mm"),
            FontSize = 11,
            Opacity = 0.6,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        text.Children.Add(name);
        text.Children.Add(when);

        var open = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { thumbBox, text } },
        };
        UiHelpers.MakeBorderless(open);
        ToolTipService.SetToolTip(open, "打开 " + item.Path);
        open.Click += (_, _) => UiHelpers.OpenFile(item.Path);

        var copy = UiHelpers.IconButton(UiHelpers.GlyphCopy, 30, 13, item.IsVideo ? "复制文件" : "复制图片");
        copy.Click += async (_, _) =>
        {
            var ok = await UiHelpers.CopyFileAsync(item.Path, item.IsVideo);
            if (!ok) _plugin.Recent.RemoveMissing();
        };
        var folder = UiHelpers.IconButton(UiHelpers.GlyphFolder, 30, 13, "在文件夹中显示");
        folder.Click += (_, _) => UiHelpers.RevealInFolder(item.Path);

        // 标注 (images) / 导出 GIF (videos)
        Button extra;
        if (item.IsVideo)
        {
            extra = GifButton(item.Path);
        }
        else
        {
            extra = UiHelpers.IconButton(UiHelpers.GlyphEdit, 30, 13, "标注");
            extra.Click += async (_, _) => await _plugin.OpenEditorAsync(item.Path);
        }

        var row = new Grid { ColumnSpacing = 2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(open);
        Grid.SetColumn(extra, 1);
        Grid.SetColumn(copy, 2);
        Grid.SetColumn(folder, 3);
        row.Children.Add(extra);
        row.Children.Add(copy);
        row.Children.Add(folder);
        return row;
    }

    /// <summary>
    /// "GIF" text button: click exports (shows the percentage while running), a second click cancels.
    /// State survives a flyout rebuild through <see cref="SnipRecordPlugin.IsExportingGif"/> + <see cref="GifProgress"/>.
    /// </summary>
    private Button GifButton(string mp4)
    {
        var text = new TextBlock { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            MinWidth = 36,
            Height = 30,
            Padding = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = text,
        };
        UiHelpers.MakeBorderless(button);
        _gifButtons[mp4] = (button, text);
        UpdateGifButton(mp4);

        button.Click += async (_, _) =>
        {
            if (_plugin.IsExportingGif(mp4))
            {
                _plugin.CancelGif(mp4);
                return;
            }
            GifProgress[mp4] = 0;
            UpdateGifButtonEverywhere(mp4);
            var progress = new Progress<double>(v =>
            {
                if (!GifProgress.ContainsKey(mp4)) return; // already finished
                GifProgress[mp4] = v;
                UpdateGifButtonEverywhere(mp4);
            });
            try
            {
                await _plugin.ExportGifAsync(mp4, progress); // result / errors go to the status line
            }
            finally
            {
                GifProgress.Remove(mp4);
                UpdateGifButtonEverywhere(mp4);
            }
        };
        return button;
    }

    private static void UpdateGifButtonEverywhere(string mp4)
    {
        foreach (var w in Instances.ToArray())
        {
            if (w.TryGetTarget(out var view)) view.UpdateGifButton(mp4);
            else Instances.Remove(w);
        }
    }

    private void UpdateGifButton(string mp4)
    {
        if (!_gifButtons.TryGetValue(mp4, out var b)) return;
        var exporting = _plugin.IsExportingGif(mp4) || GifProgress.ContainsKey(mp4);
        if (exporting)
        {
            var pct = GifProgress.TryGetValue(mp4, out var v) ? (int)Math.Round(Math.Clamp(v, 0, 1) * 100) : 0;
            b.Text.Text = pct + "%";
            ToolTipService.SetToolTip(b.Button, "正在导出 GIF，点击取消");
        }
        else
        {
            b.Text.Text = "GIF";
            ToolTipService.SetToolTip(b.Button, "导出 GIF");
        }
    }

    private void RenderColors()
    {
        _colors.Children.Clear();
        var colors = _plugin.Recent.Colors;
        _colorsEmpty.Visibility = colors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var hex in colors.Take(10))
        {
            if (UiHelpers.ParseHex(hex) is not { } c) continue;
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(1),
                BorderBrush = UiHelpers.TryBrush("ControlStrokeColorDefaultBrush"),
            };
            swatch.Resources["ButtonBackground"] = new SolidColorBrush(c);
            swatch.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(c);
            swatch.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(c);
            ToolTipService.SetToolTip(swatch, $"{ColorPickerService.Format(c, _plugin.Settings.ColorFormat)}（点击复制）");
            swatch.Click += (_, _) => _plugin.CopyColor(hex);
            _colors.Children.Add(swatch);
        }
    }

    private static ToggleSwitch SmallToggle(bool isOn) => new()
    {
        IsOn = isOn,
        OnContent = "",
        OffContent = "",
        MinWidth = 0,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static Grid ToggleRow(string glyph, string label, ToggleSwitch toggle)
    {
        var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(UiHelpers.Icon(glyph, 14));
        labelPanel.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labelPanel);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private static UIElement LabeledRow(string label, UIElement control)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static UIElement HeaderWithClear(string title, Action clear)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, Style = UiHelpers.TryStyle("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });
        var button = new HyperlinkButton { Content = "清空", FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
        button.Click += (_, _) =>
        {
            try { clear(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
        };
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }
}

/// <summary>Minimal segmented control: a row of ToggleButtons with exactly one checked.</summary>
internal sealed class Segmented : Grid
{
    private readonly List<ToggleButton> _buttons = new();
    private int _selected;

    public Segmented(IReadOnlyList<string> labels, int selected)
    {
        ColumnSpacing = 4;
        _selected = Math.Clamp(selected, 0, labels.Count - 1);
        for (var i = 0; i < labels.Count; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var index = i;
            var b = new ToggleButton
            {
                Content = labels[i],
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsChecked = i == _selected,
            };
            b.Click += (_, _) => Select(index);
            SetColumn(b, i);
            Children.Add(b);
            _buttons.Add(b);
        }
    }

    public int SelectedIndex => _selected;

    public event Action<int>? SelectionChanged;

    public void Select(int index)
    {
        _selected = Math.Clamp(index, 0, _buttons.Count - 1);
        for (var i = 0; i < _buttons.Count; i++) _buttons[i].IsChecked = i == _selected;
        SelectionChanged?.Invoke(_selected);
    }
}
