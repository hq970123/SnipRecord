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
    }

    protected override IWidgetPlugin CreatePlugin() => new SnipRecordPlugin();
}
