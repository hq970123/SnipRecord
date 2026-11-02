using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace SnipRecord.ExtensionApp.Services.Editor;

/// <summary>
/// Win2D drawing shared by the on-screen CanvasControl and the full-resolution export, so what
/// the user sees is exactly what gets saved. All drawing happens in image-pixel space (the
/// session transform maps it to the screen); bitmaps are loaded at 96 DPI so 1 DIP = 1 pixel.
/// </summary>
public sealed class AnnotationRenderer : IDisposable
{
    public const string FontFamily = "Microsoft YaHei UI";
    private readonly Dictionary<int, CanvasRenderTarget> _mosaicCache = new();
    private CanvasStrokeStyle? _round;

    public AnnotationRenderer(CanvasBitmap source) { Source = source; }

    public CanvasBitmap Source { get; private set; }

    /// <summary>After device loss the source is rebuilt; cached mosaic bitmaps belong to the old device.</summary>
    public void ReplaceSource(CanvasBitmap source)
    {
        ClearCache();
        _round?.Dispose(); _round = null;
        Source.Dispose();
        Source = source;
    }

    public static Task<CanvasBitmap> LoadAsync(ICanvasResourceCreator device, string path) =>
        CanvasBitmap.LoadAsync(device, path, 96f).AsTask();

    /// <summary>Draws image + annotations clipped to doc.Crop. The caller sets ds.Transform (image px → target).</summary>
    public void Draw(CanvasDrawingSession ds, EditorDocument doc, Annotation? pending = null, Guid? hidden = null, bool exporting = false)
    {
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        var crop = doc.Crop;
        using (ds.CreateLayer(1f, crop))
        {
            ds.DrawImage(Source, crop, crop, 1f, exporting ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.HighQualityCubic);
            foreach (var a in doc.Items)
                if (a.Id != hidden) DrawOne(ds, a);
            if (pending != null) DrawOne(ds, pending);
        }
    }

