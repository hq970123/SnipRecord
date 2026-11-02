using System.Diagnostics;
using Microsoft.UI.Xaml;
using SnipRecord.ExtensionApp.Services;
using SnipRecord.ExtensionApp.Views;
using WidBar.SDK;

namespace SnipRecord.ExtensionApp;

// 截图录屏: taskbar buttons for a screenshot (via Snipping Tool), rectangle/window
// recording and a screen color picker. Scaffold only — the behaviour is TODO.
public sealed class SnipRecordPlugin :
    WidgetPluginBase,
    IConfigurableWidgetPlugin
{
    public const string PluginId = "com.hq970123.sniprecord";

    public override string Id => PluginId;
    public override string Name => "截图录屏";
    public override string Version => "1.0.0";

    public override int PreviewLogicalWidth => _settings.PreviewWidth;
    public override int FlyoutWidth => 360;
    public override int FlyoutHeight => 420;
    public override WidgetFlyoutBackdrop FlyoutBackdrop => WidgetFlyoutBackdrop.Acrylic;

    private AppSettings _settings = new();
    private RecentStore? _recent;
    private PreviewView? _preview;
    private FlyoutView? _flyout;

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _settings = AppSettings.FromJson(context.SettingsJson);
        await base.InitializeAsync(context);

        _recent = new RecentStore(context.DataDirectory);
        ActivationHub.ProtocolActivated += OnProtocolActivated;
    }

    public override UIElement? CreatePreviewContent()
    {
        _preview = new PreviewView();
        _preview.ApplySettings(_settings);
        return _preview;
    }

    public override UIElement? CreateFlyoutContent()
    {
        _flyout = new FlyoutView(_recent);
        return _flyout;
    }

    public UIElement? CreateSettingsContent(IWidgetSettingsContext context) => new SettingsView(context);

    public override void OnSettingsDraftChanged(string settingsJson)
    {
        _settings = AppSettings.FromJson(settingsJson);
        _preview?.ApplySettings(_settings);
    }

    /// <summary>A sniprecord:// URI redirected to this process (UI thread).</summary>
    private async void OnProtocolActivated(Uri uri)
    {
        try
        {
            // TODO: handle in-process (update the flyout's recent list, copy to clipboard, ...).
            await CaptureResponseHandler.HandleHeadlessAsync(uri);
            _flyout?.Refresh();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Protocol activation failed: {ex}");
        }
    }

    public override ValueTask DisposeAsync()
    {
        ActivationHub.ProtocolActivated -= OnProtocolActivated;
        return base.DisposeAsync();
    }
}
