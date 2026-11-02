using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;

namespace SnipRecord.ExtensionApp.Services.Recording;

// ---------------------------------------------------------------------------------------------
// WASAPI COM interop (classic [ComImport]; the app is not trimmed / AOT, built-in COM is enabled).
// ---------------------------------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    public static WaveFormatEx Pcm16(uint rate, ushort channels) => new()
    {
        wFormatTag = 1, // WAVE_FORMAT_PCM
        nChannels = channels,
        nSamplesPerSec = rate,
        wBitsPerSample = 16,
        nBlockAlign = (ushort)(channels * 2),
        nAvgBytesPerSec = rate * channels * 2u,
        cbSize = 0,
    };
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity,
        [In] ref WaveFormatEx format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, [In] ref WaveFormatEx format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition,
        out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

/// <summary>Marker: the completion handler must be agile (it is called on an MTA worker).</summary>
[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject { }

[ComVisible(true)]
internal sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IAudioClient> Task => _tcs.Task;

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            int hr = operation.GetActivateResult(out int activateHr, out object? iface);
            if (hr < 0) { _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("GetActivateResult", hr)); return 0; }
            if (activateHr < 0 || iface == null)
            {
                _tcs.TrySetException(Marshal.GetExceptionForHR(activateHr) ?? new COMException("ActivateAudioInterfaceAsync", activateHr));
                return 0;
            }
            _tcs.TrySetResult((IAudioClient)iface);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
        return 0;
    }
}

internal static class AudioInterop
{
    public const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private const ushort VT_BLOB = 65;
    private const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    private const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>Process loopback needs Windows build 20348+ (Windows 11 / Server 2022).</summary>
    public static bool IsProcessLoopbackSupported => Environment.OSVersion.Version.Build >= 20348;

    public static async Task<IAudioClient> ActivateProcessLoopbackAsync(uint processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { int ActivationType; { DWORD TargetProcessId; int Mode; } } = 12 bytes
        IntPtr blob = Marshal.AllocHGlobal(12);
        // PROPVARIANT (24 bytes on 64-bit): vt @0, BLOB { ULONG cbSize @8; BYTE* pBlobData @16 }
        IntPtr prop = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(blob, 0, AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK);
            Marshal.WriteInt32(blob, 4, unchecked((int)processId));
            Marshal.WriteInt32(blob, 8, PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(prop, i, 0);
            Marshal.WriteInt16(prop, 0, unchecked((short)VT_BLOB));
            Marshal.WriteInt32(prop, 8, 12);
            Marshal.WriteIntPtr(prop, IntPtr.Size == 8 ? 16 : 12, blob);

            var handler = new ActivateCompletionHandler();
            var iid = IID_IAudioClient;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, prop,
                handler, out var op));
            var client = await handler.Task.ConfigureAwait(false);
            GC.KeepAlive(op);
            return client;
        }
        finally
        {
            Marshal.FreeHGlobal(prop);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static async Task<IAudioClient> ActivateDefaultMicrophoneAsync()
    {
        string id = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(Windows.Media.Devices.AudioDeviceRole.Default);
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("没有可用的麦克风");
        var handler = new ActivateCompletionHandler();
        var iid = IID_IAudioClient;
        Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(id, ref iid, IntPtr.Zero, handler, out var op));
        var client = await handler.Task.ConfigureAwait(false);
        GC.KeepAlive(op);
        return client;
    }
}

