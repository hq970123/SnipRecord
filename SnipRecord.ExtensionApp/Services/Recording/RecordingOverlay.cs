using System.Diagnostics;
using static SnipRecord.ExtensionApp.Services.Recording.Win32;

namespace SnipRecord.ExtensionApp.Services.Recording;

/// <summary>
/// Red frame around the recorded window plus the floating control bar (timer, pause/resume, stop).
/// Both are excluded from capture. Lives on a <see cref="UiThread"/>; every member must be called there.
/// </summary>
internal sealed class RecordingOverlay : IDisposable
{
    private readonly IntPtr _target;
    private readonly SolidOverlayWindow _border;
    private readonly RecordingControlBar _bar;
    private readonly Action _onTargetGone;
    private bool _goneRaised;

    public RecordingOverlay(IntPtr target, RecordingClock clock, Action onPauseToggle, Action onStop, Action onTargetGone)
    {
        _target = target;
        _onTargetGone = onTargetGone;
        _border = new SolidOverlayWindow("SnipRecord.RecBorder", RGB(232, 17, 35), 255, 2, excludeFromCapture: true);
        _bar = new RecordingControlBar(clock, onPauseToggle, onStop);
        _bar.Tick += Follow;
        Follow();
    }

    public void Refresh() => _bar.Invalidate();

    private void Follow()
    {
        if (!IsWindow(_target))
        {
            if (!_goneRaised) { _goneRaised = true; _onTargetGone(); }
            return;
        }

        if (IsIconic(_target) || IsCloaked(_target) || !IsWindowVisible(_target)
            || !TryGetFrameBounds(_target, out var r))
        {
            _border.Hide();
            _bar.EnsureVisible();
            return;
        }

        _border.ShowAround(r);
        _bar.FollowTarget(r);
    }

    public void Dispose()
    {
        try
        {
            _bar.Dispose();
            _border.Dispose();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
}

/// <summary>Owner-drawn GDI control bar. Draggable everywhere except on its buttons.</summary>
internal sealed class RecordingControlBar : Win32Window
{
    private const int TickTimerId = 1;
    private const int LogicalWidth = 176;
    private const int LogicalHeight = 40;

    private readonly RecordingClock _clock;
    private readonly Action _onPauseToggle;
    private readonly Action _onStop;

    private double _scale = 1;
    private int _w, _h;
    private IntPtr _font;
    private bool _userMoved;
    private int _hover; // 0 none, 1 pause, 2 stop
    private int _pressed;
    private int _tickCount;

    public event Action? Tick;

    public RecordingControlBar(RecordingClock clock, Action onPauseToggle, Action onStop)
    {
        _clock = clock;
        _onPauseToggle = onPauseToggle;
        _onStop = onStop;
        CreateHandle("SnipRecord.RecBar", WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            WS_POPUP, 0, 0, LogicalWidth, LogicalHeight, IntPtr.Zero, IDC_ARROW);
        SetLayeredWindowAttributes(Handle, 0, 240, LWA_ALPHA);
        ExcludeFromCapture(Handle);
        ApplyScale(1.0, force: true);
        SetTimer(Handle, new IntPtr(TickTimerId), 100, IntPtr.Zero);
    }

    private void ApplyScale(double scale, bool force = false)
    {
        if (!force && Math.Abs(scale - _scale) < 0.01) return;
        _scale = scale;
        _w = S(LogicalWidth);
        _h = S(LogicalHeight);
        if (_font != IntPtr.Zero) DeleteObject(_font);
        _font = CreateFontW(-S(14), 0, 0, 0, 600, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0,
            5 /*CLEARTYPE_QUALITY*/, 0, "Segoe UI");
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, _w, _h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        var rgn = CreateRoundRectRgn(0, 0, _w + 1, _h + 1, S(12), S(12));
        if (SetWindowRgn(Handle, rgn, true) == 0) DeleteObject(rgn);
    }

    private int S(int logical) => (int)Math.Round(logical * _scale);

    // Layout in client coordinates.
    private RECT PauseRect => new(S(98), S(4), S(98) + S(32), S(36));
    private RECT StopRect => new(S(136), S(4), S(136) + S(32), S(36));

    /// <summary>Centre the bar just above the target's top edge (inside it when there is no room).</summary>
    public void FollowTarget(RECT target)
    {
        if (Handle == IntPtr.Zero) return;
        ApplyScale(ScaleForRect(target));
        if (_userMoved)
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return;
        }
        var work = WorkAreaForRect(target);
        int gap = S(6);
        int x = target.Left + (target.Width - _w) / 2;
        int y = target.Top - _h - gap - S(2);
        if (y < work.Top) y = target.Top + gap;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - _w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - _h));
        SetWindowPos(Handle, HWND_TOPMOST, x, y, _w, _h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void EnsureVisible()
    {
        if (Handle == IntPtr.Zero) return;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private int HitButton(int x, int y)
    {
        if (PauseRect.Contains(x, y)) return 1;
        if (StopRect.Contains(x, y)) return 2;
        return 0;
    }

    protected override IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
    {
        handled = true;
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                var pt = new POINT { X = LoWord(lParam), Y = HiWord(lParam) };
                ScreenToClient(Handle, ref pt);
                return new IntPtr(HitButton(pt.X, pt.Y) != 0 ? HTCLIENT : HTCAPTION);
            }
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            case WM_EXITSIZEMOVE:
                _userMoved = true;
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
            {
                int hit = HitButton(LoWord(lParam), HiWord(lParam));
                if (hit != _hover) { _hover = hit; Invalidate(); }
                return IntPtr.Zero;
            }
            case WM_LBUTTONDOWN:
                _pressed = HitButton(LoWord(lParam), HiWord(lParam));
                if (_pressed != 0) SetCapture(Handle);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
            {
                int hit = HitButton(LoWord(lParam), HiWord(lParam));
                int pressed = _pressed;
                _pressed = 0;
                ReleaseCapture();
                if (pressed != 0 && hit == pressed)
                {
                    if (hit == 1) _onPauseToggle();
                    else if (hit == 2) _onStop();
                }
                Invalidate();
                return IntPtr.Zero;
            }
            case WM_TIMER:
                if ((int)wParam == TickTimerId)
                {
                    _tickCount++;
                    if (_hover != 0 && GetCursorPos(out var cp))
                    {
                        ScreenToClient(Handle, ref cp);
                        if (HitButton(cp.X, cp.Y) == 0) _hover = 0;
                    }
                    Tick?.Invoke();
                    if (_tickCount % 5 == 0 || _hover != 0) Invalidate();
                }
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_PAINT:
                Paint();
                return IntPtr.Zero;
        }
        handled = false;
        return IntPtr.Zero;
    }

