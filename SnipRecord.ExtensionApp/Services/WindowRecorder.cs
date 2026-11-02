using System.Diagnostics;
using SnipRecord.ExtensionApp.Services.Recording;
using Windows.Graphics.Capture;

namespace SnipRecord.ExtensionApp.Services;

/// <summary>
/// Window recording: Snipping-Tool-style window picker → Windows.Graphics.Capture (no yellow border,
/// cursor included) → H.264 MP4 via MediaTranscoder, with the window's process audio (process loopback,
/// Win11 / build 20348+) and optionally the microphone. Shows a red frame and a control bar (timer,
/// pause/resume, stop) that are excluded from capture.
/// Events are raised on the SynchronizationContext that called Start* (the UI thread), if any.
/// Rectangle recording is delegated to Snipping Tool and is not implemented here.
/// </summary>
public sealed class WindowRecorder : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SynchronizationContext? _syncContext;
    private RecordingClock? _clock;
    private CaptureEncoder? _encoder;
    private AudioMixer? _mixer;
    private UiThread? _ui;
    private RecordingOverlay? _overlay;
    private Task? _stopTask;
    private readonly object _stopLock = new();

    public bool IsRecording { get; private set; }

    public bool IsPaused => _clock?.IsPaused ?? false;

    /// <summary>Media time recorded so far (excludes paused time).</summary>
    public TimeSpan Elapsed => _clock?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Final path of the current / last recording.</summary>
    public string? OutputPath { get; private set; }

    /// <summary>True when the current recording has no (or only partial) audio; see <see cref="Warning"/>.</summary>
    public bool AudioUnavailable { get; private set; }

    /// <summary>Human-readable (Chinese) non-fatal problem, e.g. audio could not be captured.</summary>
    public string? Warning { get; private set; }

    /// <summary>Raised with the saved MP4 path after a successful recording.</summary>
    public event EventHandler<string>? Completed;

    /// <summary>Raised when the file could not be finalized (or no frame was captured); start failures throw instead.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Raised on start, pause, resume and stop.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Default output path: &lt;SaveFolder or Videos\SnipRecord&gt;\窗口录屏_yyyyMMdd_HHmmss.mp4</summary>
    public static string BuildOutputPath(AppSettings? settings)
    {
        string folder = string.IsNullOrWhiteSpace(settings?.SaveFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnipRecord")
            : settings!.SaveFolder!;
        return Path.Combine(folder, $"窗口录屏_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
    }

    /// <summary>Shows the window picker. Returns <see cref="IntPtr.Zero"/> when the user cancels (Esc / right click).</summary>
    public static Task<IntPtr> PickWindowAsync(CancellationToken cancellationToken = default)
        => WindowPicker.PickAsync(cancellationToken);

    /// <summary>Pick a window, then record it with the settings' folder and microphone choice.
    /// Returns false if the user cancelled the picker.</summary>
    public async Task<bool> StartWindowPickAsync(AppSettings? settings, CancellationToken cancellationToken = default)
    {
        var ctx = SynchronizationContext.Current;
        var hwnd = await PickWindowAsync(cancellationToken).ConfigureAwait(false);
        if (hwnd == IntPtr.Zero) return false;
        await StartCoreAsync(hwnd, BuildOutputPath(settings), settings?.RecordMicrophone ?? false, ctx).ConfigureAwait(false);
        return true;
    }

    /// <summary>Starts recording <paramref name="hwnd"/> (a top-level window). <paramref name="outputPath"/>
    /// null/empty = <see cref="BuildOutputPath"/>(null). Throws if recording cannot start.</summary>
    public Task StartWindowAsync(IntPtr hwnd, string? outputPath, bool microphone)
        => StartCoreAsync(hwnd, outputPath, microphone, SynchronizationContext.Current);

    private async Task StartCoreAsync(IntPtr hwnd, string? outputPath, bool microphone, SynchronizationContext? syncContext)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRecording) throw new InvalidOperationException("已经在录制中");
            _syncContext = syncContext;
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) throw new ArgumentException("窗口无效", nameof(hwnd));
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("此系统不支持窗口捕获");

            Warning = null;
            AudioUnavailable = false;
            _stopTask = null;
            string path = string.IsNullOrWhiteSpace(outputPath) ? BuildOutputPath(null) : outputPath!;
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string fileName = Path.GetFileName(path);

            var item = Direct3DInterop.CreateItemForWindow(hwnd);
            var clock = new RecordingClock();

            // --- audio (best effort) ---
            var audioQueue = new SampleQueue();
            var mixer = new AudioMixer(clock, audioQueue);
            var warnings = new List<string>();
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!AudioInterop.IsProcessLoopbackSupported)
            {
                warnings.Add("此 Windows 版本不支持录制窗口声音（需要 Windows 11）");
            }
            else
            {
                try
                {
                    var buf = AudioMixer.NewBuffer();
                    mixer.AddInput(await WasapiSource.CreateProcessLoopbackAsync(pid, buf).ConfigureAwait(false), buf);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    warnings.Add("无法录制窗口声音");
                }
            }
            if (microphone)
            {
                try
                {
                    var buf = AudioMixer.NewBuffer();
                    mixer.AddInput(await WasapiSource.CreateMicrophoneAsync(buf).ConfigureAwait(false), buf);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    warnings.Add("无法录制麦克风（请检查麦克风权限）");
                }
            }
            bool hasAudio = mixer.InputCount > 0;
            if (warnings.Count > 0)
            {
                Warning = string.Join("；", warnings);
                AudioUnavailable = true;
            }

            var encoder = new CaptureEncoder(item, clock, hasAudio ? audioQueue : null);
            UiThread? ui = null;
            try
            {
                try
                {
                    await encoder.InitializeAsync(folder, fileName).ConfigureAwait(false);
                }
                catch when (hasAudio)
                {
                    // Retry once without audio before giving up.
                    Debug.WriteLine("Encoder init with audio failed; retrying video only.");
                    await encoder.DisposeAsync().ConfigureAwait(false);
                    TryDelete(encoder.OutputPath);
                    mixer.Stop();
                    hasAudio = false;
                    AudioUnavailable = true;
                    Warning = string.IsNullOrEmpty(Warning) ? "音频编码失败，仅录制画面" : Warning + "；音频编码失败";
                    encoder = new CaptureEncoder(item, clock, null);
                    await encoder.InitializeAsync(folder, fileName).ConfigureAwait(false);
                }
                OutputPath = encoder.OutputPath;

                ui = await UiThread.StartAsync("SnipRecord recording overlay").ConfigureAwait(false);
                _overlay = await ui.InvokeAsync(() => new RecordingOverlay(hwnd, clock,
                    onPauseToggle: () => TogglePause(),
                    onStop: () => _ = StopAsync(),
                    onTargetGone: () => _ = StopAsync())).ConfigureAwait(false);

                encoder.SourceClosed += () => _ = StopAsync();

                clock.Start();
                if (hasAudio) mixer.Start();
                encoder.Start();
            }
            catch
            {
                if (hasAudio) mixer.Stop();
                if (ui != null)
                {
                    var overlay = _overlay;
                    _overlay = null;
                    ui.Shutdown(() => overlay?.Dispose());
                }
                try { await encoder.DisposeAsync().ConfigureAwait(false); } catch { }
                TryDelete(encoder.OutputPath);
                throw;
            }

            _clock = clock;
            _encoder = encoder;
            _mixer = hasAudio ? mixer : null;
            _ui = ui;
            IsRecording = true;
        }
        finally
        {
            _gate.Release();
        }
        Raise(() => StateChanged?.Invoke(this, EventArgs.Empty));
        // The window may have closed while we were starting (its close notifications came too early).
        if (!Win32.IsWindow(hwnd)) _ = StopAsync();
    }

    /// <summary>Rectangle recording is handled by Snipping Tool (ms-screenclip), not by this class.</summary>
    public Task StartRectangleAsync(Windows.Graphics.RectInt32 area, string outputPath, bool microphone)
        => throw new NotSupportedException("矩形录屏由截图工具完成");

    public void Pause()
    {
        if (IsRecording && _clock?.Pause() == true) OnPauseChanged();
    }

    public void Resume()
    {
        if (IsRecording && _clock?.Resume() == true) OnPauseChanged();
    }

    public void TogglePause()
    {
        if (IsPaused) Resume(); else Pause();
    }

    private void OnPauseChanged()
    {
        var overlay = _overlay;
        _ui?.Post(() => overlay?.Refresh());
        Raise(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Stops and finalizes the file. Safe to call repeatedly / concurrently; raises
    /// <see cref="Completed"/> (or <see cref="Failed"/>) once.</summary>
    public Task StopAsync()
    {
        lock (_stopLock)
        {
            if (!IsRecording) return _stopTask ?? Task.CompletedTask;
            return _stopTask ??= Task.Run(StopCoreAsync);
        }
    }

    private async Task StopCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string? path = OutputPath;
        Exception? error = null;
        try
        {
            var mixer = _mixer;
            var encoder = _encoder;
            var ui = _ui;
            var overlay = _overlay;
            _mixer = null; _encoder = null; _ui = null; _overlay = null;

            ui?.Shutdown(() => overlay?.Dispose());
            _clock?.Resume(); // a paused stop still flushes cleanly
            mixer?.Stop();    // ends the audio stream
            if (encoder != null)
            {
                try
                {
                    await encoder.StopAsync().ConfigureAwait(false);
                    if (encoder.FramesWritten == 0) error = new InvalidOperationException("没有捕获到任何画面");
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                await encoder.DisposeAsync().ConfigureAwait(false);
            }
            if (error != null) TryDelete(path);
        }
        finally
        {
            IsRecording = false;
            _gate.Release();
        }

        Raise(() => StateChanged?.Invoke(this, EventArgs.Empty));
        if (error != null) Raise(() => Failed?.Invoke(this, error));
        else if (!string.IsNullOrEmpty(path)) Raise(() => Completed?.Invoke(this, path!));
    }

    private void Raise(Action action)
    {
        var ctx = _syncContext;
        if (ctx != null && ctx != SynchronizationContext.Current)
        {
            ctx.Post(_ =>
            {
                try { action(); } catch (Exception ex) { Debug.WriteLine(ex); }
            }, null);
        }
        else
        {
            try { action(); } catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRecording) await StopAsync().ConfigureAwait(false);
    }
}
