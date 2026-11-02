using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnipRecord.ExtensionApp.Services;

/// <summary>One recent screenshot / recording. Kind: IsVideo ? video : image.</summary>
public sealed record RecentItem(string Path, DateTimeOffset CreatedAt, bool IsVideo);

/// <summary>
/// Recent captures (last <see cref="MaxCaptures"/>) and picked colours (last <see cref="MaxColors"/>),
/// persisted to recent.json. Safe across threads and processes: every mutation is
/// read-modify-write under a named mutex, written to a temp file and moved over the target.
/// A FileSystemWatcher picks up writes from other processes (e.g. the headless protocol handler).
/// <see cref="Changed"/> fires on an arbitrary thread — marshal to the UI thread yourself.
/// </summary>
public sealed class RecentStore : IDisposable
{
    public const int MaxCaptures = 8;
    public const int MaxColors = 12;
    private const string FileName = "recent.json";
    private const string MutexName = @"Local\SnipRecord.RecentStore";

    private static readonly Lazy<RecentStore> _shared = new(() => new RecentStore(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _file;
    private readonly object _gate = new();
    private List<RecentItem> _items = new();
    private List<string> _colors = new();
    private string? _lastText;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _disposed;

    /// <summary>Process-wide store in <see cref="AppPaths.DataDirectory"/> (with watcher).</summary>
    public static RecentStore Shared => _shared.Value;

    /// <summary>Store in the shared data folder (<see cref="AppPaths.DataDirectory"/>).</summary>
    public RecentStore() : this(AppPaths.DataDirectory, watch: true) { }

    /// <summary>
    /// Store in <paramref name="dataDirectory"/>. NOTE: the headless handler always uses
    /// <see cref="AppPaths.DataDirectory"/>; pass a different folder only if you know why.
    /// </summary>
    public RecentStore(string dataDirectory, bool watch = true)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) dataDirectory = AppPaths.DataDirectory;
        try { Directory.CreateDirectory(dataDirectory); } catch { }
        _file = System.IO.Path.Combine(dataDirectory, FileName);
        Reload(raiseEvent: false);
        if (watch) StartWatcher(dataDirectory);
    }

    /// <summary>Raised after the lists changed (own write or another process). Any thread.</summary>
    public event EventHandler? Changed;

    public string FilePath => _file;

    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>Newest first (snapshot).</summary>
    public IReadOnlyList<RecentItem> Items { get { lock (_gate) return _items.ToArray(); } }

    /// <summary>Picked colours as "#RRGGBB", newest first (snapshot).</summary>
    public IReadOnlyList<string> Colors { get { lock (_gate) return _colors.ToArray(); } }

