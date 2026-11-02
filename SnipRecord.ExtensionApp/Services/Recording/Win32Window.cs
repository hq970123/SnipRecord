using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static SnipRecord.ExtensionApp.Services.Recording.Win32;

namespace SnipRecord.ExtensionApp.Services.Recording;

/// <summary>
/// Tiny raw Win32 window base. All instances must be created, used and destroyed on the
/// thread that runs their message loop (see <see cref="UiThread"/>).
/// </summary>
internal abstract class Win32Window : IDisposable
{
    private static readonly WndProc s_proc = StaticWndProc;
    private static readonly IntPtr s_procPtr = Marshal.GetFunctionPointerForDelegate(s_proc);
    private static readonly ConcurrentDictionary<IntPtr, Win32Window> s_windows = new();
    private static readonly HashSet<string> s_registered = new();

    [ThreadStatic] private static Win32Window? t_creating;

    public IntPtr Handle { get; private set; }

    protected void CreateHandle(string className, int exStyle, int style, int x, int y, int w, int h,
        IntPtr parent, int cursorId)
    {
        var hinst = GetModuleHandleW(null);
        lock (s_registered)
        {
            if (!s_registered.Contains(className))
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = s_procPtr,
                    hInstance = hinst,
                    hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(cursorId)),
                    lpszClassName = className,
                };
                if (RegisterClassExW(ref wc) == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */) throw new Win32Exception(err);
                }
                s_registered.Add(className);
            }
        }

        t_creating = this;
        try
        {
            var hwnd = CreateWindowExW(exStyle, className, "SnipRecord", style, x, y, w, h, parent,
                IntPtr.Zero, hinst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Handle = hwnd;
            s_windows[hwnd] = this;
        }
        finally
        {
            t_creating = null;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!s_windows.TryGetValue(hwnd, out var win))
        {
            win = t_creating;
            if (win != null)
            {
                win.Handle = hwnd;
                s_windows[hwnd] = win;
            }
        }

        if (win != null)
        {
            if (msg == WM_NCDESTROY)
            {
                s_windows.TryRemove(hwnd, out _);
                win.Handle = IntPtr.Zero;
                try { win.OnDestroyed(); } catch (Exception ex) { Debug.WriteLine(ex); }
                return DefWindowProcW(hwnd, msg, wParam, lParam);
            }

            try
            {
                var r = win.WndProc(msg, wParam, lParam, out bool handled);
                if (handled) return r;
            }
            catch (Exception ex)
            {
                // Never let a managed exception unwind through user32.
                Debug.WriteLine(ex);
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    protected virtual IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
    {
        handled = false;
        return IntPtr.Zero;
    }

    protected virtual void OnDestroyed() { }

    public void Invalidate()
    {
        if (Handle != IntPtr.Zero) InvalidateRect(Handle, IntPtr.Zero, false);
    }

    public virtual void Dispose()
    {
        var h = Handle;
        if (h != IntPtr.Zero) DestroyWindow(h);
    }
}

/// <summary>Hidden message-only window used to marshal work onto a <see cref="UiThread"/>.</summary>
internal sealed class InvokeWindow : Win32Window
{
    public const uint WM_INVOKE = WM_APP + 1;
    private readonly ConcurrentQueue<Action> _queue = new();

    public InvokeWindow()
    {
        CreateHandle("SnipRecord.Invoke", 0, 0, 0, 0, 0, 0, HWND_MESSAGE, IDC_ARROW);
    }

    public void Post(Action action)
    {
        _queue.Enqueue(action);
        var h = Handle;
        if (h != IntPtr.Zero) PostMessageW(h, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
    {
        if (msg == WM_INVOKE)
        {
            while (_queue.TryDequeue(out var a))
            {
                try { a(); } catch (Exception ex) { Debug.WriteLine(ex); }
            }
            handled = true;
            return IntPtr.Zero;
        }
        handled = false;
        return IntPtr.Zero;
    }
}

/// <summary>
/// A dedicated STA thread with a Win32 message loop (per-monitor-v2 DPI aware) for overlay windows,
/// so they work regardless of which thread / dispatcher the caller is on.
/// </summary>
internal sealed class UiThread : IDisposable
{
    private readonly Thread _thread;
    private InvokeWindow? _invoker;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _exited;

    private UiThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public static async Task<UiThread> StartAsync(string name)
    {
        var t = new UiThread(name);
        t._thread.Start();
        await t._ready.Task.ConfigureAwait(false);
        return t;
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    private void Run()
    {
        try
        {
            try { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch (EntryPointNotFoundException) { }
            _invoker = new InvokeWindow();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            return;
        }
        _ready.TrySetResult();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        _exited = true;
        try { _invoker.Dispose(); } catch { }
    }

    public void Post(Action action)
    {
        if (_exited) return;
        _invoker?.Post(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_exited) { tcs.SetException(new ObjectDisposedException(nameof(UiThread))); return tcs.Task; }
        Post(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync<bool>(() => { action(); return true; });

    /// <summary>Runs <paramref name="cleanup"/> on the thread, then ends its message loop.</summary>
    public void Shutdown(Action? cleanup = null)
    {
        Post(() =>
        {
            try { cleanup?.Invoke(); } catch (Exception ex) { Debug.WriteLine(ex); }
            PostQuitMessage(0);
        });
    }

    public void Dispose() => Shutdown();
}

/// <summary>
/// Click-through, non-activating, topmost solid-colour overlay. With <c>frameThickness &gt; 0</c> it is a
/// hollow frame drawn just outside the rectangle passed to <see cref="ShowAround"/>.
/// </summary>
internal sealed class SolidOverlayWindow : Win32Window
{
    private readonly int _logicalThickness;
    private IntPtr _brush;
    private RECT _lastRect;
    private bool _visible;

    public SolidOverlayWindow(string className, uint color, byte alpha, int logicalFrameThickness, bool excludeFromCapture)
    {
        _logicalThickness = logicalFrameThickness;
        _brush = CreateSolidBrush(color);
        CreateHandle(className,
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IDC_ARROW);
        SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);
        if (excludeFromCapture) ExcludeFromCapture(Handle);
    }

    public void ShowAround(RECT target)
    {
        if (Handle == IntPtr.Zero) return;
        RECT r = target;
        int t = 0;
        if (_logicalThickness > 0)
        {
            t = Math.Max(1, (int)Math.Round(_logicalThickness * ScaleForRect(target)));
            r = new RECT(target.Left - t, target.Top - t, target.Right + t, target.Bottom + t);
        }

        if (_visible && r.Equals(_lastRect)) return;
        bool sizeChanged = r.Width != _lastRect.Width || r.Height != _lastRect.Height;
        _lastRect = r;

        if (t > 0 && sizeChanged)
        {
            var outer = CreateRectRgn(0, 0, r.Width, r.Height);
            var inner = CreateRectRgn(t, t, r.Width - t, r.Height - t);
            CombineRgn(outer, outer, inner, RGN_DIFF);
            DeleteObject(inner);
            if (SetWindowRgn(Handle, outer, true) == 0) DeleteObject(outer); // on success the system owns it
        }

        SetWindowPos(Handle, HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _visible = true;
        Invalidate();
    }

    public void Hide()
    {
        if (Handle == IntPtr.Zero || !_visible) return;
        ShowWindow(Handle, SW_HIDE);
        _visible = false;
    }

    protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_ERASEBKGND:
                handled = true;
                return new IntPtr(1);
            case WM_PAINT:
                var hdc = BeginPaint(Handle, out var ps);
                GetClientRect(Handle, out var rc);
                FillRect(hdc, ref rc, _brush);
                EndPaint(Handle, ref ps);
                handled = true;
                return IntPtr.Zero;
        }
        handled = false;
        return IntPtr.Zero;
    }

    protected override void OnDestroyed()
    {
        if (_brush != IntPtr.Zero) { DeleteObject(_brush); _brush = IntPtr.Zero; }
    }
}
