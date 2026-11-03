using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SnipRecord.ExtensionApp.Services.Gif;

public sealed class GifOptions
{
    public static readonly int[] AllowedFps = { 5, 10, 15, 20 };

    /// <summary>Frames per second, one of 5/10/15/20.</summary>
    public int Fps { get; set; } = 10;
    /// <summary>Maximum output width in px; 0 = source width. Never upscales.</summary>
    public int MaxWidth { get; set; } = 800;
    public bool LoopForever { get; set; } = true;
    public double? TrimStartSeconds { get; set; }
    public double? TrimEndSeconds { get; set; }
    /// <summary>Used when the MP4's folder is not writable; null = the default screenshot folder.</summary>
    public string? FallbackFolder { get; set; }

    public static GifOptions From(int fps, int maxWidth, string? fallbackFolder = null) =>
        new() { Fps = SnapFps(fps), MaxWidth = ClampMaxWidth(maxWidth), FallbackFolder = fallbackFolder };

    public static int SnapFps(int fps) => AllowedFps.OrderBy(f => Math.Abs(f - fps)).ThenBy(f => f).First();

    public static int ClampMaxWidth(int maxWidth) => maxWidth <= 0 ? 0 : Math.Clamp(maxWidth, 64, 3840);
}

public sealed class GifExportException : Exception
{
    public GifExportException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>MP4 → animated GIF with Windows.Media.Editing thumbnails + the WIC GIF encoder.</summary>
public static class GifExporter
{
    public const int MaxFrames = 12000;
    private const int BatchSize = 8;

    /// <summary>Returns the GIF path. Throws <see cref="GifExportException"/> (Chinese message) or OperationCanceledException.</summary>
    public static async Task<string> ExportAsync(string mp4Path, GifOptions? options = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var o = options ?? new GifOptions();
        var fps = GifOptions.SnapFps(o.Fps);
        if (!File.Exists(mp4Path)) throw new GifExportException("找不到录屏文件：" + mp4Path);

        MediaClip clip;
        try { clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(mp4Path))); }
        catch (Exception ex) { throw new GifExportException("无法读取录屏文件", ex); }
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        var props = clip.GetVideoEncodingProperties();
        int srcW = (int)props.Width, srcH = (int)props.Height;
        if (srcW <= 0 || srcH <= 0) throw new GifExportException("录屏文件没有视频画面");
        var maxW = GifOptions.ClampMaxWidth(o.MaxWidth);
        int w = srcW, h = srcH;
        if (maxW > 0 && srcW > maxW) { w = maxW; h = Math.Max(1, (int)Math.Round(srcH * (double)maxW / srcW)); }

        var duration = composition.Duration.TotalSeconds;
        var start = Math.Clamp(o.TrimStartSeconds ?? 0, 0, duration);
        var end = Math.Clamp(o.TrimEndSeconds ?? duration, start, duration);
        var count = (int)Math.Ceiling((end - start) * fps - 1e-6);
        if (count <= 0) throw new GifExportException("剪辑范围内没有画面");
        if (count > MaxFrames) throw new GifExportException($"GIF 帧数过多（{count} 帧，上限 {MaxFrames}），请降低帧率或缩短时长");

        var (part, folder) = CreatePartFile(mp4Path, o.FallbackFolder);
        var ok = false;
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.ReadWrite))
            using (var ras = fs.AsRandomAccessStream())
            {
                var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, ras);
                if (o.LoopForever)
                {
                    try
                    {
                        await enc.BitmapContainerProperties.SetPropertiesAsync(new BitmapPropertySet
                        {
                            ["/appext/Application"] = new BitmapTypedValue(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"), Windows.Foundation.PropertyType.UInt8Array),
                            ["/appext/Data"] = new BitmapTypedValue(new byte[] { 3, 1, 0, 0, 0 }, Windows.Foundation.PropertyType.UInt8Array),
                        });
                    }
                    catch (Exception ex) { Debug.WriteLine($"[SnipRecord] GIF loop ext rejected: {ex.Message}"); }
                }

                var prevCs = 0;
                for (var b = 0; b < count; b += BatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = Math.Min(BatchSize, count - b);
                    var times = Enumerable.Range(b, n).Select(i => TimeSpan.FromSeconds(Math.Min(start + i / (double)fps, Math.Max(0, duration - 0.001)))).ToList();
                    var thumbs = await composition.GetThumbnailsAsync(times, w, h, VideoFramePrecision.NearestFrame);
                    for (var k = 0; k < n; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var i = b + k;
                        byte[] pixels;
                        using (var img = k < thumbs.Count ? thumbs[k] : null)
                        {
                            if (img is null) throw new GifExportException("无法读取视频帧");
                            var dec = await BitmapDecoder.CreateAsync(img);
                            var data = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                                new BitmapTransform { ScaledWidth = (uint)w, ScaledHeight = (uint)h, InterpolationMode = BitmapInterpolationMode.Fant },
                                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                            pixels = data.DetachPixelData();
                        }
                        if (i > 0) await enc.GoToNextFrameAsync();
                        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, pixels);
                        var cs = (int)Math.Round((i + 1) * 100.0 / fps); // accumulated hundredths → no drift
                        var delay = (ushort)Math.Max(2, cs - prevCs);
                        prevCs = cs;
                        await enc.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                        {
                            ["/grctlext/Delay"] = new BitmapTypedValue(delay, Windows.Foundation.PropertyType.UInt16),
                        });
                        progress?.Report((i + 1) * 0.95 / count);
                    }
                    foreach (var t in thumbs.Skip(n)) t.Dispose();
                }
                await enc.FlushAsync();
            }
            ct.ThrowIfCancellationRequested();
            var final = UniquePath(folder, Path.GetFileNameWithoutExtension(mp4Path));
            File.Move(part, final);
            ok = true;
            progress?.Report(1.0);
            return final;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GifExportException)
        {
            throw new GifExportException("GIF 导出失败：" + ex.Message, ex);
        }
        finally
        {
            if (!ok) try { File.Delete(part); } catch { /* best effort */ }
        }
    }

    private static (string Part, string Folder) CreatePartFile(string mp4Path, string? fallback)
    {
        var folders = new List<string> { Path.GetDirectoryName(Path.GetFullPath(mp4Path))! };
        folders.Add(string.IsNullOrWhiteSpace(fallback) ? new AppSettings().GetImageFolder() : fallback);
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(folder);
                var part = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(mp4Path)}.{Guid.NewGuid():N}.gif.part");
                using (new FileStream(part, FileMode.CreateNew, FileAccess.Write)) { }
                return (part, folder);
            }
            catch (Exception ex) { Debug.WriteLine($"[SnipRecord] GIF folder {folder} not writable: {ex.Message}"); }
        }
        throw new GifExportException("没有可写入的文件夹来保存 GIF");
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".gif");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}).gif");
        return path;
    }
}
