using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SnipRecord.ExtensionApp.Services;
using Windows.ApplicationModel.Activation;
using WinRT;

namespace SnipRecord.ExtensionApp;

/// <summary>
/// Custom Main (DISABLE_XAML_GENERATED_MAIN) so activations can be routed before XAML starts.
///
/// * WidBar launch: the exe is started with --widbar-pipe=&lt;name&gt;. We register the
///   "SnipRecord.Host" key, listen for redirected activations, and start the XAML app.
/// * Protocol launch (sniprecord://..., e.g. Snipping Tool's ms-screenclip response):
///   if a widget process owns the key, redirect the activation there and exit;
///   otherwise handle the URI headless (save to the recent list etc.) and exit.
/// </summary>
public static class Program
{
    public const string InstanceKey = "SnipRecord.Host";

    [STAThread]
    public static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        AppActivationArguments? activation = null;
        try
        {
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetActivatedEventArgs failed: {ex}");
        }

        if (activation is not null && activation.Kind == ExtendedActivationKind.Protocol)
        {
            var owner = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (!owner.IsCurrent)
            {
                RedirectActivationTo(activation, owner);
                return 0;
            }

            // Nobody else is running: handle it here without any UI, then quit.
            var uri = (activation.Data as IProtocolActivatedEventArgs)?.Uri;
            if (uri is not null)
            {
                try
                {
                    Task.Run(() => CaptureResponseHandler.HandleHeadlessAsync(uri)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Headless protocol handling failed: {ex}");
                }
            }
            try { owner.UnregisterKey(); } catch { }
            return 0;
        }

        // Normal start (WidBar host launch, or a plain launch that shows the SDK's info window).
        try
        {
            var main = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (main.IsCurrent)
            {
                main.Activated += OnRedirectedActivation;
            }
            // TODO: if WidBar runs several widget processes, only the first owns the key and
            // receives protocol activations. Revisit once we know WidBar's process model.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Instance key registration failed: {ex}");
        }

        Application.Start(_ =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            var context = new DispatcherQueueSynchronizationContext(queue);
            SynchronizationContext.SetSynchronizationContext(context);
            ActivationHub.UiQueue = queue;
            new App();
        });
        return 0;
    }

    /// <summary>Runs on a background thread in the key-owning (widget) process.</summary>
    private static void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        if (e.Kind != ExtendedActivationKind.Protocol) return;
        if ((e.Data as IProtocolActivatedEventArgs)?.Uri is { } uri)
        {
            ActivationHub.Raise(uri);
        }
    }

    // ---- Redirect without blocking the STA (pattern from the Windows App SDK docs) ----

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance target)
    {
        var redirectEvent = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            try
            {
                target.RedirectActivationToAsync(args).AsTask().Wait();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RedirectActivationToAsync failed: {ex}");
            }
            SetEvent(redirectEvent);
        });

        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, new[] { redirectEvent }, out _);
        CloseHandle(redirectEvent);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);
}
