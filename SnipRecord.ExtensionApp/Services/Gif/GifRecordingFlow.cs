using System.Diagnostics;

namespace SnipRecord.ExtensionApp.Services.Gif;

public sealed record GifConversionResult(string Mp4Path, string? GifPath, bool Cancelled, string? Error, bool Mp4Deleted)
{
    public bool Succeeded => GifPath is not null;
}

public sealed record GifProgress(string Mp4Path, double Value);

/// <summary>
/// Turns finished recordings into GIFs, one at a time. Construct on the UI thread: events are posted to the
/// SynchronizationContext current at construction. Conversions never throw; read the result / events.
/// </summary>
public sealed class GifRecordingFlow
{
    private readonly Func<bool> _enabled;
    private readonly Func<GifOptions> _options;
    private readonly RecentStore? _recent;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public GifRecordingFlow(Func<bool> enabled, Func<GifOptions> options, RecentStore? recent)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _recent = recent;
    }

    public event EventHandler<string>? ExportStarted;               // mp4 path
    public event EventHandler<GifProgress>? ProgressChanged;
    public event EventHandler<GifConversionResult>? ExportCompleted;
    public event EventHandler<GifConversionResult>? ExportFailed;   // includes cancellations (Cancelled = true)

    /// <summary>Queued or running conversions.</summary>
    public int PendingCount { get { lock (_jobs) return _jobs.Count; } }
    public bool IsBusy => PendingCount > 0;

    /// <summary>True while a conversion of <paramref name="mp4Path"/> is queued or running.</summary>
    public bool IsPending(string mp4Path)
    {
        if (string.IsNullOrEmpty(mp4Path)) return false;
        lock (_jobs) return _jobs.ContainsKey(mp4Path);
    }

    public void Attach(WindowRecorder recorder) => recorder.Completed += OnRecorderCompleted;
    public void Detach(WindowRecorder recorder) => recorder.Completed -= OnRecorderCompleted;

    private void OnRecorderCompleted(object? sender, string mp4) => HandleRecordingCompleted(mp4);

    /// <summary>When GIF mode is enabled, converts the recording and deletes the MP4 on success.</summary>
    public void HandleRecordingCompleted(string mp4Path)
    {
        bool on;
        try { on = _enabled(); } catch { on = false; }
        if (on && !string.IsNullOrEmpty(mp4Path)) _ = ConvertAsync(mp4Path, null, deleteMp4: true);
    }

    public async Task<GifConversionResult> ConvertAsync(string mp4Path, GifOptions? options = null, bool deleteMp4 = false,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_jobs) { _jobs.TryGetValue(mp4Path, out var old); old?.Cancel(); _jobs[mp4Path] = cts; }
        GifConversionResult result;
        try
        {
            await _serial.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                Post(() => ExportStarted?.Invoke(this, mp4Path));
                var opts = options ?? _options();
                var reporter = new InlineProgress(v =>
                {
                    progress?.Report(v);
                    Post(() => ProgressChanged?.Invoke(this, new GifProgress(mp4Path, v)));
                });
                var gif = await GifExporter.ExportAsync(mp4Path, opts, reporter, cts.Token).ConfigureAwait(false);
                var deleted = false;
                if (deleteMp4)
                {
                    try { File.Delete(mp4Path); deleted = true; }
                    catch (Exception ex) { Debug.WriteLine($"[SnipRecord] delete mp4: {ex.Message}"); }
                }
                try
                {
                    var store = _recent ?? RecentStore.Shared;
                    if (deleted) store.Remove(mp4Path);
                    store.AddCapture(gif, false);
                }
                catch (Exception ex) { Debug.WriteLine($"[SnipRecord] GIF recent: {ex.Message}"); }
                result = new GifConversionResult(mp4Path, gif, false, null, deleted);
            }
            finally { _serial.Release(); }
        }
        catch (OperationCanceledException) { result = new GifConversionResult(mp4Path, null, true, "已取消 GIF 导出", false); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] GIF export: {ex}");
            result = new GifConversionResult(mp4Path, null, false, ex is GifExportException ? ex.Message : "GIF 导出失败：" + ex.Message, false);
        }
        finally
        {
            lock (_jobs) { if (_jobs.TryGetValue(mp4Path, out var cur) && cur == cts) _jobs.Remove(mp4Path); }
            cts.Dispose();
        }
        var r = result;
        Post(() => (r.Succeeded ? ExportCompleted : ExportFailed)?.Invoke(this, r));
        return result;
    }

    /// <summary>Cancels the conversion of <paramref name="mp4Path"/>, or all (queued + running) when null.</summary>
    public void Cancel(string? mp4Path = null)
    {
        lock (_jobs)
        {
            if (mp4Path is null) foreach (var c in _jobs.Values) c.Cancel();
            else if (_jobs.TryGetValue(mp4Path, out var c)) c.Cancel();
        }
    }

    private void Post(Action a)
    {
        void Run(object? _) { try { a(); } catch (Exception ex) { Debug.WriteLine($"[SnipRecord] GIF event handler: {ex}"); } }
        if (_context is null) Run(null); else _context.Post(Run, null);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        private int _lastPermille = -1;
        public void Report(double value)
        {
            var p = (int)(value * 1000);
            if (Interlocked.Exchange(ref _lastPermille, p) != p) report(value);
        }
    }
}
