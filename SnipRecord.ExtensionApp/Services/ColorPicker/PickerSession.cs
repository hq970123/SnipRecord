using System.Diagnostics;
using System.Runtime.InteropServices;
using static SnipRecord.ExtensionApp.Services.ColorPicker.NativeMethods;

namespace SnipRecord.ExtensionApp.Services.ColorPicker;

/// <summary>A picked screen colour.</summary>
public readonly record struct PickedColor(byte R, byte G, byte B);

/// <summary>
/// One colour-pick session, pure Win32 + GDI. <see cref="Run"/> must be called on a dedicated
/// thread (it runs its own message loop and blocks until the user picks or cancels):
///  1. freezes the whole virtual screen with BitBlt(SRCCOPY | CAPTUREBLT) into a 32-bpp DIB;
///  2. shows one topmost popup per monitor (physical pixels, PerMonitorV2) painting that frozen image;
///  3. draws a loupe (11×11 source pixels, 10× at 100 % DPI, crosshair, HEX + RGB text) next to the cursor;
///  4. arrow keys nudge 1 px (Shift = 10 px), left click / Enter / Space picks, Esc / right click cancels,
///     auto-cancel after 2 minutes. Keys come from a WH_KEYBOARD_LL hook so they work even if the
///     overlay could not take foreground; window WM_KEYDOWN is handled too as a fallback.
/// </summary>
internal sealed class PickerSession
{
    private const string ClassName = "SnipRecord.ColorPickerOverlay";
    private const uint WM_PICKER_KEY = WM_APP + 1;
    private const uint TimeoutMs = 120_000;
    private const int Radius = 5;            // 11×11 source pixels
    private const int GridCells = Radius * 2 + 1;

    private static readonly object ClassGate = new();
    private static readonly WndProc WndProcDelegate = StaticWndProc; // rooted for the process lifetime
    private static bool _classRegistered;

    [ThreadStatic] private static PickerSession? _current;

    private sealed class Overlay
    {
        public IntPtr Hwnd;
        public RECT Bounds;          // monitor rect, screen (physical) coordinates
        public IntPtr BackDC;
        public IntPtr BackBmp;
        public IntPtr BackOld;
        public IntPtr Font;
        public double Scale = 1.0;
        public RECT? LastLoupe;      // client coordinates, already inflated
    }

    private readonly List<Overlay> _overlays = new();
    private int _vx, _vy, _vw, _vh;
    private IntPtr _snapDC, _snapBmp, _snapOld, _bits;
    private int _px, _py;
    private bool _leftDown;
    private bool _done;
    private PickedColor? _result;
    private IntPtr _hook;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _timerHwnd;

    private IntPtr _brushWhite, _brushBlack, _brushPanel, _brushOutside;

    /// <summary>Blocks until the user picks (returns the colour) or cancels (null).</summary>
    public static PickedColor? Run()
    {
        var session = new PickerSession();
        _current = session;
        try
        {
            return session.RunCore();
        }
        finally
        {
            session.Cleanup();
            _current = null;
        }
    }

    private PickedColor? RunCore()
    {
        try
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1607: rely on the process-wide PerMonitorV2 manifest setting.
        }

        if (!CaptureScreen()) return null;
        if (!EnsureWindowClass()) return null;

        _brushWhite = CreateSolidBrush(Rgb(255, 255, 255));
        _brushBlack = CreateSolidBrush(Rgb(0, 0, 0));
        _brushPanel = CreateSolidBrush(Rgb(32, 32, 32));
        _brushOutside = CreateSolidBrush(Rgb(64, 64, 64));

        var monitors = EnumerateMonitors();
        if (monitors.Count == 0) monitors.Add(new RECT(_vx, _vy, _vx + _vw, _vy + _vh));

        var hInstance = GetModuleHandle(null);
        foreach (var bounds in monitors)
        {
            var hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, ClassName, "SnipRecord 取色", WS_POPUP,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Debug.WriteLine($"[SnipRecord] CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                continue;
            }
            var ov = new Overlay { Hwnd = hwnd, Bounds = bounds };
            InitBackBuffer(ov);
            _overlays.Add(ov);
        }
        if (_overlays.Count == 0) return null;

