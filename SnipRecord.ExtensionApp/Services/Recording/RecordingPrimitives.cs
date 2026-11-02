using System.Diagnostics;
using Windows.Media.Core;

namespace SnipRecord.ExtensionApp.Services.Recording;

/// <summary>
/// Media clock shared by video and audio. Based on QPC, the same time base as
/// <c>Direct3D11CaptureFrame.SystemRelativeTime</c>. Pausing freezes media time; the paused span is
/// subtracted from every later timestamp so the file has no gap.
/// </summary>
internal sealed class RecordingClock
{
    private readonly object _lock = new();
    private long _startTicks;      // 100 ns, QPC base
    private long _pausedTicks;     // total paused duration, 100 ns
    private long _pauseStartTicks;
    private bool _paused;
    private bool _started;

    public static long NowTicks()
    {
        long qpc = Stopwatch.GetTimestamp();
        long f = Stopwatch.Frequency;
        return (long)((Int128)qpc * TimeSpan.TicksPerSecond / f);
    }

    public void Start()
    {
        lock (_lock) { _startTicks = NowTicks(); _pausedTicks = 0; _paused = false; _started = true; }
    }

    public bool IsPaused { get { lock (_lock) return _paused; } }

    public bool Pause()
    {
        lock (_lock)
        {
            if (_paused || !_started) return false;
            _paused = true;
            _pauseStartTicks = NowTicks();
            return true;
        }
    }

    public bool Resume()
    {
        lock (_lock)
        {
            if (!_paused) return false;
            _pausedTicks += NowTicks() - _pauseStartTicks;
            _paused = false;
            return true;
        }
    }

    /// <summary>Current media position (frozen while paused).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                if (!_started) return TimeSpan.Zero;
                long now = _paused ? _pauseStartTicks : NowTicks();
                return TimeSpan.FromTicks(Math.Max(0, now - _startTicks - _pausedTicks));
            }
        }
    }

    /// <summary>Maps a QPC-based system-relative time to media time (may be negative).</summary>
    public TimeSpan ToMediaTime(TimeSpan systemRelativeTime)
    {
        lock (_lock) return TimeSpan.FromTicks(systemRelativeTime.Ticks - _startTicks - _pausedTicks);
    }
}

/// <summary>
/// Hands <see cref="MediaStreamSample"/>s to a MediaStreamSource. Requests that arrive before a sample is
/// ready are parked with a deferral and completed from the producer thread, so no MF thread ever blocks.
/// </summary>
internal sealed class SampleQueue
{
    private readonly object _lock = new();
    private readonly Queue<MediaStreamSample> _queue = new();
    private MediaStreamSourceSampleRequest? _pending;
    private MediaStreamSourceSampleRequestDeferral? _deferral;
    private bool _ended;

    public int Count { get { lock (_lock) return _queue.Count; } }

    public bool IsEnded { get { lock (_lock) return _ended; } }

    public void Push(MediaStreamSample sample)
    {
        MediaStreamSourceSampleRequest? req;
        MediaStreamSourceSampleRequestDeferral? def;
        lock (_lock)
        {
            if (_ended) return;
            if (_pending == null) { _queue.Enqueue(sample); return; }
            req = _pending; def = _deferral;
            _pending = null; _deferral = null;
        }
        Complete(req, def, sample);
    }

    public void OnRequested(MediaStreamSourceSampleRequest request)
    {
        lock (_lock)
        {
            if (_queue.Count > 0) { request.Sample = _queue.Dequeue(); return; }
            if (_ended) { request.Sample = null; return; } // end of stream
            if (_pending != null)
            {
                // Should not happen (MSS asks one sample at a time per stream); end the stale one.
                try { _pending.Sample = null; _deferral?.Complete(); } catch { }
            }
            _pending = request;
            _deferral = request.GetDeferral();
        }
    }

    /// <summary>No more samples; queued ones are still delivered, then end-of-stream.</summary>
    public void End()
    {
        MediaStreamSourceSampleRequest? req = null;
        MediaStreamSourceSampleRequestDeferral? def = null;
        lock (_lock)
        {
            if (_ended) return;
            _ended = true;
            if (_pending != null && _queue.Count == 0)
            {
                req = _pending; def = _deferral;
                _pending = null; _deferral = null;
            }
        }
        if (req != null) Complete(req, def, null);
    }

    private static void Complete(MediaStreamSourceSampleRequest req, MediaStreamSourceSampleRequestDeferral? def,
        MediaStreamSample? sample)
    {
        try
        {
            req.Sample = sample;
            def?.Complete();
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
}

/// <summary>Thread-safe ring of 16-bit PCM bytes; overflow drops the oldest data.</summary>
internal sealed class PcmRingBuffer
{
    private readonly object _lock = new();
    private readonly byte[] _buf;
    private int _head; // read position
    private int _count;
    private readonly int _align;

    public PcmRingBuffer(int capacityBytes, int blockAlign)
    {
        _align = blockAlign;
        _buf = new byte[capacityBytes - capacityBytes % blockAlign];
    }

    public int Available { get { lock (_lock) return _count; } }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (data.Length >= _buf.Length) data = data[^_buf.Length..];
            int overflow = _count + data.Length - _buf.Length;
            if (overflow > 0) SkipLocked(RoundUp(overflow));
            int tail = (_head + _count) % _buf.Length;
            int first = Math.Min(data.Length, _buf.Length - tail);
            data[..first].CopyTo(_buf.AsSpan(tail));
            data[first..].CopyTo(_buf.AsSpan(0));
            _count += data.Length;
        }
    }

    /// <summary>Fills <paramref name="dst"/>; returns bytes of real data, the rest is zeroed (silence).</summary>
    public int Read(Span<byte> dst)
    {
        lock (_lock)
        {
            int n = Math.Min(dst.Length, _count);
            n -= n % _align;
            int first = Math.Min(n, _buf.Length - _head);
            _buf.AsSpan(_head, first).CopyTo(dst);
            _buf.AsSpan(0, n - first).CopyTo(dst[first..]);
            _head = (_head + n) % _buf.Length;
            _count -= n;
            dst[n..].Clear();
            return n;
        }
    }

    public void Skip(int bytes)
    {
        lock (_lock) SkipLocked(RoundUp(bytes));
    }

    public void Clear()
    {
        lock (_lock) { _head = 0; _count = 0; }
    }

    private void SkipLocked(int bytes)
    {
        bytes = Math.Min(bytes, _count);
        _head = (_head + bytes) % _buf.Length;
        _count -= bytes;
    }

    private int RoundUp(int bytes) => (bytes + _align - 1) / _align * _align;
}
