using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using static SnipRecord.ExtensionApp.Services.FullScreen.FullScreenNative;

namespace SnipRecord.ExtensionApp.Services.FullScreen;

public enum FullScreenTarget { CurrentMonitor, AllMonitors }

/// <summary>
/// Native full-screen screenshot (GDI BitBlt with CAPTUREBLT) → PNG file + clipboard (CF_DIB and "PNG") + RecentStore.
/// Never throws; failures return null with <see cref="LastError"/> set (Chinese user text).
/// </summary>
public static class FullScreenCapture
{
    private static int _busy;

    /// <summary>Error of the last call that returned null (Chinese), else null.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Non-fatal issue of the last call (folder fallback, clipboard failure), else null.</summary>
    public static string? LastWarning { get; private set; }

    public static bool IsCapturing => Volatile.Read(ref _busy) != 0;

    public static async Task<string?> CaptureAsync(FullScreenTarget target, AppSettings? settings, RecentStore? recent,
        bool flash = true, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
        {
            LastError = "正在截图，请稍候";
            return null;
        }
        LastError = null; LastWarning = null;
        try
        {
            var delay = settings?.DelaySeconds ?? 0;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var shot = await RunOnThread(() => Grab(target), sta: false).ConfigureAwait(false);
            if (shot is null) { LastError ??= "无法截取屏幕"; return null; }
            if (flash) StartFlash(shot.X, shot.Y, shot.Width, shot.Height);

            var png = await EncodePngAsync(shot).ConfigureAwait(false);
            var path = SaveFile(png, settings);
            if (path is null) return null;

            var clipOk = await RunOnThread(() => SetClipboard(shot, png), sta: true).ConfigureAwait(false);
            if (!clipOk) LastWarning ??= "截图已保存，但复制到剪贴板失败";

            try { (recent ?? RecentStore.Shared).AddCapture(path, false); }
            catch (Exception ex) { Debug.WriteLine($"[SnipRecord] FullScreen recent: {ex.Message}"); }
            return path;
        }
        catch (OperationCanceledException) { LastError = "已取消"; return null; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] FullScreen failed: {ex}");
            LastError = "全屏截图失败：" + ex.Message;
            return null;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private sealed record Shot(int X, int Y, int Width, int Height, byte[] Bgra);

    private static Task<T> RunOnThread<T>(Func<T> work, bool sta)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); } catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "SnipRecord.FullScreen" };
        if (sta) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    private static Shot? Grab(FullScreenTarget target)
    {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        int x, y, w, h;
        if (target == FullScreenTarget.AllMonitors)
        {
            x = GetSystemMetrics(SM_XVIRTUALSCREEN); y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            w = GetSystemMetrics(SM_CXVIRTUALSCREEN); h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }
        else
        {
            GetCursorPos(out var pt);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST), ref mi)) { LastError = "无法获取显示器信息"; return null; }
            x = mi.rcMonitor.Left; y = mi.rcMonitor.Top; w = mi.rcMonitor.Right - x; h = mi.rcMonitor.Bottom - y;
        }
        if (w <= 0 || h <= 0) { LastError = "屏幕尺寸无效"; return null; }

        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero) { LastError = "无法访问屏幕"; return null; }
            mem = CreateCompatibleDC(screen);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            bmp = CreateDIBSection(screen, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero || bits == IntPtr.Zero) { LastError = "内存不足，无法截图"; return null; }
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT)) { LastError = $"截屏失败 (错误 {Marshal.GetLastWin32Error()})"; return null; }
            var data = new byte[checked(w * h * 4)];
            Marshal.Copy(bits, data, 0, data.Length);
            for (var i = 3; i < data.Length; i += 4) data[i] = 255; // GDI leaves alpha undefined
            return new Shot(x, y, w, h, data);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static async Task<byte[]> EncodePngAsync(Shot s)
    {
        using var ms = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)s.Width, (uint)s.Height, 96, 96, s.Bgra);
        await enc.FlushAsync();
        var bytes = new byte[ms.Size];
        ms.Seek(0);
        using var reader = new DataReader(ms.GetInputStreamAt(0));
        await reader.LoadAsync((uint)ms.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string? SaveFile(byte[] png, AppSettings? settings)
    {
        var fallback = new AppSettings().GetImageFolder();
        var preferred = settings?.GetImageFolder() ?? fallback;
        var name = $"全屏_{DateTime.Now:yyyyMMdd_HHmmss}";
        foreach (var folder in preferred == fallback ? new[] { fallback } : new[] { preferred, fallback })
        {
            try
            {
                Directory.CreateDirectory(folder);
                for (var n = 1; ; n++)
                {
                    var path = Path.Combine(folder, n == 1 ? name + ".png" : $"{name}_{n}.png");
                    try
                    {
                        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                        fs.Write(png);
                        if (folder != preferred) LastWarning = "无法写入设置的图片文件夹，已保存到默认文件夹";
                        return path;
                    }
                    catch (IOException) when (File.Exists(path) && n < 1000) { }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[SnipRecord] FullScreen save to {folder}: {ex.Message}"); }
        }
        LastError = "无法保存截图文件";
        return null;
    }

    private static bool SetClipboard(Shot s, byte[] png)
    {
        // CF_DIB: BITMAPINFOHEADER + bottom-up 32-bpp rows.
        var stride = s.Width * 4;
        var dib = new byte[40 + stride * s.Height];
        var hdr = new BITMAPINFOHEADER { biSize = 40, biWidth = s.Width, biHeight = s.Height, biPlanes = 1, biBitCount = 32, biSizeImage = stride * s.Height };
        var hp = GCHandle.Alloc(hdr, GCHandleType.Pinned);
        try { Marshal.Copy(hp.AddrOfPinnedObject(), dib, 0, 40); } finally { hp.Free(); }
        for (var row = 0; row < s.Height; row++)
            System.Buffer.BlockCopy(s.Bgra, row * stride, dib, 40 + (s.Height - 1 - row) * stride, stride);

        var opened = false;
        for (var i = 0; i < 10 && !(opened = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(50);
        if (!opened) return false;
        try
        {
            EmptyClipboard();
            var ok = Put(CF_DIB, dib);
            var pngFmt = RegisterClipboardFormat("PNG");
            if (pngFmt != 0) Put(pngFmt, png);
            return ok;
        }
        finally { CloseClipboard(); }
    }

    private static bool Put(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) return false;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return false; }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) != IntPtr.Zero) return true; // clipboard owns h now
        GlobalFree(h);
        return false;
    }

    // ---- brief white fade over the captured area (excluded from capture) ----
    private static readonly WndProc s_wndProc = DefWindowProc;
    private static string? s_flashClass;

    private static void StartFlash(int x, int y, int w, int h)
    {
        var t = new Thread(() =>
        {
            try
            {
                SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                var inst = GetModuleHandle(null);
                if (s_flashClass is null)
                {
                    var wc = new WNDCLASSEX
                    {
                        cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                        hInstance = inst, hbrBackground = GetStockObject(WHITE_BRUSH), lpszClassName = "SnipRecord.Flash",
                    };
                    if (RegisterClassEx(ref wc) == 0) return;
                    s_flashClass = wc.lpszClassName;
                }
                var hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
                    s_flashClass, null, WS_POPUP, x, y, w, h, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) return;
                try
                {
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                    SetLayeredWindowAttributes(hwnd, 0, 140, LWA_ALPHA);
                    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    UpdateWindow(hwnd);
                    for (var a = 140; a > 0; a -= 14)
                    {
                        SetLayeredWindowAttributes(hwnd, 0, (byte)a, LWA_ALPHA);
                        while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
                        Thread.Sleep(20);
                    }
                }
                finally { DestroyWindow(hwnd); }
            }
            catch (Exception ex) { Debug.WriteLine($"[SnipRecord] flash: {ex.Message}"); }
        }) { IsBackground = true, Name = "SnipRecord.Flash" };
        t.Start();
    }
}