    private void Paint()
    {
        var hdc = BeginPaint(Handle, out var ps);
        try
        {
            GetClientRect(Handle, out var rc);
            var mem = CreateCompatibleDC(hdc);
            var bmp = CreateCompatibleBitmap(hdc, rc.Width, rc.Height);
            var oldBmp = SelectObject(mem, bmp);
            try
            {
                Draw(mem, rc);
                BitBlt(hdc, 0, 0, rc.Width, rc.Height, mem, 0, 0, SRCCOPY);
            }
            finally
            {
                SelectObject(mem, oldBmp);
                DeleteObject(bmp);
                DeleteDC(mem);
            }
        }
        finally
        {
            EndPaint(Handle, ref ps);
        }
    }

    private void Draw(IntPtr dc, RECT rc)
    {
        Fill(dc, rc, RGB(32, 32, 32));
        bool paused = _clock.IsPaused;
        var oldPen = SelectObject(dc, GetStockObject(NULL_PEN));

        // Status dot: red while recording (blinks), amber when paused.
        bool dotOn = paused || (_tickCount / 5) % 2 == 0;
        var dotBrush = CreateSolidBrush(paused ? RGB(255, 185, 0) : dotOn ? RGB(232, 17, 35) : RGB(110, 20, 28));
        var oldBrush = SelectObject(dc, dotBrush);
        int cy = rc.Height / 2;
        Ellipse(dc, S(12), cy - S(5), S(12) + S(10), cy + S(5));
        SelectObject(dc, oldBrush);
        DeleteObject(dotBrush);

        // Timer text.
        var t = _clock.Elapsed;
        string text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        var oldFont = SelectObject(dc, _font);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(255, 255, 255));
        var tr = new RECT(S(28), 0, S(94), rc.Height);
        DrawTextW(dc, text, text.Length, ref tr, DT_LEFT | DT_VCENTER | DT_SINGLELINE);
        SelectObject(dc, oldFont);

        // Pause / resume button.
        var pr = PauseRect;
        if (_hover == 1) RoundFill(dc, pr, RGB(64, 64, 64));
        int pcx = (pr.Left + pr.Right) / 2, pcy = (pr.Top + pr.Bottom) / 2;
        if (paused)
        {
            var tri = new[]
            {
                new POINT { X = pcx - S(5), Y = pcy - S(7) },
                new POINT { X = pcx - S(5), Y = pcy + S(7) },
                new POINT { X = pcx + S(7), Y = pcy },
            };
            var b = CreateSolidBrush(RGB(255, 255, 255));
            var ob = SelectObject(dc, b);
            Polygon(dc, tri, tri.Length);
            SelectObject(dc, ob);
            DeleteObject(b);
        }
        else
        {
            Fill(dc, new RECT(pcx - S(6), pcy - S(7), pcx - S(2), pcy + S(7)), RGB(255, 255, 255));
            Fill(dc, new RECT(pcx + S(2), pcy - S(7), pcx + S(6), pcy + S(7)), RGB(255, 255, 255));
        }

        // Stop button.
        var sr = StopRect;
        if (_hover == 2) RoundFill(dc, sr, RGB(64, 64, 64));
        int scx = (sr.Left + sr.Right) / 2, scy = (sr.Top + sr.Bottom) / 2;
        RoundFill(dc, new RECT(scx - S(7), scy - S(7), scx + S(7), scy + S(7)), RGB(232, 17, 35), S(3));

        SelectObject(dc, oldPen);
    }

    private static void Fill(IntPtr dc, RECT r, uint color)
    {
        var b = CreateSolidBrush(color);
        FillRect(dc, ref r, b);
        DeleteObject(b);
    }

    private void RoundFill(IntPtr dc, RECT r, uint color, int radius = -1)
    {
        if (radius < 0) radius = S(6);
        var b = CreateSolidBrush(color);
        var ob = SelectObject(dc, b);
        RoundRect(dc, r.Left, r.Top, r.Right + 1, r.Bottom + 1, radius * 2, radius * 2);
        SelectObject(dc, ob);
        DeleteObject(b);
    }

    public override void Dispose()
    {
        if (Handle != IntPtr.Zero) KillTimer(Handle, new IntPtr(TickTimerId));
        base.Dispose();
    }

    protected override void OnDestroyed()
    {
        if (_font != IntPtr.Zero) { DeleteObject(_font); _font = IntPtr.Zero; }
    }
}
