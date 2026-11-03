using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SnipRecord.ExtensionApp.Services.Gif;
using SnipRecord.ExtensionApp.Services.Hotkeys;

namespace SnipRecord.ExtensionApp.Services;

/// <summary>
/// Per-instance settings. WidBar stores this JSON for each placed copy; a copy is also
/// persisted to <see cref="AppPaths.DataDirectory"/>\settings.json (see <see cref="SaveShared"/>)
/// so the headless protocol handler, which has no WidBar context, can read it.
/// </summary>
public sealed class AppSettings
{
    public const int MinWidth = 48;
    public const int MaxWidth = 400;

    /// <summary>Allowed values for <see cref="DelaySeconds"/>.</summary>
    public static readonly int[] AllowedDelays = { 0, 3, 5, 10 };

    public const string ColorFormatHex = "HEX";
    public const string ColorFormatRgb = "RGB";
    public const string ColorFormatHsl = "HSL";

    private const string SharedFileName = "settings.json";

    /// <summary>Taskbar slot width in logical pixels.</summary>
    public int PreviewWidth { get; set; } = 120;

    /// <summary>Folder for recordings (videos); null/empty = Videos\SnipRecord.</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Also record the microphone during window recording.</summary>
    public bool RecordMicrophone { get; set; }

    /// <summary>Folder for screenshots; null/empty = Pictures\Screenshots\SnipRecord.</summary>
    public string? ImageSaveFolder { get; set; }

    /// <summary>Delay before Snipping Tool is launched: 0, 3, 5 or 10 seconds.</summary>
    public int DelaySeconds { get; set; }

    /// <summary>Add &amp;auto-save to ms-screenclip requests (Snipping Tool also saves its own copy).</summary>
    public bool SnipAutoSave { get; set; }

    /// <summary>Clipboard format of the color picker: "HEX" (#RRGGBB), "RGB" (rgb(r, g, b)) or "HSL" (hsl(h, s%, l%)).</summary>
    public string ColorFormat { get; set; } = ColorFormatHex;

    /// <summary>Snipping Tool mode used by the taskbar screenshot button: Rectangle, Window or FreeForm.</summary>
    public SnipMode DefaultSnipMode { get; set; } = SnipMode.Rectangle;

    /// <summary>
    /// Global hotkey bindings, key = <see cref="HotkeyAction"/> name, value = "Ctrl+Alt+A" ("" = disabled).
    /// null = all defaults; a missing action also falls back to its default. Use <see cref="GetHotkeys"/> / <see cref="SetHotkeys"/>.
    /// </summary>
    public Dictionary<string, string>? Hotkeys { get; set; }

    /// <summary>Register the global hotkeys at all.</summary>
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>Full-screen capture covers all monitors (virtual screen) instead of the monitor under the cursor.</summary>
    public bool FullScreenAllMonitors { get; set; }

    /// <summary>GIF frame rate: 5, 10, 15 or 20 (snapped by <see cref="GifOptions.SnapFps"/>).</summary>
    public int GifFps { get; set; } = 10;

    /// <summary>GIF max width in pixels; 0 = original size, else 64–3840 (<see cref="GifOptions.ClampMaxWidth"/>).</summary>
    public int GifMaxWidth { get; set; } = 800;

    /// <summary>Convert finished recordings (window + Snipping Tool) to GIF and delete the MP4.</summary>
    public bool RecordAsGif { get; set; }

    /// <summary>Open the annotation editor after a screenshot is saved (Snipping Tool or full screen).</summary>
    public bool OpenEditorAfterCapture { get; set; }

    /// <summary>Effective bindings (defaults merged in).</summary>
    public HotkeyBindings GetHotkeys() => HotkeyBindings.FromSettingsMap(Hotkeys);

    /// <summary>Store <paramref name="bindings"/>; null resets to defaults.</summary>
    public void SetHotkeys(HotkeyBindings? bindings) => Hotkeys = bindings?.ToSettingsMap();

    /// <summary>GIF options from the settings (fallback folder = screenshot folder).</summary>
    public GifOptions GetGifOptions() => GifOptions.From(GifFps, GifMaxWidth, GetImageFolder());

