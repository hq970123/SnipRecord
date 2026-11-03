using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SnipRecord.ExtensionApp.Services;
using SnipRecord.ExtensionApp.Services.FullScreen;
using SnipRecord.ExtensionApp.Services.Gif;
using SnipRecord.ExtensionApp.Services.Hotkeys;
using SnipRecord.ExtensionApp.Views;
using SnipRecord.ExtensionApp.Views.Editor;
using WidBar.SDK;

namespace SnipRecord.ExtensionApp;

// 截图录屏: taskbar buttons for a screenshot (via Snipping Tool), rectangle recording (Snipping
// Tool) / window recording (own Windows.Graphics.Capture recorder) and a screen colour picker.
// The plugin is also the controller the views call into.
public sealed class SnipRecordPlugin :
    WidgetPluginBase,
    IConfigurableWidgetPlugin,
    IWidgetFlyoutLifecycle
{
    public const string PluginId = "com.hq970123.sniprecord";

    public override string Id => PluginId;
    public override string Name => "截图录屏";
    public override string Version => "1.0.0";

    public override int PreviewLogicalWidth => _settings.PreviewWidth;
    public override int FlyoutWidth => 380;
    public override int FlyoutHeight => 560;
    public override WidgetFlyoutBackdrop FlyoutBackdrop => WidgetFlyoutBackdrop.Acrylic;

    private AppSettings _settings = new();
    private RecentStore _recent = RecentStore.Shared;
    private ColorPickerService? _picker;
    private WindowRecorder? _recorder;
    private DispatcherQueue? _ui;
    private DispatcherTimer? _timer;
    private PreviewView? _preview;
    private FlyoutView? _flyout;
    private bool _flyoutShown;
    private bool _busy;
    private bool _disposed;
    private GifRecordingFlow? _gif;

    // Global hotkeys are process-wide (GlobalHotkeyService.Shared): exactly one plugin instance owns them.
    private static readonly object HotkeyGate = new();
    private static readonly List<SnipRecordPlugin> LiveInstances = new();
    private static SnipRecordPlugin? _hotkeyOwner;
    private string? _appliedHotkeyKey;   // signature of the last applied hotkey settings (avoid re-registering on every draft)
    private int _hotkeyVersion;          // drops stale RegisterAsync results

    /// <summary>Current settings (live; replaced when the settings page changes them).</summary>
    public AppSettings Settings => _settings;
    public RecentStore Recent => _recent;
    public bool IsWindowRecording => _recorder?.IsRecording ?? false;
    public bool IsPaused => _recorder?.IsPaused ?? false;
    public TimeSpan RecordingElapsed => _recorder?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Non-fatal recorder warning (Chinese) from the last window recording.</summary>
    public string? RecorderWarning { get; private set; }

    /// <summary>Last status / error line for the flyout (Chinese), null = nothing to show.</summary>
    public string? StatusMessage { get; private set; }
    public bool StatusIsError { get; private set; }

    /// <summary>Recording state, timer tick, status message or warnings changed (UI thread).</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Result of the last hotkey registration (per action: registered / in use / disabled …), null when this
    /// instance does not own the hotkeys yet or hotkeys are turned off. Show <c>HotkeyRegistration.StatusText</c>.
    /// </summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration>? HotkeyStatus { get; private set; }

    /// <summary><see cref="HotkeyStatus"/> changed (UI thread).</summary>
    public event Action? HotkeyStatusChanged;

    /// <summary>A GIF conversion is queued or running (any file).</summary>
    public bool IsGifBusy => _gif?.IsBusy ?? false;

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _settings = AppSettings.FromJson(context.SettingsJson);
        // The flyout's microphone / GIF toggles can only persist to the shared settings.json
        // (the flyout has no WidBar settings context), so that file wins for these values.
        try
        {
            if (File.Exists(AppSettings.SharedFilePath))
            {
                var shared = AppSettings.LoadShared();
                _settings.RecordMicrophone = shared.RecordMicrophone;
                _settings.RecordAsGif = shared.RecordAsGif;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] reading shared settings failed: {ex.Message}");
        }
        _settings.SaveShared();
        await base.InitializeAsync(context);

        _ui = DispatcherQueue.GetForCurrentThread();
        _recent = RecentStore.Shared;
        _recent.Changed += OnRecentChanged;
        _picker = new ColorPickerService(_recent, () => _settings);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RaiseStateChanged();

        SnippingToolLauncher.DiscoverCompleted += OnDiscoverCompleted;
        ActivationHub.ProtocolActivated += OnProtocolActivated;

        EnsureGifFlow();
        InitHotkeys();

        // Ask Snipping Tool whether it supports the ms-screenclip 1.2 API (answer arrives as a protocol activation).
        if (SnippingToolLauncher.Capabilities is null || DateTimeOffset.Now - SnippingToolLauncher.Capabilities.CheckedAt > TimeSpan.FromDays(1))
        {
            _ = SnippingToolLauncher.DiscoverAsync();
        }
    }

    public override UIElement? CreatePreviewContent()
    {
        _preview = new PreviewView(this);
        return _preview;
    }

    public override UIElement? CreateFlyoutContent()
    {
        _flyout = new FlyoutView(this);
        return _flyout;
    }

    public UIElement? CreateSettingsContent(IWidgetSettingsContext context) => new SettingsView(context);

    public override void OnSettingsDraftChanged(string settingsJson)
    {
        _settings = AppSettings.FromJson(settingsJson);
        _settings.SaveShared();
        _preview?.Refresh();
        if (_flyoutShown) _flyout?.Refresh();
        ApplyHotkeys(force: false);
    }

    public void OnFlyoutShown()
    {
        _flyoutShown = true;
        _recent.RemoveMissing();
        _flyout?.Refresh();
    }

    public void OnFlyoutHidden()
    {
        _flyoutShown = false;
    }

    // ---------------- actions (called from the views, UI thread) ----------------

    /// <summary>Screenshot through Snipping Tool. null = the default mode from settings.</summary>
    public async Task SnipAsync(SnipMode? mode = null)
    {
        if ((mode ?? _settings.DefaultSnipMode) == SnipMode.FullScreen)
        {
            await FullScreenAsync();
            return;
        }
        CloseFlyout();
        var ok = await SnippingToolLauncher.LaunchAsync(mode ?? _settings.DefaultSnipMode, _settings);
        if (!ok) SetStatus("无法启动截图工具，请确认已安装最新版 Windows 截图工具。", error: true);
    }

    /// <summary>
    /// Own full-screen capture (no Snipping Tool): monitor under the cursor, or all monitors when
    /// <paramref name="target"/> is null and <see cref="AppSettings.FullScreenAllMonitors"/> is set.
    /// Saves a PNG, copies it, adds it to recent; opens the editor when <see cref="AppSettings.OpenEditorAfterCapture"/>.
    /// </summary>
    public async Task FullScreenAsync(FullScreenTarget? target = null)
    {
        if (FullScreenCapture.IsCapturing) return;
        var t = target ?? (_settings.FullScreenAllMonitors ? FullScreenTarget.AllMonitors : FullScreenTarget.CurrentMonitor);
        CloseFlyout();
        try
        {
            await Task.Delay(200); // let the flyout fade out so it is not in the shot
            var path = await FullScreenCapture.CaptureAsync(t, _settings, _recent);
            await OnUiAsync(() =>
            {
                if (path is null)
                {
                    SetStatus(FullScreenCapture.LastError ?? "全屏截图失败。", error: true);
                }
                else
                {
                    var msg = "截图已保存：" + Path.GetFileName(path);
                    if (!string.IsNullOrWhiteSpace(FullScreenCapture.LastWarning)) msg += "（" + FullScreenCapture.LastWarning + "）";
                    SetStatus(msg, error: false);
                }
            });
            if (path is not null && _settings.OpenEditorAfterCapture)
            {
                await OnUiTaskAsync(() => OpenEditorAsync(path));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] full screen failed: {ex}");
            await OnUiAsync(() => SetStatus($"全屏截图失败：{ex.Message}", error: true));
        }
    }

    /// <summary>
    /// Open the annotation editor on <paramref name="imagePath"/> (UI thread). Never throws: failures
    /// (missing / unreadable file) go to the status line. The editor adds saved copies to recent itself.
    /// </summary>
    public async Task OpenEditorAsync(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            SetStatus("没有可标注的截图。", error: true);
            return;
        }
        CloseFlyout();
        try
        {
            await AnnotationEditor.OpenAsync(imagePath);
        }
        catch (FileNotFoundException)
        {
            SetStatus("文件不存在：" + Path.GetFileName(imagePath), error: true);
            try { _recent.RemoveMissing(); } catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] editor failed: {ex}");
            SetStatus($"无法打开标注编辑器：{ex.Message}", error: true);
        }
    }

    /// <summary>Open the editor on the newest existing image in recent captures.</summary>
    public Task OpenEditorOnLastCaptureAsync()
    {
        var item = _recent.Items.FirstOrDefault(i => !i.IsVideo && File.Exists(i.Path));
        if (item is null)
        {
            SetStatus("最近没有可标注的截图。", error: true);
            return Task.CompletedTask;
        }
        return OpenEditorAsync(item.Path);
    }

    /// <summary>
    /// Convert a recording to GIF (MP4 kept) with the GIF settings. Never throws; progress / result also go to the
    /// status line and <see cref="StateChanged"/>. A second call for the same file restarts it.
    /// </summary>
    public async Task<GifConversionResult> ExportGifAsync(string mp4Path, IProgress<double>? progress = null)
    {
        var flow = EnsureGifFlow();
        var r = await flow.ConvertAsync(mp4Path, _settings.GetGifOptions(), deleteMp4: false, progress);
        return r;
    }

    /// <summary>Cancel the GIF conversion of <paramref name="mp4Path"/> (queued or running).</summary>
    public void CancelGif(string mp4Path) => _gif?.Cancel(mp4Path);

    /// <summary>True while <paramref name="mp4Path"/> is queued or being converted to GIF.</summary>
    public bool IsExportingGif(string mp4Path) => _gif?.IsPending(mp4Path) ?? false;

    /// <summary>Rectangle recording through Snipping Tool.</summary>
    public async Task RecordRectangleAsync()
    {
        CloseFlyout();
        var ok = await SnippingToolLauncher.LaunchAsync(SnipMode.Recording, _settings);
        if (!ok) SetStatus("无法启动截图工具录屏，请确认已安装最新版 Windows 截图工具。", error: true);
    }

    /// <summary>Pick a window and record it with our own recorder.</summary>
    public async Task RecordWindowAsync()
    {
        if (IsWindowRecording)
        {
            await StopRecordingAsync();
            return;
        }
        if (_busy) return;
        _busy = true;
        CloseFlyout();
        try
        {
            await Task.Delay(200); // let the flyout disappear before the picker overlay appears
            var recorder = EnsureRecorder();
            var started = await recorder.StartWindowPickAsync(_settings);
            await OnUiAsync(() =>
            {
                if (started)
                {
                    RecorderWarning = recorder.Warning;
                    SetStatus(recorder.Warning, error: false);
                }
                UpdateTimer();
                RaiseStateChanged();
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] window recording failed to start: {ex}");
            await OnUiAsync(() => SetStatus($"无法开始窗口录屏：{ex.Message}", error: true));
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task StopRecordingAsync()
    {
        if (_recorder is null) return;
        try
        {
            await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] stop failed: {ex}");
        }
        await OnUiAsync(() => { UpdateTimer(); RaiseStateChanged(); });
    }

    public async Task PickColorAsync()
    {
        if (_picker is null || _picker.IsPicking) return;
        CloseFlyout();
        try
        {
            var color = await _picker.PickAsync(200);
            if (color is { } c)
            {
                var text = ColorPickerService.Format(c, _settings.ColorFormat);
                await OnUiAsync(() => SetStatus($"已复制颜色 {text}", error: false));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] colour pick failed: {ex}");
            await OnUiAsync(() => SetStatus("取色失败。", error: true));
        }
    }

    /// <summary>Flyout microphone toggle: kept in memory + shared settings.json.</summary>
    public void SetRecordMicrophone(bool value)
    {
        if (_settings.RecordMicrophone == value) return;
        _settings.RecordMicrophone = value;
        _settings.SaveShared();
    }

    /// <summary>Flyout "录成 GIF" toggle: kept in memory + shared settings.json (same path as the microphone toggle).</summary>
    public void SetRecordAsGif(bool value)
    {
        if (_settings.RecordAsGif == value) return;
        _settings.RecordAsGif = value;
        _settings.SaveShared();
    }

    /// <summary>Copy a recent colour (stored as #RRGGBB) in the chosen format; returns the copied text.</summary>
    public string CopyColor(string hex)
    {
        var text = hex;
        if (UiHelpers.ParseHex(hex) is { } c)
        {
            text = ColorPickerService.Format(c, _settings.ColorFormat);
        }
        UiHelpers.CopyText(text);
        SetStatus($"已复制颜色 {text}", error: false);
        return text;
    }

    public void ClearStatus()
    {
        StatusMessage = null;
        StatusIsError = false;
    }

    // ---------------- plumbing ----------------

    private void CloseFlyout()
    {
        if (_flyout is not null) FlyoutCloser.Close(_flyout);
    }

    private WindowRecorder EnsureRecorder()
    {
        if (_recorder is not null) return _recorder;
        var gif = EnsureGifFlow();
        var r = _recorder = new WindowRecorder();
        r.StateChanged += (_, _) => Ui(() => { UpdateTimer(); RaiseStateChanged(); });
        r.Completed += (_, path) => Ui(() =>
        {
            try { _recent.AddCapture(path, isVideo: true); } catch (Exception ex) { Debug.WriteLine(ex); }
            SetStatus($"录屏已保存：{Path.GetFileName(path)}", error: false);
            UpdateTimer();
        });
        r.Failed += (_, ex) => Ui(() =>
        {
            SetStatus($"录屏失败：{ex.Message}", error: true);
            UpdateTimer();
        });
        // Subscribed after our Completed handler, so "录屏已保存" is replaced by the GIF progress when RecordAsGif is on.
        gif.Attach(r);
        return r;
    }

    /// <summary>Created on the UI thread (the flow posts its events to the current SynchronizationContext).</summary>
    private GifRecordingFlow EnsureGifFlow()
    {
        if (_gif is not null) return _gif;
        var flow = _gif = new GifRecordingFlow(
            enabled: () => _settings.RecordAsGif,
            options: () => _settings.GetGifOptions(),
            recent: _recent);
        flow.ExportStarted += (_, mp4) => Ui(() => SetStatus($"正在转换 GIF：{Path.GetFileName(mp4)}", error: false));
        flow.ProgressChanged += (_, p) => Ui(() =>
        {
            var pct = (int)Math.Round(Math.Clamp(p.Value, 0, 1) * 100);
            SetStatus($"正在转换 GIF：{Path.GetFileName(p.Mp4Path)} {pct}%", error: false);
        });
        flow.ExportCompleted += (_, r) => Ui(() =>
            SetStatus("GIF 已保存：" + Path.GetFileName(r.GifPath) + (r.Mp4Deleted ? "（已删除 MP4）" : ""), error: false));
        flow.ExportFailed += (_, r) => Ui(() =>
        {
            if (r.Cancelled) SetStatus("已取消 GIF 导出。", error: false);
            else SetStatus(r.Error ?? "GIF 导出失败。", error: true);
        });
        return flow;
    }

    // ---------------- hotkeys ----------------

    private void InitHotkeys()
    {
        lock (HotkeyGate)
        {
            LiveInstances.Add(this);
            if (_hotkeyOwner is not null) return;
            _hotkeyOwner = this;
        }
        TakeHotkeyOwnership();
    }

    private bool OwnsHotkeys { get { lock (HotkeyGate) return ReferenceEquals(_hotkeyOwner, this); } }

    /// <summary>UI thread of this instance: start the shared service here and apply our settings.</summary>
    private void TakeHotkeyOwnership()
    {
        try
        {
            var svc = GlobalHotkeyService.Shared;
            svc.Start();
            svc.HotkeyPressed -= OnHotkeyPressed;
            svc.HotkeyPressed += OnHotkeyPressed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] hotkey service start failed: {ex}");
            return;
        }
        _appliedHotkeyKey = null;
        ApplyHotkeys(force: true);
    }

    private string HotkeySettingsKey() =>
        _settings.HotkeysEnabled ? "on|" + string.Join(";", _settings.GetHotkeys().ToSettingsMap().OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)) : "off";

    /// <summary>(Re-)register the hotkeys from the current settings when this instance owns them.</summary>
    private async void ApplyHotkeys(bool force)
    {
        if (_disposed || !OwnsHotkeys) return;
        var key = HotkeySettingsKey();
        if (!force && key == _appliedHotkeyKey) return;
        _appliedHotkeyKey = key;
        var version = ++_hotkeyVersion;
        try
        {
            IReadOnlyDictionary<HotkeyAction, HotkeyRegistration>? status = null;
            if (_settings.HotkeysEnabled)
            {
                status = await GlobalHotkeyService.Shared.RegisterAsync(_settings.GetHotkeys());
            }
            else
            {
                await GlobalHotkeyService.Shared.UnregisterAllAsync();
            }
            await OnUiAsync(() =>
            {
                if (version != _hotkeyVersion || _disposed) return;
                HotkeyStatus = status;
                HotkeyStatusChanged?.Invoke();
                var failed = status?.Values.Where(r => r.Status is HotkeyRegistrationStatus.InUse or HotkeyRegistrationStatus.Failed).ToList();
                if (failed is { Count: > 0 })
                {
                    var names = string.Join("、", failed.Select(r => HotkeyBindings.GetDisplayName(r.Action) + " " + r.Hotkey));
                    SetStatus($"部分快捷键不可用（已被占用）：{names}", error: true);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] hotkey registration failed: {ex}");
            _appliedHotkeyKey = null;
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyAction action) => Ui(async () =>
    {
        if (_disposed) return;
        try
        {
            switch (action)
            {
                case HotkeyAction.Snip: await SnipAsync(); break;
                case HotkeyAction.FullScreen: await FullScreenAsync(); break;
                case HotkeyAction.RecordRectangle: await RecordRectangleAsync(); break;
                case HotkeyAction.RecordWindow: await RecordWindowAsync(); break; // toggles stop while recording
                case HotkeyAction.ColorPick: await PickColorAsync(); break;
                case HotkeyAction.OpenEditorOnLastCapture: await OpenEditorOnLastCaptureAsync(); break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] hotkey {action} failed: {ex}");
        }
    });

    /// <summary>Called on dispose: hand the hotkeys to another live instance, or release them.</summary>
    private void ReleaseHotkeys()
    {
        SnipRecordPlugin? next = null;
        bool wasOwner;
        lock (HotkeyGate)
        {
            LiveInstances.Remove(this);
            wasOwner = ReferenceEquals(_hotkeyOwner, this);
            if (!wasOwner) return;
            next = LiveInstances.FirstOrDefault(p => !p._disposed);
            _hotkeyOwner = next;
        }
        try { GlobalHotkeyService.Shared.HotkeyPressed -= OnHotkeyPressed; } catch { }
        HotkeyStatus = null;
        if (next is not null)
        {
            next.Ui(next.TakeHotkeyOwnership);
        }
        else
        {
            try { _ = GlobalHotkeyService.Shared.UnregisterAllAsync(); } catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    private void UpdateTimer()
    {
        if (_timer is null) return;
        if (IsWindowRecording && !_timer.IsEnabled) _timer.Start();
        else if (!IsWindowRecording && _timer.IsEnabled) _timer.Stop();
    }

    private void SetStatus(string? message, bool error)
    {
        StatusMessage = string.IsNullOrWhiteSpace(message) ? null : message;
        StatusIsError = error && StatusMessage is not null;
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        _preview?.Refresh();
        if (_flyoutShown) _flyout?.RefreshState();
        StateChanged?.Invoke();
    }

    private void Ui(Action action)
    {
        var q = _ui;
        if (q is null || q.HasThreadAccess) { action(); return; }
        q.TryEnqueue(() => action());
    }

    private Task OnUiTaskAsync(Func<Task> action)
    {
        var q = _ui;
        if (q is null || q.HasThreadAccess) return action();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!q.TryEnqueue(async () =>
            {
                try { await action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
        {
            tcs.SetResult();
        }
        return tcs.Task;
    }

    private Task OnUiAsync(Action action)
    {
        var q = _ui;
        if (q is null || q.HasThreadAccess) { action(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!q.TryEnqueue(() =>
            {
                try { action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }))
        {
            tcs.SetResult();
        }
        return tcs.Task;
    }

    private void OnRecentChanged(object? sender, EventArgs e) => Ui(() => _flyout?.Refresh());

    private void OnDiscoverCompleted(SnippingToolCapabilities caps) => Ui(() => _flyout?.Refresh());

    /// <summary>A sniprecord:// URI redirected to this process (UI thread).</summary>
    private async void OnProtocolActivated(Uri uri)
    {
        try
        {
            var r = await CaptureResponseHandler.HandleAsync(uri, _settings, _recent);
            await OnUiAsync(() =>
            {
                switch (r.Status)
                {
                    case CaptureStatus.Failed:
                        SetStatus(r.Error ?? "截图失败。", error: true);
                        break;
                    case CaptureStatus.Saved when r.FilePath is not null:
                        SetStatus((r.IsVideo ? "录屏已保存：" : "截图已保存：") + Path.GetFileName(r.FilePath), error: false);
                        break;
                    case CaptureStatus.Discovered:
                        _flyout?.Refresh();
                        break;
                }
            });
            if (r.Status == CaptureStatus.Saved && r.FilePath is not null)
            {
                if (r.IsVideo)
                {
                    // Snipping Tool recording: GIF mode converts it (and deletes the MP4) like a window recording.
                    await OnUiAsync(() => EnsureGifFlow().HandleRecordingCompleted(r.FilePath));
                }
                else if (_settings.OpenEditorAfterCapture)
                {
                    await OnUiTaskAsync(() => OpenEditorAsync(r.FilePath));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Protocol activation failed: {ex}");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        ReleaseHotkeys();
        _gif?.Cancel();
        ActivationHub.ProtocolActivated -= OnProtocolActivated;
        SnippingToolLauncher.DiscoverCompleted -= OnDiscoverCompleted;
        _recent.Changed -= OnRecentChanged;
        _timer?.Stop();
        if (_recorder is not null)
        {
            if (_gif is not null) _gif.Detach(_recorder);
            try { await _recorder.DisposeAsync(); } catch (Exception ex) { Debug.WriteLine(ex); }
            _recorder = null;
        }
        _gif = null;
        await base.DisposeAsync();
    }
}