/// <summary>Event-driven WASAPI capture of 48 kHz / stereo / 16-bit PCM into a <see cref="PcmRingBuffer"/>.</summary>
internal sealed class WasapiSource : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BlockAlign = 4;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AutoResetEvent _event = new(false);
    private readonly PcmRingBuffer _sink;
    private readonly Thread _thread;
    private volatile bool _stop;

    public string Name { get; }
    public bool Faulted { get; private set; }

    private WasapiSource(string name, IAudioClient client, bool loopback, PcmRingBuffer sink)
    {
        Name = name;
        _client = client;
        _sink = sink;

        var fmt = WaveFormatEx.Pcm16(SampleRate, Channels);
        uint flags = AudioInterop.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        long bufferHns;
        if (loopback)
        {
            flags |= AudioInterop.AUDCLNT_STREAMFLAGS_LOOPBACK; // as in the ApplicationLoopback sample
            bufferHns = 200_000; // 20 ms
        }
        else
        {
            flags |= AudioInterop.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AudioInterop.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            bufferHns = 1_000_000; // 100 ms
        }
        Marshal.ThrowExceptionForHR(_client.Initialize(AudioInterop.AUDCLNT_SHAREMODE_SHARED, flags, bufferHns, 0, ref fmt, IntPtr.Zero));
        var iid = AudioInterop.IID_IAudioCaptureClient;
        Marshal.ThrowExceptionForHR(_client.GetService(ref iid, out object svc));
        _capture = (IAudioCaptureClient)svc;
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()));

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "SnipRecord audio " + name };
    }

    public static async Task<WasapiSource> CreateProcessLoopbackAsync(uint processId, PcmRingBuffer sink)
    {
        var client = await AudioInterop.ActivateProcessLoopbackAsync(processId).ConfigureAwait(false);
        return new WasapiSource("loopback", client, loopback: true, sink);
    }

    public static async Task<WasapiSource> CreateMicrophoneAsync(PcmRingBuffer sink)
    {
        var client = await AudioInterop.ActivateDefaultMicrophoneAsync().ConfigureAwait(false);
        return new WasapiSource("mic", client, loopback: false, sink);
    }

    public void Start()
    {
        Marshal.ThrowExceptionForHR(_client.Start());
        _thread.Start();
    }

    private void CaptureLoop()
    {
        byte[] buf = new byte[SampleRate * BlockAlign / 10];
        try
        {
            while (!_stop)
            {
                _event.WaitOne(100);
                while (!_stop)
                {
                    int hr = _capture.GetNextPacketSize(out uint packet);
                    if (hr < 0) { Faulted = true; return; }
                    if (packet == 0) break;

                    hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint bufFlags, out _, out _);
                    if (hr < 0) { Faulted = true; return; }
                    int bytes = checked((int)frames * BlockAlign);
                    if (buf.Length < bytes) buf = new byte[bytes];
                    if ((bufFlags & AudioInterop.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                        Array.Clear(buf, 0, bytes);
                    else
                        Marshal.Copy(data, buf, 0, bytes);
                    _capture.ReleaseBuffer(frames);
                    _sink.Write(buf.AsSpan(0, bytes));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Faulted = true;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _event.Set();
        if (_thread.IsAlive) _thread.Join(1000);
        try { _client.Stop(); } catch { }
        try { Marshal.FinalReleaseComObject(_capture); } catch { }
        try { Marshal.FinalReleaseComObject(_client); } catch { }
        _event.Dispose();
    }
}

/// <summary>
/// Mixes window-process loopback + optional microphone into one PCM stream paced by the media clock,
/// padding with silence on underrun, so audio stays continuous and aligned with the video timestamps.
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    private const int Rate = WasapiSource.SampleRate;
    private const int Align = WasapiSource.BlockAlign;
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(120);
    private const int MaxBacklogBytes = Rate * Align * 400 / 1000; // drop beyond ~400 ms of queued input

    private readonly RecordingClock _clock;
    private readonly SampleQueue _queue;
    private readonly List<(WasapiSource Source, PcmRingBuffer Buffer)> _inputs = new();
    private Thread? _thread;
    private volatile bool _stop;
    private long _framesProduced;
    private byte[] _mix = Array.Empty<byte>();
    private byte[] _tmp = Array.Empty<byte>();

    public AudioMixer(RecordingClock clock, SampleQueue queue)
    {
        _clock = clock;
        _queue = queue;
    }

    public int InputCount => _inputs.Count;

    public static PcmRingBuffer NewBuffer() => new(Rate * Align * 3, Align);

    public void AddInput(WasapiSource source, PcmRingBuffer buffer) => _inputs.Add((source, buffer));

    public void Start()
    {
        foreach (var (s, b) in _inputs) { b.Clear(); s.Start(); }
        _thread = new Thread(Loop) { IsBackground = true, Name = "SnipRecord audio mixer" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            while (!_stop)
            {
                Thread.Sleep(10);
                Pump(final: false);
            }
            Pump(final: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _queue.End();
        }
    }

    private void Pump(bool final)
    {
        if (_clock.IsPaused)
        {
            foreach (var (_, b) in _inputs) b.Clear();
            return;
        }

        var media = _clock.Elapsed - (final ? TimeSpan.Zero : Latency);
        if (media <= TimeSpan.Zero) return;
        long target = media.Ticks * Rate / TimeSpan.TicksPerSecond;
        long frames = Math.Min(target - _framesProduced, Rate); // at most 1 s per sample
        if (frames <= 0) return;

        int bytes = (int)frames * Align;
        if (_mix.Length < bytes) { _mix = new byte[bytes]; _tmp = new byte[bytes]; }
        var mix = _mix.AsSpan(0, bytes);
        mix.Clear();
        var mix16 = MemoryMarshal.Cast<byte, short>(mix);

        foreach (var (_, buffer) in _inputs)
        {
            // Keep latency bounded if a source delivers faster than real time or we fell behind.
            int excess = buffer.Available - bytes - MaxBacklogBytes;
            if (excess > 0) buffer.Skip(excess);

            var tmp = _tmp.AsSpan(0, bytes);
            int got = buffer.Read(tmp);
            if (got == 0) continue;
            var src16 = MemoryMarshal.Cast<byte, short>(tmp);
            for (int i = 0; i < mix16.Length; i++)
            {
                int v = mix16[i] + src16[i];
                mix16[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
        }

        var data = mix.ToArray();
        var sample = MediaStreamSample.CreateFromBuffer(data.AsBuffer(),
            TimeSpan.FromTicks(_framesProduced * TimeSpan.TicksPerSecond / Rate));
        sample.Duration = TimeSpan.FromTicks(frames * TimeSpan.TicksPerSecond / Rate);
        _queue.Push(sample);
        _framesProduced += frames;
    }

    /// <summary>Flushes up to the current media time, ends the audio stream and stops capture.</summary>
    public void Stop()
    {
        _stop = true;
        if (_thread != null && _thread.IsAlive) _thread.Join(2000);
        else _queue.End();
        foreach (var (s, _) in _inputs) s.Dispose();
        _inputs.Clear();
    }

    public void Dispose() => Stop();
}
