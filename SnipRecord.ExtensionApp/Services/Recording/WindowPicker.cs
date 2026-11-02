using System.Diagnostics;
using static SnipRecord.ExtensionApp.Services.Recording.Win32;

namespace SnipRecord.ExtensionApp.Services.Recording;

/// <summary>
/// Snipping-Tool-style window picker. A nearly invisible (alpha 1) topmost layer spanning the virtual
/// screen swallows the mouse; the top-level window under the cursor is found by walking EnumWindows in
/// Z-order (WindowFromPoint would just return our own layer) and highlighted with a tint + frame.
/// Left click selects, Esc / right click cancels.
/// </summary>
internal static class WindowPicker
{
    private static readonly HashSet<string> s_skipClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Shell_InputSwitchTopLevelWindow", "SnipRecord.PickerCatcher", "SnipRecord.PickerTint",
        "SnipRecord.PickerFrame", "SnipRecord.RecBorder", "SnipRecord.RecBar",
    };

    /// <summary>Returns the chosen top-level HWND, or <see cref="IntPtr.Zero"/> if cancelled.</summary>
    public static async Task<IntPtr> PickAsync(CancellationToken cancellationToken = default)
    {
        var ui = await UiThread.StartAsync("SnipRecord window picker").ConfigureAwait(false);
        var tcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        PickerSession? session = null;
        ui.Post(() =>
        {
            try
            {
                session = new PickerSession(result =>
                {
                    tcs.TrySetResult(result);
                    ui.Shutdown(() => session?.Close());
                });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                ui.Shutdown(() => session?.Close());
            }
        });

        using (cancellationToken.Register(() => ui.Post(() => session?.Finish(IntPtr.Zero))))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>The topmost capturable top-level window containing the screen point, or zero.</summary>
    public static IntPtr FindWindowAt(int x, int y)
    {
        uint ownPid = (uint)Environment.ProcessId;
        IntPtr shell = GetShellWindow();
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd, ownPid, shell)) return true;
            if (TryGetFrameBounds(hwnd, out var r) && r.Contains(x, y))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsCandidate(IntPtr hwnd, uint ownPid, IntPtr shell)
    {
        if (hwnd == shell || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return false;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TRANSPARENT) != 0) return false; // click-through overlays
        if (IsCloaked(hwnd)) return false;                 // other virtual desktops, suspended UWP
        if (s_skipClasses.Contains(GetClassName(hwnd))) return false;
        if ((ex & WS_EX_TOOLWINDOW) != 0 && GetWindowTextLengthW(hwnd) == 0) return false; // tooltips, shadows
        if (!TryGetFrameBounds(hwnd, out var r) || r.Width < 8 || r.Height < 8) return false;
        return true;
    }

    private sealed class PickerSession
    {
        private const int TimerId = 1;
        private readonly Action<IntPtr> _done;
        private readonly CatcherWindow _catcher;
        private readonly SolidOverlayWindow _tint;
        private readonly SolidOverlayWindow _frame;
        private IntPtr _current;
        private bool _finished;
        private bool _escWasDown = true; // ignore an Esc already held when the picker opens

        public PickerSession(Action<IntPtr> done)
        {
            _done = done;
            _catcher = new CatcherWindow(this);
            _tint = new SolidOverlayWindow("SnipRecord.PickerTint", RGB(0, 120, 212), 48, 0, false);
            _frame = new SolidOverlayWindow("SnipRecord.PickerFrame", RGB(0, 120, 212), 255, 3, false);

            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            SetWindowPos(_catcher.Handle, HWND_TOPMOST, vx, vy, vw, vh, SWP_SHOWWINDOW);
            SetForegroundWindow(_catcher.Handle); // best effort, for WM_KEYDOWN; Esc is also polled
            SetTimer(_catcher.Handle, new IntPtr(TimerId), 30, IntPtr.Zero);
            UpdateHover();
        }

        public void UpdateHover()
        {
            if (_finished) return;
            if (!GetCursorPos(out var pt)) return;
            var hwnd = FindWindowAt(pt.X, pt.Y);
            if (hwnd == IntPtr.Zero || !TryGetFrameBounds(hwnd, out var r))
            {
                _current = IntPtr.Zero;
                _tint.Hide();
                _frame.Hide();
                return;
            }
            _current = hwnd;
            _tint.ShowAround(r);
            _frame.ShowAround(r);
        }

        public void OnTimer()
        {
            bool escDown = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
            if (escDown && !_escWasDown) { Finish(IntPtr.Zero); return; }
            _escWasDown = escDown;
            UpdateHover(); // follow windows that move/appear under a still cursor
        }

        public void OnClick()
        {
            UpdateHover();
            if (_current != IntPtr.Zero) Finish(_current);
        }

        public void Finish(IntPtr result)
        {
            if (_finished) return;
            _finished = true;
            Close();
            _done(result);
        }

        public void Close()
        {
            try
            {
                if (_catcher.Handle != IntPtr.Zero) KillTimer(_catcher.Handle, new IntPtr(TimerId));
                _tint.Dispose();
                _frame.Dispose();
                _catcher.Dispose();
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    private sealed class CatcherWindow : Win32Window
    {
        private readonly PickerSession _session;
        private bool _down;

        public CatcherWindow(PickerSession session)
        {
            _session = session;
            CreateHandle("SnipRecord.PickerCatcher", WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IDC_CROSS);
            // Alpha 1: invisible to the eye but still hit-testable (alpha 0 pixels would be click-through).
            SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
        }

        protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
        {
            handled = true;
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    _session.UpdateHover();
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                    _down = true;
                    SetCapture(Handle);
                    return IntPtr.Zero;
                case WM_LBUTTONUP:
                    ReleaseCapture();
                    if (_down) { _down = false; _session.OnClick(); }
                    return IntPtr.Zero;
                case WM_RBUTTONDOWN:
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    _session.Finish(IntPtr.Zero);
                    return IntPtr.Zero;
                case WM_KEYDOWN:
                    if ((int)wParam == VK_ESCAPE) _session.Finish(IntPtr.Zero);
                    return IntPtr.Zero;
                case WM_TIMER:
                    _session.OnTimer();
                    return IntPtr.Zero;
                case WM_ERASEBKGND:
                    return new IntPtr(1);
                case WM_PAINT:
                    // Paint black; at alpha 1 it is imperceptible.
                    var hdc = BeginPaint(Handle, out var ps);
                    GetClientRect(Handle, out var rc);
                    var brush = CreateSolidBrush(0);
                    FillRect(hdc, ref rc, brush);
                    DeleteObject(brush);
                    EndPaint(Handle, ref ps);
                    return IntPtr.Zero;
                case WM_CLOSE:
                    _session.Finish(IntPtr.Zero);
                    return IntPtr.Zero;
            }
            handled = false;
            return IntPtr.Zero;
        }
    }
}
