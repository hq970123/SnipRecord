namespace SnipRecord.ExtensionApp.Services;

public enum SnipMode
{
    Rectangle,
    Window,
    FullScreen,
    FreeForm,
    Recording,
}

/// <summary>Launches Snipping Tool (ms-screenclip:) with a sniprecord:// redirect. TODO.</summary>
public static class SnippingToolLauncher
{
    public const string RedirectScheme = "sniprecord";

    public static Task<bool> LaunchAsync(SnipMode mode)
    {
        // TODO: build the ms-screenclip URI and call Windows.System.Launcher.LaunchUriAsync.
        return Task.FromResult(false);
    }
}
