using System.Diagnostics;
using SnipRecord.ExtensionApp.Services.ColorPicker;
using Windows.UI;

namespace SnipRecord.ExtensionApp.Services;

/// <summary>
/// Screen colour picker: freezes the screen, shows a loupe overlay on every monitor, and on click
/// copies the colour to the clipboard in <see cref="AppSettings.ColorFormat"/> and adds its HEX to
/// the recent colours. Runs on its own STA thread (pure Win32/GDI), so it can be awaited from the
/// UI thread without blocking it. Only one pick runs at a time; a second call returns the same task.
/// </summary>
public sealed class ColorPickerService
{
    private readonly RecentStore? _recent;
    private readonly Func<AppSettings>? _settings;
    private readonly object _gate = new();
    private Task<Color?>? _running;

    /// <summary>Uses <see cref="RecentStore.Shared"/> and the shared settings.json.</summary>
    public ColorPickerService() : this(null, null) { }

    /// <param name="recent">Store for picked colours (null = <see cref="RecentStore.Shared"/>).</param>
    /// <param name="settings">Current settings, read at pick time (null = <see cref="AppSettings.LoadShared"/>).</param>
    public ColorPickerService(RecentStore? recent, Func<AppSettings>? settings)
    {
        _recent = recent;
        _settings = settings;
    }

    /// <summary>Raised (thread-pool thread) after a colour was picked; the string is what went to the clipboard.</summary>
    public event Action<Color, string>? ColorPicked;

    public bool IsPicking { get { lock (_gate) return _running is not null; } }

    /// <summary>Returns the picked color, or null when cancelled (Esc / right click / timeout) or on failure.</summary>
    public Task<Color?> PickAsync() => PickAsync(0);

    /// <param name="startDelayMs">Wait before freezing the screen, e.g. 150–250 ms so a closing flyout is gone.</param>
    public Task<Color?> PickAsync(int startDelayMs)
    {
        lock (_gate)
        {
            return _running ??= RunAsync(Math.Max(0, startDelayMs));
        }
    }

    private async Task<Color?> RunAsync(int startDelayMs)
    {
        try
        {
            await Task.Delay(startDelayMs > 0 ? startDelayMs : 1).ConfigureAwait(false); // always yield before _running is published

            AppSettings settings;
            try { settings = _settings?.Invoke() ?? AppSettings.LoadShared(); }
            catch { settings = AppSettings.LoadShared(); }
            var format = settings.ColorFormat;

            var (picked, text) = await RunOnStaThread(() =>
            {
                var c = PickerSession.Run();
                if (c is not { } p) return ((PickedColor?)null, (string?)null);
                var s = ColorFormatter.Format(p.R, p.G, p.B, format);
                if (!Win32Clipboard.SetText(s)) Debug.WriteLine("[SnipRecord] colour not copied to clipboard");
                return (c, s);
            }).ConfigureAwait(false);

            if (picked is not { } pc) return null;

            try
            {
                (_recent ?? RecentStore.Shared).AddColor(ColorFormatter.ToHex(pc.R, pc.G, pc.B));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SnipRecord] recent colour not saved: {ex.Message}");
            }

            var color = new Color { A = 255, R = pc.R, G = pc.G, B = pc.B };
            try { ColorPicked?.Invoke(color, text ?? ToHex(color)); }
            catch (Exception ex) { Debug.WriteLine($"[SnipRecord] ColorPicked handler threw: {ex}"); }
            return color;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] colour picker failed: {ex}");
            return null;
        }
        finally
        {
            lock (_gate) _running = null;
        }
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "SnipRecord.ColorPicker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static string ToHex(Color c) => ColorFormatter.ToHex(c.R, c.G, c.B);

    /// <summary>Formats as "#RRGGBB", "rgb(r, g, b)" or "hsl(h, s%, l%)" (AppSettings.ColorFormat values).</summary>
    public static string Format(Color c, string? format) => ColorFormatter.Format(c.R, c.G, c.B, format);
}