        if (GetCursorPos(out var pt))
        {
            _px = Math.Clamp(pt.X, _vx, _vx + _vw - 1);
            _py = Math.Clamp(pt.Y, _vy, _vy + _vh - 1);
        }
        else
        {
            _px = _vx + _vw / 2;
            _py = _vy + _vh / 2;
        }

        foreach (var ov in _overlays)
        {
            SetWindowPos(ov.Hwnd, HWND_TOPMOST, ov.Bounds.Left, ov.Bounds.Top, ov.Bounds.Width, ov.Bounds.Height, SWP_SHOWWINDOW);
            UpdateWindow(ov.Hwnd);
        }
        var active = OverlayAt(_px, _py) ?? _overlays[0];
        ForceForeground(active.Hwnd);
        UpdateLoupe();

        _hookProc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Debug.WriteLine($"[SnipRecord] keyboard hook failed: {Marshal.GetLastWin32Error()}");

        _timerHwnd = _overlays[0].Hwnd;
        SetTimer(_timerHwnd, (UIntPtr)1, TimeoutMs, IntPtr.Zero);

        while (!_done)
        {
            var r = GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (r == 0 || r == -1) break;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return _result;
    }

    // ------------------------------------------------------------------ capture

    private bool CaptureScreen()
    {
        _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (_vw <= 0 || _vh <= 0) return false;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;
        try
        {
            _snapDC = CreateCompatibleDC(screen);
            if (_snapDC == IntPtr.Zero) return false;

            var bih = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = _vw,
                biHeight = -_vh, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };
            _snapBmp = CreateDIBSection(screen, ref bih, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_snapBmp == IntPtr.Zero || _bits == IntPtr.Zero) return false;
            _snapOld = SelectObject(_snapDC, _snapBmp);

            if (!BitBlt(_snapDC, 0, 0, _vw, _vh, screen, _vx, _vy, SRCCOPY | CAPTUREBLT))
            {
                Debug.WriteLine($"[SnipRecord] BitBlt failed: {Marshal.GetLastWin32Error()}");
                return false;
            }
            GdiFlush();
            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private bool TryGetPixel(int x, int y, out byte r, out byte g, out byte b)
    {
        var sx = x - _vx;
        var sy = y - _vy;
        if (_bits == IntPtr.Zero || sx < 0 || sy < 0 || sx >= _vw || sy >= _vh)
        {
            r = g = b = 0;
            return false;
        }
        var offset = ((long)sy * _vw + sx) * 4;
        var v = Marshal.ReadInt32(_bits + (nint)offset); // BGRA in memory
        b = (byte)(v & 0xFF);
        g = (byte)((v >> 8) & 0xFF);
        r = (byte)((v >> 16) & 0xFF);
        return true;
    }

    private static List<RECT> EnumerateMonitors()
    {
        var list = new List<RECT>();
        MonitorEnumProc proc = (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            list.Add(GetMonitorInfo(hMon, ref mi) ? mi.rcMonitor : rc);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        list.RemoveAll(r => r.Width <= 0 || r.Height <= 0);
        return list;
    }

    // ------------------------------------------------------------------ windows

    private static bool EnsureWindowClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered) return true;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, IDC_CROSS),
                hbrBackground = IntPtr.Zero,
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_CLASS_ALREADY_EXISTS)
                {
                    Debug.WriteLine($"[SnipRecord] RegisterClassEx failed: {err}");
                    return false;
                }
            }
            _classRegistered = true;
            return true;
        }
    }

    private void InitBackBuffer(Overlay ov)
    {
        uint dpi = 96;
        try { dpi = GetDpiForWindow(ov.Hwnd); } catch (EntryPointNotFoundException) { }
        if (dpi == 0) dpi = 96;
        ov.Scale = dpi / 96.0;

        var wdc = GetDC(ov.Hwnd);
        try
        {
            ov.BackDC = CreateCompatibleDC(wdc);
            ov.BackBmp = CreateCompatibleBitmap(wdc, Math.Max(1, ov.Bounds.Width), Math.Max(1, ov.Bounds.Height));
            ov.BackOld = SelectObject(ov.BackDC, ov.BackBmp);
        }
        finally
        {
            ReleaseDC(ov.Hwnd, wdc);
        }

        ov.Font = CreateFont(-(int)Math.Round(11 * ov.Scale), 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, FIXED_PITCH | FF_MODERN, "Consolas");
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        var me = GetCurrentThreadId();
        var fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0u;
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }

    private Overlay? OverlayAt(int x, int y)
    {
        foreach (var ov in _overlays)
        {
            if (ov.Bounds.Contains(x, y)) return ov;
        }
        return null;
    }

    private Overlay? Find(IntPtr hwnd)
    {
        foreach (var ov in _overlays)
        {
            if (ov.Hwnd == hwnd) return ov;
        }
        return null;
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var s = _current;
        if (s is not null)
        {
            try
            {
                if (s.HandleMessage(hWnd, msg, wParam, lParam, out var result)) return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SnipRecord] colour picker wndproc error: {ex}");
                s.Finish(null);
            }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private bool HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        var ov = Find(hWnd);
        if (ov is null) return false;

        switch (msg)
        {
            case WM_PAINT:
                Paint(ov);
                return true;

            case WM_ERASEBKGND:
                result = new IntPtr(1);
                return true;

            case WM_SETCURSOR:
                SetCursor(LoadCursor(IntPtr.Zero, IDC_CROSS));
                result = new IntPtr(1);
                return true;

            case WM_MOUSEMOVE:
                MoveTo(ov.Bounds.Left + LoWordSigned(lParam), ov.Bounds.Top + HiWordSigned(lParam));
                return true;

            case WM_LBUTTONDOWN:
                _leftDown = true;
                SetCapture(hWnd);
                MoveTo(ov.Bounds.Left + LoWordSigned(lParam), ov.Bounds.Top + HiWordSigned(lParam));
                return true;

            case WM_LBUTTONUP:
                // Only a press that started on the overlay picks (ignores the release of the
                // click that opened the picker).
                if (_leftDown)
                {
                    _leftDown = false;
                    ReleaseCapture();
                    MoveTo(ov.Bounds.Left + LoWordSigned(lParam), ov.Bounds.Top + HiWordSigned(lParam));
                    Pick();
                }
                return true;

            case WM_RBUTTONDOWN:
                return true; // cancel on release so the up-event doesn't reach the window below

            case WM_RBUTTONUP:
                Finish(null);
                return true;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_PICKER_KEY:
                return HandleKey((int)wParam.ToInt64());

            case WM_TIMER:
                Finish(null);
                return true;

            case WM_DPICHANGED:
                return true; // keep our physical monitor bounds; don't resize

            case WM_CLOSE:
                Finish(null);
                return true;

            case WM_DESTROY:
                ov.Hwnd = IntPtr.Zero;
                return true;
        }
        return false;
    }

    private bool HandleKey(int vk)
    {
        if (_done) return true;
        var step = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ? 10 : 1;
        switch (vk)
        {
            case VK_ESCAPE:
                Finish(null);
                return true;
            case VK_RETURN:
            case VK_SPACE:
                Pick();
                return true;
            case VK_LEFT: Nudge(-step, 0); return true;
            case VK_RIGHT: Nudge(step, 0); return true;
            case VK_UP: Nudge(0, -step); return true;
            case VK_DOWN: Nudge(0, step); return true;
        }
        return false;
    }

    private void Nudge(int dx, int dy)
    {
        var x = Math.Clamp(_px + dx, _vx, _vx + _vw - 1);
        var y = Math.Clamp(_py + dy, _vy, _vy + _vh - 1);
        SetCursorPos(x, y);
        MoveTo(x, y);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION && !_done && _overlays.Count > 0)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)kb.vkCode;
            if (vk is VK_ESCAPE or VK_RETURN or VK_SPACE or VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN)
            {
                var m = (uint)wParam.ToInt64();
                if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                {
                    var target = _timerHwnd != IntPtr.Zero ? _timerHwnd : _overlays[0].Hwnd;
                    PostMessage(target, WM_PICKER_KEY, new IntPtr(vk), IntPtr.Zero);
                }
                return new IntPtr(1); // swallow down and up while picking
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ------------------------------------------------------------------ state

    private void MoveTo(int x, int y)
    {
        x = Math.Clamp(x, _vx, _vx + _vw - 1);
        y = Math.Clamp(y, _vy, _vy + _vh - 1);
        if (x == _px && y == _py) return;
        _px = x;
        _py = y;
        UpdateLoupe();
    }

    /// <summary>Invalidates the old loupe area(s) and the new one.</summary>
    private void UpdateLoupe()
    {
        foreach (var ov in _overlays)
        {
            if (ov.LastLoupe is { } old && ov.Hwnd != IntPtr.Zero)
            {
                var r = old;
                InvalidateRect(ov.Hwnd, ref r, false);
            }
            ov.LastLoupe = null;
        }
        var cur = OverlayAt(_px, _py);
        if (cur is null || cur.Hwnd == IntPtr.Zero) return;
        var lr = LoupeRect(cur, _px - cur.Bounds.Left, _py - cur.Bounds.Top);
        var inflated = new RECT(lr.Left - 2, lr.Top - 2, lr.Right + 2, lr.Bottom + 2);
        cur.LastLoupe = inflated;
        InvalidateRect(cur.Hwnd, ref inflated, false);
    }

    private void Pick()
    {
        if (TryGetPixel(_px, _py, out var r, out var g, out var b)) Finish(new PickedColor(r, g, b));
        else Finish(null);
    }

    private void Finish(PickedColor? result)
    {
        if (_done) return;
        _done = true;
        _result = result;
        try { ReleaseCapture(); } catch { }
        foreach (var ov in _overlays)
        {
            if (ov.Hwnd != IntPtr.Zero)
            {
                var h = ov.Hwnd;
                DestroyWindow(h);
                ov.Hwnd = IntPtr.Zero;
            }
        }
        PostQuitMessage(0);
    }

    // ------------------------------------------------------------------ painting

    private readonly struct LoupeMetrics
    {
        public readonly int Cell, Border, Box, Panel, Offset;

        public LoupeMetrics(double scale)
        {
            Cell = Math.Max(6, (int)Math.Round(10 * scale));
            Border = Math.Max(1, (int)Math.Round(1 * scale));
            Box = GridCells * Cell + 4 * Border;     // ~114 px at 100 %
            Panel = Math.Max(30, (int)Math.Round(36 * scale));
            Offset = Math.Max(16, (int)Math.Round(24 * scale));
        }
    }

    private static RECT LoupeRect(Overlay ov, int localX, int localY)
    {
        var m = new LoupeMetrics(ov.Scale);
        int w = m.Box, h = m.Box + m.Panel;
        var left = localX + m.Offset;
        var top = localY + m.Offset;
        if (left + w > ov.Bounds.Width) left = localX - m.Offset - w;
        if (top + h > ov.Bounds.Height) top = localY - m.Offset - h;
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        return new RECT(left, top, left + w, top + h);
    }

    private void Paint(Overlay ov)
    {
        var hdc = BeginPaint(ov.Hwnd, out var ps);
        try
        {
            var r = ps.rcPaint;
            if (hdc == IntPtr.Zero || r.Width <= 0 || r.Height <= 0 || ov.BackDC == IntPtr.Zero) return;

            BitBlt(ov.BackDC, r.Left, r.Top, r.Width, r.Height, _snapDC,
                r.Left + ov.Bounds.Left - _vx, r.Top + ov.Bounds.Top - _vy, SRCCOPY);

            if (ov.Bounds.Contains(_px, _py))
            {
                DrawLoupe(ov, LoupeRect(ov, _px - ov.Bounds.Left, _py - ov.Bounds.Top));
            }

            BitBlt(hdc, r.Left, r.Top, r.Width, r.Height, ov.BackDC, r.Left, r.Top, SRCCOPY);
        }
        finally
        {
            EndPaint(ov.Hwnd, ref ps);
        }
    }

    private void DrawLoupe(Overlay ov, RECT rect)
    {
        var dc = ov.BackDC;
        var m = new LoupeMetrics(ov.Scale);
        var bw = m.Border;

        // Box: white outer line, black inner line, then the zoomed grid.
        var box = new RECT(rect.Left, rect.Top, rect.Left + m.Box, rect.Top + m.Box);
        FillRect(dc, ref box, _brushWhite);
        var inner = new RECT(box.Left + bw, box.Top + bw, box.Right - bw, box.Bottom - bw);
        FillRect(dc, ref inner, _brushBlack);

        var gx = box.Left + 2 * bw;
        var gy = box.Top + 2 * bw;
        for (var dy = -Radius; dy <= Radius; dy++)
        {
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                var cell = new RECT(
                    gx + (dx + Radius) * m.Cell, gy + (dy + Radius) * m.Cell,
                    gx + (dx + Radius + 1) * m.Cell, gy + (dy + Radius + 1) * m.Cell);
                if (TryGetPixel(_px + dx, _py + dy, out var cr, out var cg, out var cb))
                {
                    var brush = CreateSolidBrush(Rgb(cr, cg, cb));
                    FillRect(dc, ref cell, brush);
                    DeleteObject(brush);
                }
                else
                {
                    FillRect(dc, ref cell, _brushOutside);
                }
            }
        }

        // Crosshair on the centre pixel: black frame outside, white frame inside.
        var c = new RECT(gx + Radius * m.Cell, gy + Radius * m.Cell, gx + (Radius + 1) * m.Cell, gy + (Radius + 1) * m.Cell);
        DrawFrame(dc, new RECT(c.Left - bw, c.Top - bw, c.Right + bw, c.Bottom + bw), bw, _brushBlack);
        DrawFrame(dc, c, bw, _brushWhite);

        // Text panel: HEX on the first line, RGB on the second.
        var panel = new RECT(rect.Left, box.Bottom, rect.Left + m.Box, box.Bottom + m.Panel);
        FillRect(dc, ref panel, _brushPanel);
        DrawFrame(dc, panel, bw, _brushWhite);

        TryGetPixel(_px, _py, out var r, out var g, out var b);
        var oldFont = ov.Font != IntPtr.Zero ? SelectObject(dc, ov.Font) : IntPtr.Zero;
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, Rgb(255, 255, 255));
        var half = panel.Height / 2;
        var line1 = new RECT(panel.Left + bw, panel.Top + bw, panel.Right - bw, panel.Top + half);
        var line2 = new RECT(panel.Left + bw, panel.Top + half, panel.Right - bw, panel.Bottom - bw);
        const uint flags = DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX;
        var hex = ColorFormatter.ToHex(r, g, b);
        var rgb = ColorFormatter.ToRgb(r, g, b);
        DrawText(dc, hex, hex.Length, ref line1, flags);
        DrawText(dc, rgb, rgb.Length, ref line2, flags);
        if (oldFont != IntPtr.Zero) SelectObject(dc, oldFont);
    }

    private static void DrawFrame(IntPtr dc, RECT r, int t, IntPtr brush)
    {
        var top = new RECT(r.Left, r.Top, r.Right, r.Top + t);
        var bottom = new RECT(r.Left, r.Bottom - t, r.Right, r.Bottom);
        var left = new RECT(r.Left, r.Top, r.Left + t, r.Bottom);
        var right = new RECT(r.Right - t, r.Top, r.Right, r.Bottom);
        FillRect(dc, ref top, brush);
        FillRect(dc, ref bottom, brush);
        FillRect(dc, ref left, brush);
        FillRect(dc, ref right, brush);
    }

    // ------------------------------------------------------------------ cleanup

    private void Cleanup()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        GC.KeepAlive(_hookProc);

        if (!_done)
        {
            _done = true;
            foreach (var ov in _overlays)
            {
                if (ov.Hwnd != IntPtr.Zero) DestroyWindow(ov.Hwnd);
                ov.Hwnd = IntPtr.Zero;
            }
        }

        foreach (var ov in _overlays)
        {
            if (ov.BackDC != IntPtr.Zero)
            {
                if (ov.BackOld != IntPtr.Zero) SelectObject(ov.BackDC, ov.BackOld);
                DeleteDC(ov.BackDC);
            }
            if (ov.BackBmp != IntPtr.Zero) DeleteObject(ov.BackBmp);
            if (ov.Font != IntPtr.Zero) DeleteObject(ov.Font);
            ov.BackDC = ov.BackBmp = ov.BackOld = ov.Font = IntPtr.Zero;
        }
        _overlays.Clear();

        foreach (var brush in new[] { _brushWhite, _brushBlack, _brushPanel, _brushOutside })
        {
            if (brush != IntPtr.Zero) DeleteObject(brush);
        }
        _brushWhite = _brushBlack = _brushPanel = _brushOutside = IntPtr.Zero;

        if (_snapDC != IntPtr.Zero)
        {
            if (_snapOld != IntPtr.Zero) SelectObject(_snapDC, _snapOld);
            DeleteDC(_snapDC);
        }
        if (_snapBmp != IntPtr.Zero) DeleteObject(_snapBmp);
        _snapDC = _snapBmp = _snapOld = _bits = IntPtr.Zero;
    }
}