    public void Add(RecentItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path)) return;
        Mutate((items, _) =>
        {
            items.RemoveAll(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, item);
            if (items.Count > MaxCaptures) items.RemoveRange(MaxCaptures, items.Count - MaxCaptures);
        });
    }

    public void AddCapture(string path, bool isVideo) => Add(new RecentItem(path, DateTimeOffset.Now, isVideo));

    /// <summary>Adds a colour ("#RRGGBB", case-insensitive; stored upper-case).</summary>
    public void AddColor(string hex)
    {
        var norm = NormalizeHex(hex);
        if (norm is null) return;
        Mutate((_, colors) =>
        {
            colors.RemoveAll(c => string.Equals(c, norm, StringComparison.OrdinalIgnoreCase));
            colors.Insert(0, norm);
            if (colors.Count > MaxColors) colors.RemoveRange(MaxColors, colors.Count - MaxColors);
        });
    }

    public void Remove(string path) =>
        Mutate((items, _) => items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Drops entries whose file no longer exists.</summary>
    public void RemoveMissing() => Mutate((items, _) => items.RemoveAll(i => !File.Exists(i.Path)));

    public void ClearCaptures() => Mutate((items, _) => items.Clear());

    public void ClearColors() => Mutate((_, colors) => colors.Clear());

    /// <summary>Re-reads recent.json; raises <see cref="Changed"/> if the content differs.</summary>
    public void Reload() => Reload(raiseEvent: true);

    // ---------------------------------------------------------------------------------

    private void Mutate(Action<List<RecentItem>, List<string>> change)
    {
        string text;
        lock (_gate)
        {
            if (_disposed) return;
            using (var mutex = AcquireMutex())
            {
                // Read the latest on-disk state first so we never drop another process' entries.
                var (items, colors) = Parse(SafeRead());
                change(items, colors);
                text = Serialize(items, colors);
                try
                {
                    AtomicFile.WriteAllText(_file, text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SnipRecord] recent.json write failed: {ex.Message}");
                }
                _items = items;
                _colors = colors;
                _lastText = text;
            }
        }
        RaiseChanged();
    }

    private void Reload(bool raiseEvent)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            var text = SafeRead();
            changed = !string.Equals(text, _lastText, StringComparison.Ordinal);
            if (changed)
            {
                var (items, colors) = Parse(text);
                _items = items;
                _colors = colors;
                _lastText = text;
            }
        }
        if (changed && raiseEvent) RaiseChanged();
    }

    private string? SafeRead()
    {
        try
        {
            return AtomicFile.ReadAllText(_file);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] recent.json read failed: {ex.Message}");
            return _lastText;
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] RecentStore.Changed handler threw: {ex}");
        }
    }

    private void StartWatcher(string directory)
    {
        try
        {
            _debounce = new Timer(_ => Reload(raiseEvent: true), null, Timeout.Infinite, Timeout.Infinite);
            var w = new FileSystemWatcher(directory, FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            w.Changed += OnFileEvent;
            w.Created += OnFileEvent;
            w.Renamed += OnFileEvent;
            w.Deleted += OnFileEvent;
            w.Error += (_, e) => Debug.WriteLine($"[SnipRecord] recent.json watcher error: {e.GetException().Message}");
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] recent.json watcher not started: {ex.Message}");
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        try { _debounce?.Change(150, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private static MutexHolder AcquireMutex()
    {
        Mutex? m = null;
        try
        {
            m = new Mutex(false, MutexName);
            bool owned;
            try
            {
                owned = m.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                owned = true; // previous owner died; state on disk is still a complete file.
            }
            return new MutexHolder(m, owned);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] recent mutex unavailable: {ex.Message}");
            m?.Dispose();
            return new MutexHolder(null, false);
        }
    }

    private readonly struct MutexHolder : IDisposable
    {
        private readonly Mutex? _mutex;
        private readonly bool _owned;

        public MutexHolder(Mutex? mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        public void Dispose()
        {
            if (_mutex is null) return;
            try { if (_owned) _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }

    internal static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return null;
        foreach (var ch in s)
        {
            if (!Uri.IsHexDigit(ch)) return null;
        }
        return "#" + s.ToUpperInvariant();
    }

    // ---- JSON shape: { "captures": [ { "path", "kind": "image"|"video", "time" } ], "colors": [ "#RRGGBB" ] }

    private sealed class FileModel
    {
        [JsonPropertyName("captures")] public List<CaptureModel>? Captures { get; set; }
        [JsonPropertyName("colors")] public List<string>? Colors { get; set; }
    }

    private sealed class CaptureModel
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    }

    private static (List<RecentItem> Items, List<string> Colors) Parse(string? text)
    {
        var items = new List<RecentItem>();
        var colors = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return (items, colors);
        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(text, JsonOptions);
            if (model?.Captures is { } caps)
            {
                foreach (var c in caps)
                {
                    if (string.IsNullOrWhiteSpace(c.Path)) continue;
                    if (items.Exists(i => string.Equals(i.Path, c.Path, StringComparison.OrdinalIgnoreCase))) continue;
                    items.Add(new RecentItem(c.Path, c.Time, string.Equals(c.Kind, "video", StringComparison.OrdinalIgnoreCase)));
                    if (items.Count >= MaxCaptures) break;
                }
            }
            if (model?.Colors is { } cols)
            {
                foreach (var c in cols)
                {
                    var n = NormalizeHex(c);
                    if (n is null || colors.Contains(n)) continue;
                    colors.Add(n);
                    if (colors.Count >= MaxColors) break;
                }
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SnipRecord] recent.json is corrupt, starting empty: {ex.Message}");
        }
        return (items, colors);
    }

    private static string Serialize(List<RecentItem> items, List<string> colors)
    {
        var model = new FileModel
        {
            Captures = items.Select(i => new CaptureModel
            {
                Path = i.Path,
                Kind = i.IsVideo ? "video" : "image",
                Time = i.CreatedAt,
            }).ToList(),
            Colors = colors.ToList(),
        };
        return JsonSerializer.Serialize(model, JsonOptions);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _watcher?.Dispose(); } catch { }
        try { _debounce?.Dispose(); } catch { }
        _watcher = null;
        _debounce = null;
    }
}
