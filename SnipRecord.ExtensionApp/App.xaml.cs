using WidBar.SDK;
using WidBar.SDK.Hosting;

namespace SnipRecord.ExtensionApp;

// Entry point of the plugin process. The SDK base class talks to WidBar, renders
// the taskbar preview and hosts the flyout/settings windows. We only hand it our
// plugin (one instance per placed widget).
public partial class App : WidgetHostApplication
{
    public App()
    {
        InitializeComponent();
        // Our own windows (annotation editor) can be the last ones open; closing them must not end the
        // process — WidBar.SDK exits explicitly when the host asks it to shut down.
        DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override IWidgetPlugin CreatePlugin() => new SnipRecordPlugin();
}
