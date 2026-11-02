using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SnipRecord.ExtensionApp.Services;
using SnipRecord.ExtensionApp.Services.Editor;
using Windows.Graphics;
using Windows.UI;

namespace SnipRecord.ExtensionApp.Views.Editor;

public enum EditorSaveMode
{
    /// <summary>Ctrl+S writes "&lt;name&gt;_edited.png" next to the original (unique name, reused within the session).</summary>
    EditedCopy,
    /// <summary>Ctrl+S overwrites the original file (encoded per its extension).</summary>
    Overwrite,
}

public sealed class AnnotationEditorOptions
{
    public EditorSaveMode DefaultSave { get; init; } = EditorSaveMode.EditedCopy;
    /// <summary>Add saved files to RecentStore.Shared (shows up in the flyout's recent list).</summary>
    public bool AddToRecent { get; init; } = true;
}

/// <summary>
/// Screenshot annotation editor: a top-level WinUI 3 window (Mica, per-monitor DPI) hosting a
/// Win2D CanvasControl. Call <see cref="OpenAsync"/> on the UI thread.
/// </summary>
public sealed partial class AnnotationEditor
{
    private static readonly Dictionary<string, AnnotationEditor> OpenEditors = new(StringComparer.OrdinalIgnoreCase);

    private const double Pad = 12;              // DIP around the image
    private const double ToolbarHeight = 88;    // DIP, two toolbar rows
    private const double MinWidthDip = 820, MinHeightDip = 420;

