using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SnipRecord.ExtensionApp.Services.Hotkeys;

public enum HotkeyRegistrationStatus { Registered, Disabled, InUse, Duplicate, Invalid, Failed }

public sealed record HotkeyRegistration(HotkeyAction Action, Hotkey Hotkey, HotkeyRegistrationStatus Status, int ErrorCode = 0)
{
    public bool IsRegistered => Status == HotkeyRegistrationStatus.Registered;

    public string StatusText => Status switch
    {
        HotkeyRegistrationStatus.Registered => "已启用",
        HotkeyRegistrationStatus.Disabled => "未设置",
        HotkeyRegistrationStatus.InUse => "已被占用",
        HotkeyRegistrationStatus.Duplicate => "与其他快捷键重复",
        HotkeyRegistrationStatus.Invalid => "无效（需包含 Ctrl、Alt 或 Win）",
        _ => $"注册失败（错误 {ErrorCode}）",
    };
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey (MOD_NOREPEAT) on a message-only window owned by a dedicated thread.
/// Call <see cref="Start"/> on the UI thread so <see cref="HotkeyPressed"/> is raised there.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private static readonly Lazy<GlobalHotkeyService> _shared = new(() => new GlobalHotkeyService());
    public static GlobalHotkeyService Shared => _shared.Value;

    private const uint WM_HOTKEY = 0x0312, WM_APP_INVOKE = 0x8001, MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly HashSet<int> _registeredIds = new();
    private SynchronizationContext? _context;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private HotkeyBindings _bindings = new();
    private int _suspendCount;
    private bool _disposed;

    public event EventHandler<HotkeyAction>? HotkeyPressed;

    /// <summary>Result of the last <see cref="RegisterAsync"/>.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> Registrations { get; private set; } =
        new Dictionary<HotkeyAction, HotkeyRegistration>();

    public bool IsSuspended { get { lock (_gate) return _suspendCount > 0; } }

    /// <summary>Captures the current SynchronizationContext (call on the UI thread) and starts the hotkey thread.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _context = SynchronizationContext.Current ?? _context;
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => ThreadMain(ready)) { IsBackground = true, Name = "SnipRecord.Hotkeys" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
        }
    }

    /// <summary>Unregisters everything, then registers <paramref name="bindings"/>. While suspended only probes (register + unregister).</summary>
    public Task<IReadOnlyDictionary<HotkeyAction, HotkeyRegistration>> RegisterAsync(HotkeyBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (_thread is null) Start();
        var copy = bindings.Clone();
        return InvokeAsync<IReadOnlyDictionary<HotkeyAction, HotkeyRegistration>>(() =>
        {
            lock (_gate) _bindings = copy;
            var result = ApplyOnThread(copy, keep: !IsSuspended);
            Registrations = result;
            return result;
        });
    }

    public Task UnregisterAllAsync()
    {
        if (_thread is null) return Task.CompletedTask;
        return InvokeAsync(() =>
        {
            lock (_gate) _bindings = new HotkeyBindings();
            UnregisterOnThread();
            Registrations = new Dictionary<HotkeyAction, HotkeyRegistration>();
            return true;
        });
    }

    /// <summary>Temporarily releases all hotkeys (e.g. while a recorder box has focus). Ref-counted.</summary>
    public void Suspend()
    {
        lock (_gate) { if (++_suspendCount != 1 || _thread is null) return; }
        _ = InvokeAsync(() => { UnregisterOnThread(); return true; });
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_suspendCount == 0) return;
            if (--_suspendCount != 0 || _thread is null) return;
        }
        _ = InvokeAsync(() =>
        {
            HotkeyBindings b;
            lock (_gate) { if (_suspendCount != 0) return true; b = _bindings; }
            Registrations = ApplyOnThread(b, keep: true);
            return true;
        });
    }

    public void Dispose()
    {
        Thread? t;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            t = _thread;
        }
        if (t is null) return;
        _queue.Enqueue(() => { UnregisterOnThread(); if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); PostQuitMessage(0); });
        PostThreadMessage(_threadId, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        t.Join(2000);
    }

    // ---------------- hotkey thread ----------------

    private Task<T> InvokeAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() => { try { tcs.SetResult(work()); } catch (Exception ex) { tcs.SetException(ex); } });
        if (!PostThreadMessage(_threadId, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero))
            tcs.TrySetException(new InvalidOperationException("Hotkey thread is not running."));
        return tcs.Task;
    }

    private void ThreadMain(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // create the message queue
        _hwnd = CreateWindowEx(0, "STATIC", "SnipRecord.Hotkeys", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_APP_INVOKE && msg.hwnd == IntPtr.Zero)
            {
                while (_queue.TryDequeue(out var a)) a();
                continue;
            }
            if (msg.message == WM_HOTKEY)
            {
                var id = (int)msg.wParam;
                if (id >= 1 && Enum.IsDefined(typeof(HotkeyAction), id - 1)) Raise((HotkeyAction)(id - 1));
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void Raise(HotkeyAction action)
    {
        var handler = HotkeyPressed;
        if (handler is null) return;
        void Fire(object? _) { try { handler(this, action); } catch (Exception ex) { Debug.WriteLine($"[SnipRecord] hotkey handler: {ex}"); } }
        if (_context is { } ctx) ctx.Post(Fire, null); else ThreadPool.QueueUserWorkItem(Fire);
    }

    private Dictionary<HotkeyAction, HotkeyRegistration> ApplyOnThread(HotkeyBindings bindings, bool keep)
    {
        UnregisterOnThread();
        var result = new Dictionary<HotkeyAction, HotkeyRegistration>();
        var seen = new HashSet<Hotkey>();
        foreach (var action in HotkeyBindings.AllActions)
        {
            var hk = bindings[action];
            HotkeyRegistration r;
            if (hk.IsEmpty) r = new(action, hk, HotkeyRegistrationStatus.Disabled);
            else if (!hk.IsValid) r = new(action, hk, HotkeyRegistrationStatus.Invalid);
            else if (!seen.Add(hk)) r = new(action, hk, HotkeyRegistrationStatus.Duplicate);
            else
            {
                var id = (int)action + 1;
                if (RegisterHotKey(_hwnd, id, (uint)hk.Modifiers | MOD_NOREPEAT, (uint)hk.VirtualKey))
                {
                    if (keep) _registeredIds.Add(id); else UnregisterHotKey(_hwnd, id);
                    r = new(action, hk, HotkeyRegistrationStatus.Registered);
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    r = new(action, hk, err == ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyRegistrationStatus.InUse : HotkeyRegistrationStatus.Failed, err);
                }
            }
            result[action] = r;
        }
        return result;
    }

    private void UnregisterOnThread()
    {
        foreach (var id in _registeredIds) UnregisterHotKey(_hwnd, id);
        _registeredIds.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string? title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
