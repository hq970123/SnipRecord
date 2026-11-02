using Microsoft.UI.Dispatching;

namespace SnipRecord.ExtensionApp;

/// <summary>
/// Hands protocol activations (sniprecord://...) that were redirected to this process
/// over to the plugin on the UI thread. If nobody is listening yet, the latest URI is
/// kept and delivered to the first subscriber.
/// </summary>
public static class ActivationHub
{
    private static readonly object Gate = new();
    private static Action<Uri>? _handlers;
    private static Uri? _pending;

    /// <summary>Set by Program once the XAML dispatcher exists.</summary>
    internal static DispatcherQueue? UiQueue { get; set; }

    /// <summary>Raised on the UI thread.</summary>
    public static event Action<Uri>? ProtocolActivated
    {
        add
        {
            Uri? pending;
            lock (Gate)
            {
                _handlers += value;
                pending = _pending;
                _pending = null;
            }
            if (pending is not null && value is not null) Dispatch(value, pending);
        }
        remove
        {
            lock (Gate) _handlers -= value;
        }
    }

    internal static void Raise(Uri uri)
    {
        Action<Uri>? handlers;
        lock (Gate)
        {
            handlers = _handlers;
            if (handlers is null) _pending = uri;
        }
        if (handlers is not null) Dispatch(handlers, uri);
    }

    private static void Dispatch(Action<Uri> handler, Uri uri)
    {
        var queue = UiQueue;
        if (queue is null || queue.HasThreadAccess) handler(uri);
        else queue.TryEnqueue(() => handler(uri));
    }
}
