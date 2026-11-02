using System.Diagnostics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace SnipRecord.ExtensionApp.Services.Recording;

/// <summary>
/// Windows.Graphics.Capture → D3D11 compose → MediaStreamSource → MediaTranscoder (H.264/AAC MP4).
/// Same shape as robmikh/SimpleRecorder, but samples are pushed through deferrals instead of blocking,
/// frames are capped at 30 fps, and window resizes are letterboxed into the fixed output size via D2D.
/// </summary>
internal sealed class CaptureEncoder : IAsyncDisposable
{
    public const int FrameRate = 30;
    private const int MaxWidth = 4096;   // H.264 encoder limits (level 5.1)
    private const int MaxHeight = 2304;
    private const int MaxQueuedFrames = 6;
    private static readonly long MinFrameInterval = TimeSpan.TicksPerSecond / FrameRate - 20_000; // ~31 ms

    private readonly GraphicsCaptureItem _item;
    private readonly RecordingClock _clock;
    private readonly SampleQueue _videoQueue = new();
    private readonly SampleQueue? _audioQueue;
    private readonly object _gpuLock = new();
    private readonly object _frameLock = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _lastSize;

    // Lazily created letterbox resources.
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ID3D11Texture2D? _composeTexture;
    private ID2D1Bitmap1? _composeTarget;
    private ID3D11Texture2D? _stageTexture;
    private ID2D1Bitmap1? _stageBitmap;
    private bool _d2dFailed;

    private IRandomAccessStream? _stream;
    private MediaStreamSource? _mss;
    private PrepareTranscodeResult? _prepared;
    private Task? _transcodeTask;
    private long _lastVideoTicks = long.MinValue;
    private volatile bool _stopping;
    private int _closedRaised;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? OutputPath { get; private set; }
    public long FramesWritten { get; private set; }

    /// <summary>The captured window was closed (raised once, on a capture thread).</summary>
    public event Action? SourceClosed;

    public CaptureEncoder(GraphicsCaptureItem item, RecordingClock clock, SampleQueue? audioQueue)
    {
        _item = item;
        _clock = clock;
        _audioQueue = audioQueue;
    }

    /// <summary>Creates the device, output file and transcode pipeline. Capture starts in <see cref="Start"/>.</summary>
    public async Task InitializeAsync(string folder, string fileName)
    {
        var size = _item.Size;
        (Width, Height) = ComputeOutputSize(size.Width, size.Height);

        _device = Direct3DInterop.CreateDevice();
        _context = _device.ImmediateContext;
        _winrtDevice = Direct3DInterop.CreateWinRTDevice(_device);

        Directory.CreateDirectory(folder);
        var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder);
        var file = await storageFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
        OutputPath = file.Path;
        _stream = await file.OpenAsync(FileAccessMode.ReadWrite);

