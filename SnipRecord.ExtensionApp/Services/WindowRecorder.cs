namespace SnipRecord.ExtensionApp.Services;

/// <summary>
/// Rectangle / window recording via Windows.Graphics.Capture + MediaFoundation
/// (graphicsCaptureWithoutBorder / graphicsCaptureProgrammatic). TODO.
/// </summary>
public sealed class WindowRecorder : IAsyncDisposable
{
    public bool IsRecording { get; private set; }

    public event EventHandler<string>? Completed;

    public Task StartWindowAsync(IntPtr hwnd, string outputPath, bool microphone)
    {
        IsRecording = true;
        return Task.CompletedTask;
    }

    public Task StartRectangleAsync(Windows.Graphics.RectInt32 area, string outputPath, bool microphone)
    {
        IsRecording = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRecording = false;
        Completed?.Invoke(this, string.Empty);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