    public void DrawOne(CanvasDrawingSession ds, Annotation a)
    {
        _round ??= new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round, DashCap = CanvasCapStyle.Round };
        switch (a)
        {
            case StrokeAnnotation s when s.Points.Length > 0:
                if (s.Points.Length == 1) { ds.FillCircle(s.Points[0], s.Width / 2, s.Color); break; }
                using (var pb = new CanvasPathBuilder(ds))
                {
                    pb.BeginFigure(s.Points[0]);
                    for (var i = 1; i < s.Points.Length; i++) pb.AddLine(s.Points[i]);
                    pb.EndFigure(CanvasFigureLoop.Open);
                    using var geo = CanvasGeometry.CreatePath(pb);
                    // one geometry → a translucent highlighter doesn't darken where it overlaps itself
                    ds.DrawGeometry(geo, s.Color, s.Width, _round);
                }
                break;

            case LineAnnotation l:
                if (!l.Arrow) { ds.DrawLine(l.Start, l.End, l.Color, l.Width, _round); break; }
                var dir = l.End - l.Start;
                var len = dir.Length();
                if (len < 0.5f) break;
                dir /= len;
                var head = Math.Min(l.HeadLength, len);
                var normal = new Vector2(-dir.Y, dir.X);
                var baseCenter = l.End - dir * head;
                var halfW = head * 0.5f;
                ds.DrawLine(l.Start, l.End - dir * (head * 0.6f), l.Color, l.Width, _round);
                using (var arrow = CanvasGeometry.CreatePolygon(ds, new[] { l.End, baseCenter + normal * halfW, baseCenter - normal * halfW }))
                    ds.FillGeometry(arrow, l.Color);
                break;

            case ShapeAnnotation sh:
                if (sh.Ellipse)
                    ds.DrawEllipse((float)(sh.Rect.X + sh.Rect.Width / 2), (float)(sh.Rect.Y + sh.Rect.Height / 2),
                        (float)sh.Rect.Width / 2, (float)sh.Rect.Height / 2, sh.Color, sh.Width);
                else ds.DrawRectangle(sh.Rect, sh.Color, sh.Width);
                break;

            case TextAnnotation t when t.Text.Length > 0:
                using (var layout = CreateTextLayout(ds, t.Text, t.FontSize))
                    ds.DrawTextLayout(layout, t.Position, t.Color);
                break;

            case MosaicAnnotation m when m.Rect.Width >= 1 && m.Rect.Height >= 1:
                var small = GetMosaic(m.Block);
                var src = new Rect(m.Rect.X / m.Block, m.Rect.Y / m.Block, m.Rect.Width / m.Block, m.Rect.Height / m.Block);
                ds.DrawImage(small, m.Rect, src, 1f, CanvasImageInterpolation.NearestNeighbor);
                break;

            case NumberAnnotation n:
                ds.FillCircle(n.Center, n.Radius, n.Color);
                ds.DrawCircle(n.Center, n.Radius, Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), Math.Max(1f, n.Radius / 8));
                using (var fmt = new CanvasTextFormat
                {
                    FontFamily = FontFamily, FontSize = n.Radius * (n.Number >= 10 ? 1.0f : 1.25f), FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                })
                    ds.DrawText(n.Number.ToString(), new Rect(n.Center.X - n.Radius, n.Center.Y - n.Radius, n.Radius * 2, n.Radius * 2),
                        IsLight(n.Color) ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), fmt);
                break;
        }
    }

    public static CanvasTextLayout CreateTextLayout(ICanvasResourceCreator rc, string text, float fontSize)
    {
        using var fmt = new CanvasTextFormat { FontFamily = FontFamily, FontSize = fontSize, WordWrapping = CanvasWordWrapping.NoWrap };
        return new CanvasTextLayout(rc, text, fmt, 100000f, 100000f);
    }

    /// <summary>Layout size (pixels) of a text annotation, used for hit testing and selection.</summary>
    public static Size MeasureText(ICanvasResourceCreator rc, string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text)) return new Size(0, fontSize * 1.3);
        using var layout = CreateTextLayout(rc, text, fontSize);
        var b = layout.LayoutBounds;
        return new Size(Math.Ceiling(b.Width), Math.Ceiling(b.Height));
    }

    public static bool IsLight(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 170;

    /// <summary>The whole source downscaled by `block` (area-averaged); drawn back with nearest-neighbour → mosaic cells aligned to the image grid.</summary>
    private CanvasRenderTarget GetMosaic(int block)
    {
        block = Math.Max(2, block);
        if (_mosaicCache.TryGetValue(block, out var rt)) return rt;
        double sw = Source.SizeInPixels.Width, sh = Source.SizeInPixels.Height;
        rt = new CanvasRenderTarget(Source.Device, (float)Math.Ceiling(sw / block), (float)Math.Ceiling(sh / block), 96f);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            // dest is exactly size/block so partial edge cells keep the same grid
            ds.DrawImage(Source, new Rect(0, 0, sw / block, sh / block), new Rect(0, 0, sw, sh), 1f, CanvasImageInterpolation.HighQualityCubic);
        }
        _mosaicCache[block] = rt;
        return rt;
    }

    private void ClearCache()
    {
        foreach (var rt in _mosaicCache.Values) rt.Dispose();
        _mosaicCache.Clear();
    }

    // ---------------------------------------------------------------- export

    /// <summary>Renders doc at original pixel resolution (crop size). Caller disposes.</summary>
    public CanvasRenderTarget Render(EditorDocument doc)
    {
        var crop = doc.Crop;
        var rt = new CanvasRenderTarget(Source.Device, (float)crop.Width, (float)crop.Height, 96f);
        using var ds = rt.CreateDrawingSession();
        ds.Clear(Color.FromArgb(0, 0, 0, 0));
        ds.Transform = Matrix3x2.CreateTranslation((float)-crop.X, (float)-crop.Y);
        Draw(ds, doc, exporting: true);
        return rt;
    }

    /// <summary>Encodes to path (format from extension), via a temp file so a failed write never truncates the original.</summary>
    public async Task SaveAsync(EditorDocument doc, string path)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => CanvasBitmapFileFormat.Jpeg,
            ".bmp" => CanvasBitmapFileFormat.Bmp,
            ".gif" => CanvasBitmapFileFormat.Gif,
            ".tif" or ".tiff" => CanvasBitmapFileFormat.Tiff,
            ".jxr" => CanvasBitmapFileFormat.JpegXR,
            _ => CanvasBitmapFileFormat.Png,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var rt = Render(doc))
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
                await rt.SaveAsync(fs.AsRandomAccessStream(), format, 0.95f);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Puts the result on the clipboard as a bitmap (+ "PNG" for apps that prefer it). UI thread.</summary>
    public async Task CopyToClipboardAsync(EditorDocument doc)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var rt = Render(doc))
            await rt.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        package.SetData("PNG", stream.CloneStream());
        Clipboard.SetContent(package);
        try { Clipboard.Flush(); } catch { /* stays available while the process lives */ }
    }

    public void Dispose()
    {
        ClearCache();
        _round?.Dispose();
        Source.Dispose();
    }
}