    private static readonly Color[] Palette =
    {
        Color.FromArgb(0xFF, 0xE8, 0x11, 0x23), // 红
        Color.FromArgb(0xFF, 0xFF, 0xB9, 0x00), // 黄
        Color.FromArgb(0xFF, 0x10, 0xB9, 0x5A), // 绿
        Color.FromArgb(0xFF, 0x00, 0x78, 0xD4), // 蓝
        Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F), // 黑
        Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), // 白
    };
    private static readonly string[] PaletteNames = { "红色", "黄色", "绿色", "蓝色", "黑色", "白色" };
    // per width level (细/中/粗), in DIP at the current zoom → converted to image pixels when used
    private static readonly float[] StrokeDip = { 2f, 4f, 7f };
    private static readonly float[] FontDip = { 14f, 20f, 30f };
    private static readonly float[] NumberRadiusDip = { 10f, 13f, 17f };
    private static readonly float[] MosaicDip = { 6f, 10f, 16f };

    private readonly AnnotationEditorOptions _options;
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly Grid _root;
    private readonly CursorGrid _canvasHost;
    private readonly CanvasControl _canvas;
    private readonly Canvas _overlay;
    private readonly EditorDocument _doc;
    private readonly CommandStack _stack;
    private readonly AnnotationRenderer _renderer;
    private readonly byte[] _pixels;   // original pixels for device-lost rebuilds (never re-read the file: it may have been overwritten)
    private readonly Dictionary<EditorTool, ToggleButton> _toolButtons = new();
    private readonly List<(Button Button, Border Ring)> _swatches = new();
    private readonly ToggleButton[] _widthButtons = new ToggleButton[3];
    private Button _undoButton = null!, _redoButton = null!;
    private TextBlock _status = null!, _sizeText = null!;
    private Ellipse _customSwatch = null!;
    private EditorTool _tool = EditorTool.Pen;
    private Color _color = Palette[0];
    private int _widthLevel = 1;
    private string? _editedCopyPath;
    private bool _forceClose, _closed, _confirming;

    public string ImagePath { get; }
    /// <summary>Last path written by a save (null until saved).</summary>
    public string? SavedPath { get; private set; }
    public bool IsDirty => _stack.IsDirty;
    public Window Window => _window;
    /// <summary>Completes when the window closes, with the last saved path (or null).</summary>
    public Task<string?> Completion => _completion.Task;
    /// <summary>Raised on the UI thread after every successful save (path written).</summary>
    public event EventHandler<string>? Saved;
    public event EventHandler? Closed;

    /// <summary>
    /// Loads the image (throws FileNotFoundException, or the decoder's exception for unreadable images),
    /// then opens and activates the editor window. Opening a path that is already open brings that window
    /// to the front. Must be called on a thread with a DispatcherQueue (the widget's UI thread).
    /// </summary>
    public static async Task<AnnotationEditor> OpenAsync(string imagePath, AnnotationEditorOptions? options = null)
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is null)
            throw new InvalidOperationException("AnnotationEditor.OpenAsync must be called on the UI thread.");
        var full = System.IO.Path.GetFullPath(imagePath);
        if (OpenEditors.TryGetValue(full, out var existing)) { existing.Activate(); return existing; }
        if (!File.Exists(full)) throw new FileNotFoundException("找不到图片文件", full);

        var bitmap = await AnnotationRenderer.LoadAsync(CanvasDevice.GetSharedDevice(), full);
        if (OpenEditors.TryGetValue(full, out existing)) { bitmap.Dispose(); existing.Activate(); return existing; }
        var editor = new AnnotationEditor(full, bitmap, options ?? new AnnotationEditorOptions());
        OpenEditors[full] = editor;
        editor.Activate();
        return editor;
    }

    private AnnotationEditor(string path, CanvasBitmap bitmap, AnnotationEditorOptions options)
    {
        ImagePath = path;
        _options = options;
        _renderer = new AnnotationRenderer(bitmap);
        _pixels = bitmap.GetPixelBytes();
        _doc = new EditorDocument((int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
        _stack = new CommandStack(_doc);

        _window = new Window { Title = $"图片标注 - {System.IO.Path.GetFileName(path)}" };
        _appWindow = _window.AppWindow;
        var mica = MicaController.IsSupported();
        if (mica) _window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };

        _root = new Grid
        {
            Background = mica ? null : EditorUi.TryBrush("SolidBackgroundFillColorBaseBrush") ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(BuildToolbar());

        _canvas = new CanvasControl { UseSharedDevice = true, IsTabStop = true, ClearColor = Color.FromArgb(0, 0, 0, 0) };
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += OnCreateResources;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerReleased;
        _canvas.PointerCaptureLost += OnPointerReleased;
        _canvas.DoubleTapped += OnDoubleTapped;
        _overlay = new Canvas();      // null Background → only the inline TextBox takes input
        _canvasHost = new CursorGrid();
        _canvasHost.Children.Add(_canvas);
        _canvasHost.Children.Add(_overlay);
        Grid.SetRow(_canvasHost, 1);
        _root.Children.Add(_canvasHost);
        _root.KeyDown += OnKeyDown;

        _stack.Changed += (_, _) => { UpdateCommandState(); _canvas.Invalidate(); };

        _window.Content = _root;
        _root.Loaded += (_, _) =>
        {
            _root.XamlRoot.Changed += (_, _) => { CommitText(); _canvas.Invalidate(); };   // DPI / size change
            _canvas.Focus(FocusState.Programmatic);
        };
        _appWindow.Closing += OnClosing;
        _window.Closed += OnClosed;

        PlaceWindow();
        SelectTool(_tool);
        SelectColor(_color);
        SelectWidth(_widthLevel);
        UpdateCommandState();
    }

    public void Activate()
    {
        if (_closed) return;
        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        _window.Activate();
        try { SetForegroundWindow(Win32Interop.GetWindowFromWindowId(_appWindow.Id)); } catch { }
    }

    /// <summary>Close the window; force = skip the unsaved-changes prompt.</summary>
    public void Close(bool force = false)
    {
        if (_closed) return;
        _forceClose |= force;
        _window.Close();
    }

    // ---------------------------------------------------------------- toolbar

    private FrameworkElement BuildToolbar()
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        AddTool(tools, EditorTool.Select, "\uE7C9", "选择/移动 (V)");
        AddTool(tools, EditorTool.Pen, "\uED63", "画笔 (P)");
        AddTool(tools, EditorTool.Highlighter, "\uED64", "荧光笔 (H)");
        AddTool(tools, EditorTool.Arrow, "\uF0AF", "箭头 (A，按住 Shift 对齐 45°)");
        AddTool(tools, EditorTool.Line, "\uE738", "直线 (L，按住 Shift 对齐 45°)");
        AddTool(tools, EditorTool.Rectangle, "\uE739", "矩形 (R，按住 Shift 画正方形)");
        AddTool(tools, EditorTool.Ellipse, "\uEA3A", "椭圆 (E，按住 Shift 画圆)");
        AddTool(tools, EditorTool.Text, "\uE8D2", "文字 (T)");
        AddTool(tools, EditorTool.Mosaic, "\uF0E2", "马赛克 (M)");
        AddTool(tools, EditorTool.Number, null, "序号标记 (N)");
        AddTool(tools, EditorTool.Crop, "\uE7A8", "裁剪 (C，拖出区域后松开即裁剪)");
        tools.Children.Add(Separator());
        _undoButton = EditorUi.IconButton("\uE7A7", 36, 16, "撤销 (Ctrl+Z)");
        _undoButton.Click += (_, _) => Undo();
        _redoButton = EditorUi.IconButton("\uE7A6", 36, 16, "重做 (Ctrl+Y)");
        _redoButton.Click += (_, _) => Redo();
        var delete = EditorUi.IconButton("\uE74D", 36, 16, "删除所选 (Delete)");
        delete.Click += (_, _) => DeleteSelected();
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        tools.Children.Add(delete);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = IconText("\uE8C8", "复制"), Height = 32 };
        ToolTipService.SetToolTip(copy, "复制到剪贴板 (Ctrl+C)");
        copy.Click += async (_, _) => await CopyAsync();
        var save = new SplitButton { Content = IconText("\uE74E", "保存"), Height = 32 };
        ToolTipService.SetToolTip(save, _options.DefaultSave == EditorSaveMode.Overwrite ? "覆盖原图 (Ctrl+S)" : "保存为副本 _edited.png (Ctrl+S)");
        save.Click += async (_, _) => await SaveAsync(_options.DefaultSave);
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("\uEA35", "保存为副本 (_edited.png)", async () => await SaveAsync(EditorSaveMode.EditedCopy)));
        menu.Items.Add(MenuItem("\uE74E", "覆盖原图", async () => await SaveAsync(EditorSaveMode.Overwrite)));
        menu.Items.Add(MenuItem("\uE792", "另存为… (Ctrl+Shift+S)", async () => await SaveAsAsync()));
        save.Flyout = menu;
        var copyClose = new Button { Content = IconText("\uE8FB", "复制并关闭"), Height = 32, Style = EditorUi.TryStyle("AccentButtonStyle") };
        copyClose.Click += async (_, _) => { if (await CopyAsync()) Close(force: true); };
        actions.Children.Add(copy);
        actions.Children.Add(save);
        actions.Children.Add(copyClose);

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(tools);
        Grid.SetColumn(actions, 1);
        top.Children.Add(actions);

        // row 2: palette, width, size + status
        var props = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var i = 0; i < Palette.Length; i++) props.Children.Add(Swatch(Palette[i], PaletteNames[i]));
        _customSwatch = new Ellipse
        {
            Width = 18, Height = 18, Fill = new SolidColorBrush(Colors.Transparent),
            Stroke = EditorUi.TryBrush("ControlStrongStrokeColorDefaultBrush") ?? new SolidColorBrush(Colors.Gray), StrokeThickness = 1,
        };
        var customButton = SwatchButton(new Grid { Children = { _customSwatch, EditorUi.Icon("\uE790", 11) } }, "自定义颜色", out var customRing);
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = true, Color = _color };
        var ok = new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Right, Style = EditorUi.TryStyle("AccentButtonStyle") };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { picker, ok } } };
        ok.Click += (_, _) => { flyout.Hide(); _customSwatch.Fill = new SolidColorBrush(picker.Color); ApplyColor(picker.Color); };
        customButton.Click += (_, _) => { picker.Color = _color; flyout.ShowAt(customButton); };
        _swatches.Add((customButton, customRing));
        props.Children.Add(customButton);
        props.Children.Add(Separator());
        string[] widthNames = { "细", "中", "粗" };
        double[] widthBars = { 1.5, 3, 5 };
        for (var i = 0; i < 3; i++)
        {
            var level = i;
            var bar = new Rectangle
            {
                Width = 14, Height = widthBars[i], RadiusX = 1, RadiusY = 1, VerticalAlignment = VerticalAlignment.Center,
                Fill = EditorUi.TryBrush("TextFillColorPrimaryBrush") ?? new SolidColorBrush(Colors.Gray),
            };
            var b = new ToggleButton
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { bar, new TextBlock { Text = widthNames[i], VerticalAlignment = VerticalAlignment.Center } } },
                Height = 30, Padding = new Thickness(8, 0, 8, 0),
            };
            ToolTipService.SetToolTip(b, $"粗细：{widthNames[i]} ({i + 1})");
            b.Click += (_, _) => ApplyWidth(level);
            _widthButtons[i] = b;
            props.Children.Add(b);
        }
        props.Children.Add(Separator());
        _sizeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 12, Margin = new Thickness(4, 0, 0, 0) };
        props.Children.Add(_sizeText);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
        props.Children.Add(_status);

        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(Pad, 8, Pad, 4), Children = { top, props } };
        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
    }

    private void AddTool(Panel panel, EditorTool tool, string? glyph, string tooltip)
    {
        object content = glyph != null
            ? EditorUi.Icon(glyph, 16)
            : new Border   // 序号: a small "1" in a circle (no glyph that exists on both Win10 MDL2 and Win11 Fluent)
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5),
                BorderBrush = EditorUi.TryBrush("TextFillColorPrimaryBrush") ?? new SolidColorBrush(Colors.Gray),
                Child = new TextBlock { Text = "1", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        var b = new ToggleButton { Content = content, Width = 36, Height = 34, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(b, tooltip);
        b.Click += (_, _) => SelectTool(tool);
        _toolButtons[tool] = b;
        panel.Children.Add(b);
    }

    private Button Swatch(Color c, string name)
    {
        var dot = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)), StrokeThickness = 1 };
        var b = SwatchButton(dot, name, out var ring);
        b.Tag = c;
        b.Click += (_, _) => ApplyColor(c);
        _swatches.Add((b, ring));
        return b;
    }

    private static Button SwatchButton(UIElement content, string tooltip, out Border ring)
    {
        ring = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), Child = content };
        var b = new Button { Content = ring, Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(15) };
        EditorUi.MakeBorderless(b);
        ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    private static UIElement Separator() => new Rectangle
    {
        Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        Fill = EditorUi.TryBrush("DividerStrokeColorDefaultBrush") ?? new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
    };

    private static StackPanel IconText(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { EditorUi.Icon(glyph, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
    };

    private static MenuFlyoutItem MenuItem(string glyph, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph, FontFamily = EditorUi.IconFont } };
        item.Click += (_, _) => action();
        return item;
    }

    private void SelectTool(EditorTool tool)
    {
        CommitText();
        if (tool != EditorTool.Select) _selected = null;
        _tool = tool;
        foreach (var (t, b) in _toolButtons) b.IsChecked = t == tool;
        _canvasHost.SetCursor(tool switch
        {
            EditorTool.Select => Microsoft.UI.Input.InputSystemCursorShape.Arrow,
            EditorTool.Text => Microsoft.UI.Input.InputSystemCursorShape.IBeam,
            EditorTool.Number => Microsoft.UI.Input.InputSystemCursorShape.Hand,
            _ => Microsoft.UI.Input.InputSystemCursorShape.Cross,
        });
        _canvas.Invalidate();
    }

    private void SelectColor(Color c)
    {
        _color = c;
        var accent = EditorUi.TryBrush("AccentFillColorDefaultBrush") ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        var inPalette = Palette.Contains(c);
        foreach (var (b, ring) in _swatches)
            ring.BorderBrush = (b.Tag is Color sc ? sc == c : !inPalette) ? accent : null;
    }

    private void SelectWidth(int level)
    {
        _widthLevel = Math.Clamp(level, 0, 2);
        for (var i = 0; i < 3; i++) _widthButtons[i].IsChecked = i == _widthLevel;
    }

    /// <summary>Palette click: sets the current colour and restyles the selected annotation / the text being edited.</summary>
    private void ApplyColor(Color c)
    {
        SelectColor(c);
        if (_editBox != null) { _editBox.Foreground = new SolidColorBrush(c); return; }
        if (Selected is { } sel and not MosaicAnnotation)
        {
            var after = sel.WithStyle(c, null);
            if (after != sel) _stack.Execute(new ReplaceCommand(sel, after));
        }
        _canvas.Focus(FocusState.Programmatic);
    }

    private void ApplyWidth(int level)
    {
        SelectWidth(level);
        if (_editBox != null) { _editBox.FontSize = FontPx() * _scale; return; }
        if (Selected is { } sel)
        {
            Annotation after = sel switch
            {
                TextAnnotation t => t with { FontSize = FontPx(), Size = AnnotationRenderer.MeasureText(CanvasDevice.GetSharedDevice(), t.Text, FontPx()) },
                NumberAnnotation n => n with { Radius = Px(NumberRadiusDip) },
                MosaicAnnotation m => m with { Block = MosaicPx() },
                _ => sel.WithStyle(sel.Color, StrokePx()),
            };
            if (after != sel) _stack.Execute(new ReplaceCommand(sel, after));
        }
        _canvas.Focus(FocusState.Programmatic);
    }

    // sizes are chosen in DIP at the current zoom, stored in image pixels
    private float Px(float[] dipTable) => (float)Math.Max(1, Math.Round(dipTable[_widthLevel] / Math.Max(_scale, 0.01f) * 2) / 2);
    private float StrokePx() => Px(StrokeDip);
    private float FontPx() => (float)Math.Max(8, Math.Round(FontDip[_widthLevel] / Math.Max(_scale, 0.01f)));
    private int MosaicPx() => (int)Math.Max(3, Math.Round(MosaicDip[_widthLevel] / Math.Max(_scale, 0.01f)));

    private void UpdateCommandState()
    {
        _undoButton.IsEnabled = _stack.CanUndo;
        _redoButton.IsEnabled = _stack.CanRedo;
        _window.Title = $"{(_stack.IsDirty ? "● " : "")}图片标注 - {System.IO.Path.GetFileName(ImagePath)}";
        _sizeText.Text = $"{(int)_doc.Crop.Width} × {(int)_doc.Crop.Height} px";
    }

    private void ShowStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = error ? new SolidColorBrush(EditorUi.ErrorRed) : EditorUi.TryBrush("TextFillColorSecondaryBrush") ?? new SolidColorBrush(Colors.Gray);
    }

    // ---------------------------------------------------------------- commands

    private void Undo() { CommitText(); if (_stack.Undo()) ValidateSelection(); }
    private void Redo() { CommitText(); if (_stack.Redo()) ValidateSelection(); }
    private void ValidateSelection() { if (_selected is { } id && _doc.Find(id) is null) _selected = null; _canvas.Invalidate(); }

    private void DeleteSelected()
    {
        if (Selected is { } sel) { _stack.Execute(new RemoveCommand(sel)); _selected = null; }
    }

    private async Task<bool> CopyAsync()
    {
        CommitText();
        try
        {
            await _renderer.CopyToClipboardAsync(_doc);
            ShowStatus("已复制到剪贴板");
            return true;
        }
        catch (Exception ex) { ShowStatus("复制失败：" + ex.Message, true); return false; }
    }

    private async Task<bool> SaveAsync(EditorSaveMode mode)
    {
        CommitText();
        var target = mode == EditorSaveMode.Overwrite
            ? ImagePath
            : _editedCopyPath ??= UniquePath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ImagePath)!,
                System.IO.Path.GetFileNameWithoutExtension(ImagePath) + "_edited.png"));
        return await SaveToAsync(target);
    }

    private async Task<bool> SaveAsAsync()
    {
        CommitText();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(_appWindow.Id)
            {
                SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(ImagePath) + "_edited",
                DefaultFileExtension = ".png",
            };
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG 图片", new List<string> { ".jpg", ".jpeg" });
            picker.FileTypeChoices.Add("BMP 图片", new List<string> { ".bmp" });
            var result = await picker.PickSaveFileAsync();
            if (result is null || string.IsNullOrEmpty(result.Path)) return false;
            return await SaveToAsync(result.Path);
        }
        catch (Exception ex) { ShowStatus("另存为失败：" + ex.Message, true); return false; }
    }

    private async Task<bool> SaveToAsync(string path)
    {
        try
        {
            await _renderer.SaveAsync(_doc, path);
            SavedPath = path;
            _stack.MarkSaved();
            ShowStatus("已保存：" + System.IO.Path.GetFileName(path));
            if (_options.AddToRecent) try { RecentStore.Shared.AddCapture(path, false); } catch { }
            Saved?.Invoke(this, path);
            return true;
        }
        catch (Exception ex) { ShowStatus("保存失败：" + ex.Message, true); return false; }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (var i = 2; ; i++) { var p = System.IO.Path.Combine(dir, $"{name} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }

    // ---------------------------------------------------------------- window lifecycle

    private void RequestClose()
    {
        if (!_stack.IsDirty) Close(force: true);
        else _ = ConfirmCloseAsync();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || !_stack.IsDirty) return;
        args.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_confirming || _root.XamlRoot is null) return;
        _confirming = true;
        try
        {
            CommitText();
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = "保存更改？",
                Content = "标注尚未保存，关闭后将丢失。",
                PrimaryButtonText = _options.DefaultSave == EditorSaveMode.Overwrite ? "保存 (覆盖原图)" : "保存副本",
                SecondaryButtonText = "不保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            var r = await dialog.ShowAsync();
            if (r == ContentDialogResult.None) return;
            if (r == ContentDialogResult.Primary && !await SaveAsync(_options.DefaultSave)) return;
            Close(force: true);
        }
        finally { _confirming = false; }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true;
        OpenEditors.Remove(ImagePath);
        try { _canvas.RemoveFromVisualTree(); } catch { }   // Win2D: release the control's device resources
        _renderer.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
        _completion.TrySetResult(SavedPath);
    }

    /// <summary>Client size: image at 1:1 physical pixels plus chrome, capped to 80% of the work area of the monitor under the cursor.</summary>
    private void PlaceWindow()
    {
        GetCursorPos(out var pt);
        var work = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary).WorkArea;
        double scale = 1;
        try { if (GetDpiForMonitor(MonitorFromPoint(pt, 2 /*NEAREST*/), 0 /*EFFECTIVE*/, out var dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0; } catch { }

        var chromeW = 2 * Pad * scale;
        var chromeH = (ToolbarHeight + 2 * Pad) * scale;
        double imgW = _doc.PixelWidth, imgH = _doc.PixelHeight;
        double maxW = work.Width * 0.8, maxH = work.Height * 0.8;
        var fit = Math.Clamp(Math.Min((maxW - chromeW) / imgW, (maxH - chromeH) / imgH), 0.05, 1);
        var w = (int)Math.Ceiling(imgW * fit + chromeW);
        var h = (int)Math.Ceiling(imgH * fit + chromeH);
        w = Math.Min(Math.Max(w, (int)(MinWidthDip * scale)), work.Width);
        h = Math.Min(Math.Max(h, (int)(MinHeightDip * scale)), work.Height);

        _appWindow.Move(new PointInt32(work.X + 40, work.Y + 40));   // land on the target monitor first so DPI matches
        _appWindow.ResizeClient(new SizeInt32(w, h));
        var outer = _appWindow.Size;
        _appWindow.Move(new PointInt32(work.X + Math.Max(0, (work.Width - outer.Width) / 2), work.Y + Math.Max(0, (work.Height - outer.Height) / 2)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Grid that exposes ProtectedCursor so the canvas area shows a per-tool cursor.</summary>
    private sealed partial class CursorGrid : Grid
    {
        public void SetCursor(Microsoft.UI.Input.InputSystemCursorShape shape)
        {
            try { ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(shape); } catch { }
        }
    }
}