    public static AppSettings FromJson(string? json)
    {
        try
        {
            var s = string.IsNullOrWhiteSpace(json)
                ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            s.Normalize();
            return s;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public AppSettings Clone() => FromJson(ToJson());

    /// <summary>Clamp / repair values that came from older or hand-edited JSON.</summary>
    public void Normalize()
    {
        PreviewWidth = Math.Clamp(PreviewWidth, MinWidth, MaxWidth);
        if (Array.IndexOf(AllowedDelays, DelaySeconds) < 0)
        {
            // Snap to the nearest allowed value.
            var best = 0;
            foreach (var d in AllowedDelays)
            {
                if (Math.Abs(d - DelaySeconds) < Math.Abs(best - DelaySeconds)) best = d;
            }
            DelaySeconds = best;
        }
        ColorFormat = (ColorFormat ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            ColorFormatRgb => ColorFormatRgb,
            ColorFormatHsl => ColorFormatHsl,
            _ => ColorFormatHex,
        };
        if (DefaultSnipMode is not (SnipMode.Rectangle or SnipMode.Window or SnipMode.FreeForm))
        {
            DefaultSnipMode = SnipMode.Rectangle;
        }
        if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = null;
        if (string.IsNullOrWhiteSpace(ImageSaveFolder)) ImageSaveFolder = null;
        GifFps = GifOptions.SnapFps(GifFps);
        GifMaxWidth = GifOptions.ClampMaxWidth(GifMaxWidth);
        if (Hotkeys is not null)
        {
            // Drop null values / blank keys from hand-edited JSON; "" (disabled) is kept.
            var clean = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in Hotkeys)
            {
                if (!string.IsNullOrWhiteSpace(k) && v is not null) clean[k.Trim()] = v.Trim();
            }
            Hotkeys = clean;
        }
    }

    /// <summary>Effective screenshot folder (not created).</summary>
    public string GetImageFolder() =>
        ImageSaveFolder ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots", "SnipRecord");

    /// <summary>Effective recording folder (not created).</summary>
    public string GetVideoFolder() =>
        SaveFolder ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnipRecord");

    public static string SharedFilePath => System.IO.Path.Combine(AppPaths.DataDirectory, SharedFileName);

    /// <summary>Settings last written by <see cref="SaveShared"/>; defaults if none / unreadable.</summary>
    public static AppSettings LoadShared()
    {
        try
        {
            return FromJson(AtomicFile.ReadAllText(SharedFilePath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] LoadShared failed: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Persist this instance to the shared settings.json. The plugin should call this whenever it
    /// gets settings from WidBar (InitializeAsync / OnSettingsDraftChanged). Never throws.
    /// </summary>
    public bool SaveShared()
    {
        try
        {
            AtomicFile.WriteAllText(SharedFilePath, ToJson());
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] SaveShared failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Data folder shared by the widget process and the headless protocol handler.
/// Order: (1) ApplicationData.Current.LocalFolder when the process has package identity;
/// (2) %LOCALAPPDATA%\Packages\&lt;PFN&gt;\LocalState derived from the install folder name
///     (WindowsApps\Name_Version_Arch_Res_PublisherId) when running from the package without
///     identity — the same physical folder as (1); (3) %LOCALAPPDATA%\SnipRecord.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> _dataDirectory = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DataDirectory => _dataDirectory.Value;

    private static string Resolve()
    {
        string? dir = null;
        try
        {
            dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] No package identity for ApplicationData: {ex.Message}");
        }

        dir ??= FromInstallFolder();
        dir ??= System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnipRecord");

        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    private static readonly Regex PackageFullName =
        new(@"^(?<name>[^_]+)_[^_]+_[^_]*_[^_]*_(?<pid>[a-z0-9]{13})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? FromInstallFolder()
    {
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null)
            {
                var m = PackageFullName.Match(d.Name);
                if (m.Success)
                {
                    var pfn = $"{m.Groups["name"].Value}_{m.Groups["pid"].Value}";
                    var local = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Packages", pfn, "LocalState");
                    return Directory.Exists(System.IO.Path.GetDirectoryName(local)) ? local : null;
                }
                d = d.Parent;
            }
        }
        catch { }
        return null;
    }
}

/// <summary>Crash- and process-safe small-file IO: write temp + replace, retry on sharing violations.</summary>
internal static class AtomicFile
{
    private const int Attempts = 8;

    /// <summary>Returns null when the file does not exist.</summary>
    public static string? ReadAllText(string path)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (i < Attempts)
            {
                Thread.Sleep(25 * i);
            }
            catch (UnauthorizedAccessException) when (i < Attempts)
            {
                Thread.Sleep(25 * i);
            }
        }
    }

    public static void WriteAllText(string path, string text)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, text);
        try
        {
            for (var i = 1; ; i++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (IOException) when (i < Attempts)
                {
                    Thread.Sleep(25 * i);
                }
                catch (UnauthorizedAccessException) when (i < Attempts)
                {
                    Thread.Sleep(25 * i);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