        var videoDescriptor = new VideoStreamDescriptor(
            VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)Width, (uint)Height))
        { Name = "video" };

        var profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 },
            Video = VideoEncodingProperties.CreateH264(),
        };
        profile.Video.Width = (uint)Width;
        profile.Video.Height = (uint)Height;
        profile.Video.Bitrate = (uint)Math.Clamp((long)(Width * (double)Height * FrameRate * 0.15), 1_000_000, 60_000_000);
        profile.Video.FrameRate.Numerator = FrameRate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;

        if (_audioQueue != null)
        {
            var audioDescriptor = new AudioStreamDescriptor(
                AudioEncodingProperties.CreatePcm(WasapiSource.SampleRate, WasapiSource.Channels, 16))
            { Name = "audio" };
            profile.Audio = AudioEncodingProperties.CreateAac(WasapiSource.SampleRate, WasapiSource.Channels, 192_000);
            _mss = new MediaStreamSource(videoDescriptor, audioDescriptor);
        }
        else
        {
            profile.Audio = null;
            _mss = new MediaStreamSource(videoDescriptor);
        }
        _mss.BufferTime = TimeSpan.Zero;
        _mss.CanSeek = false;
        _mss.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        _mss.SampleRequested += OnSampleRequested;

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        _prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_mss, _stream, profile);
        if (!_prepared.CanTranscode)
            throw new InvalidOperationException("无法创建 H.264 编码器: " + _prepared.FailureReason);
    }

    /// <summary>Starts capture and encoding. Call <see cref="RecordingClock.Start"/> right before.</summary>
    public void Start()
    {
        if (_prepared == null || _winrtDevice == null) throw new InvalidOperationException("Not initialized");

        _lastSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _lastSize);
        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;
        _session = _pool.CreateCaptureSession(_item);

        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
                Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                _session.IsCursorCaptureEnabled = true;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        Direct3DInterop.TrySetBorderRequired(_session, false);

        _session.StartCapture();
        _transcodeTask = _prepared.TranscodeAsync().AsTask();
    }

    private static (int W, int H) ComputeOutputSize(int w, int h)
    {
        w = Math.Max(w, 2);
        h = Math.Max(h, 2);
        double scale = Math.Min(1.0, Math.Min((double)MaxWidth / w, (double)MaxHeight / h));
        int ow = (int)(w * scale) & ~1;
        int oh = (int)(h * scale) & ~1;
        return (Math.Max(ow, 64), Math.Max(oh, 64));
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        try
        {
            var descriptor = request.StreamDescriptor;
            bool isAudio = descriptor is AudioStreamDescriptor || descriptor?.Name == "audio";
            if (isAudio)
            {
                if (_audioQueue != null) _audioQueue.OnRequested(request);
                else request.Sample = null;
            }
            else
            {
                _videoQueue.OnRequested(request);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            try { request.Sample = null; } catch { }
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => RaiseClosed();

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) SourceClosed?.Invoke();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_stopping) return;
        lock (_frameLock) HandleFrame(sender);
    }

    private void HandleFrame(Direct3D11CaptureFramePool sender)
    {
        if (_stopping) return;
        SizeInt32 newSize = default;
        bool resized = false;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            var contentSize = frame.ContentSize;
            if (contentSize.Width != _lastSize.Width || contentSize.Height != _lastSize.Height)
            {
                resized = true;
                newSize = contentSize;
            }

            if (_clock.IsPaused || _stopping) return;
            var ts = _clock.ToMediaTime(frame.SystemRelativeTime);
            long ticks = Math.Max(0, ts.Ticks);
            if (_lastVideoTicks != long.MinValue && ticks - _lastVideoTicks < MinFrameInterval) return; // 30 fps cap
            if (_videoQueue.Count >= MaxQueuedFrames) return; // encoder is behind; drop

            var surface = Compose(frame.Surface, contentSize);
            if (surface == null) return;
            var sample = MediaStreamSample.CreateFromDirect3D11Surface(surface, TimeSpan.FromTicks(ticks));
            _lastVideoTicks = ticks;
            FramesWritten++;
            _videoQueue.Push(sample);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            if (resized && !_stopping && newSize.Width > 0 && newSize.Height > 0)
            {
                try
                {
                    _lastSize = newSize;
                    sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, newSize);
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
    }

    /// <summary>Copies (or letterbox-scales) the frame into a fresh texture owned by the sample.</summary>
    private IDirect3DSurface? Compose(IDirect3DSurface frameSurface, SizeInt32 contentSize)
    {
        var device = _device!;
        var ctx = _context!;
        lock (_gpuLock)
        {
            using var src = Direct3DInterop.GetTexture(frameSurface);
            var srcDesc = src.Description;
            int cw = Math.Clamp(contentSize.Width, 1, (int)srcDesc.Width);
            int ch = Math.Clamp(contentSize.Height, 1, (int)srcDesc.Height);

            using var output = device.CreateTexture2D(NewTargetDescription(Width, Height));

            if (cw >= Width && cw <= Width + 1 && ch >= Height && ch <= Height + 1)
            {
                // Same size (an odd source dimension loses one column/row to the even output size).
                ctx.CopySubresourceRegion(output, 0, 0, 0, 0, src, 0, new Box(0, 0, 0, Width, Height, 1));
            }
            else if (!_d2dFailed && TryLetterbox(src, srcDesc, cw, ch))
            {
                ctx.CopyResource(output, _composeTexture!);
            }
            else
            {
                // Fallback: black background + top-left crop.
                using (var rtv = device.CreateRenderTargetView(output))
                    ctx.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));
                int w = Math.Min(cw, Width), h = Math.Min(ch, Height);
                ctx.CopySubresourceRegion(output, 0, 0, 0, 0, src, 0, new Box(0, 0, 0, w, h, 1));
            }

            return Direct3DInterop.CreateWinRTSurface(output);
        }
    }

    private static Texture2DDescription NewTargetDescription(int w, int h) => new()
    {
        Width = (uint)w,
        Height = (uint)h,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    };

    private bool TryLetterbox(ID3D11Texture2D src, Texture2DDescription srcDesc, int cw, int ch)
    {
        try
        {
            var device = _device!;
            var ctx = _context!;
            if (_d2dContext == null)
            {
                _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
                _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

                _composeTexture = device.CreateTexture2D(NewTargetDescription(Width, Height));
                using var composeSurface = _composeTexture.QueryInterface<IDXGISurface>();
                _composeTarget = _d2dContext.CreateBitmapFromDxgiSurface(composeSurface, new BitmapProperties1(
                    new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96,
                    BitmapOptions.Target | BitmapOptions.CannotDraw));
            }

            // Copy the frame into our own shader-resource texture so D2D can sample it regardless of how
            // the capture pool's textures were created.
            if (_stageTexture == null || _stageTexture.Description.Width != srcDesc.Width
                || _stageTexture.Description.Height != srcDesc.Height)
            {
                _stageBitmap?.Dispose();
                _stageTexture?.Dispose();
                _stageTexture = device.CreateTexture2D(NewTargetDescription((int)srcDesc.Width, (int)srcDesc.Height));
                using var stageSurface = _stageTexture.QueryInterface<IDXGISurface>();
                _stageBitmap = _d2dContext.CreateBitmapFromDxgiSurface(stageSurface, new BitmapProperties1(
                    new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
            }
            ctx.CopySubresourceRegion(_stageTexture, 0, 0, 0, 0, src, 0, new Box(0, 0, 0, cw, ch, 1));

            float scale = Math.Min((float)Width / cw, (float)Height / ch);
            float dw = cw * scale, dh = ch * scale;
            float dx = (Width - dw) / 2f, dy = (Height - dh) / 2f;

            _d2dContext.Target = _composeTarget;
            _d2dContext.BeginDraw();
            _d2dContext.Clear(new Color4(0f, 0f, 0f, 1f));
            _d2dContext.DrawBitmap(_stageBitmap!, new Vortice.RawRectF(dx, dy, dx + dw, dy + dh), 1f,
                BitmapInterpolationMode.Linear, new Vortice.RawRectF(0, 0, cw, ch));
            _d2dContext.EndDraw();
            _d2dContext.Target = null;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _d2dFailed = true;
            return false;
        }
    }

    /// <summary>Stops capture, ends the video stream and waits for the MP4 to be finalized.</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        try
        {
            if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
            _item.Closed -= OnItemClosed;
        }
        catch { }
        lock (_frameLock) // wait for an in-flight frame before tearing the pool down
        {
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            _session = null;
            _pool = null;
        }

        _videoQueue.End();
        try
        {
            if (_transcodeTask != null) await _transcodeTask.ConfigureAwait(false);
        }
        finally
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopping) { try { await StopAsync().ConfigureAwait(false); } catch { } }
        lock (_gpuLock)
        {
            _stageBitmap?.Dispose(); _stageBitmap = null;
            _stageTexture?.Dispose(); _stageTexture = null;
            _composeTarget?.Dispose(); _composeTarget = null;
            _composeTexture?.Dispose(); _composeTexture = null;
            _d2dContext?.Dispose(); _d2dContext = null;
            _d2dDevice?.Dispose(); _d2dDevice = null;
            _d2dFactory?.Dispose(); _d2dFactory = null;
        }
        try { (_winrtDevice as IDisposable)?.Dispose(); } catch { }
        _winrtDevice = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }
}
