namespace SnipRecord.ExtensionApp.Services;

/// <summary>
/// Handles sniprecord:// activations, e.g. the response Snipping Tool sends back to
/// the redirect-uri after an ms-screenclip request. TODO: parse code/reason/file
/// token, save the image, add it to RecentStore.
/// </summary>
public static class CaptureResponseHandler
{
    /// <summary>Called with no UI (protocol launch when no widget process is running) or from the plugin.</summary>
    public static Task HandleHeadlessAsync(Uri uri)
    {
        System.Diagnostics.Debug.WriteLine($"[SnipRecord] protocol activation: {uri}");
        return Task.CompletedTask;
    }
}
