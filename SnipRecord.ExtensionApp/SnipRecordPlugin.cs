using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SnipRecord.ExtensionApp.Services;
using SnipRecord.ExtensionApp.Views;
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

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _settings = AppSettings.FromJson(context.SettingsJson);
        // The flyout's microphone toggle can only persist to the shared settings.json
        // (the flyout has no WidBar settings context), so that file wins for this one value.
        try
        {
            if (File.Exists(AppSettings.SharedFilePath))
            {
                _settings.RecordMicrophone = AppSettings.LoadShared().RecordMicrophone;
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
        CloseFlyout();
        var ok = await SnippingToolLauncher.LaunchAsync(mode ?? _settings.DefaultSnipMode, _settings);
        if (!ok) SetStatus("无法启动截图工具，请确认已安装最新版 Windows 截图工具。", error: true);
    }

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
        return r;
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Protocol activation failed: {ex}");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        ActivationHub.ProtocolActivated -= OnProtocolActivated;
        SnippingToolLauncher.DiscoverCompleted -= OnDiscoverCompleted;
        _recent.Changed -= OnRecentChanged;
        _timer?.Stop();
        if (_recorder is not null)
        {
            try { await _recorder.DisposeAsync(); } catch (Exception ex) { Debug.WriteLine(ex); }
            _recorder = null;
        }
        await base.DisposeAsync();
    }
}
