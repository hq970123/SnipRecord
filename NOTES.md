# NOTES — SnipRecord scaffold (step 1)

Copied from the working LyricsPlayer widget and renamed. Nothing is functional yet; every
file is meant to compile so later steps only fill in behaviour.

## File tree
```
SnipRecord.sln                       ExtensionApp {4EF42E08-D579-4DD8-A7A1-92F5B55EF934}, Package {12386AF8-2918-404F-B104-FA02D8A9D79D}; x64/ARM64
.github/workflows/build.yml          x64+ARM64 MSIX, self-signed (CN=hq970123), zip SnipRecord_<arch>.zip, GitHub release; version 1.0.<run>.0
installer/Install.bat, Install.ps1   trust SnipRecord.cer + Add-AppxPackage (with Dependencies/)
README.md, PRIVACY.md, NOTES.md
SnipRecord (Package)/
  SnipRecord (Package).wapproj       Public\plugin.json linked from ExtensionApp\obj\widbar\plugin.json
  Package.appxmanifest               Identity hq970123.SnipRecord / CN=hq970123 / 1.0.0.0
  Images/*.png                       PLACEHOLDERS copied from LyricsPlayer
SnipRecord.ExtensionApp/
  SnipRecord.ExtensionApp.csproj     net10.0-windows10.0.19041.0, WinAppSDK 2.3.1, WidBar.SDK 2.0.5, DISABLE_XAML_GENERATED_MAIN
  app.manifest                       PerMonitorV2
  Properties/PublishProfiles/win-x64.pubxml, win-arm64.pubxml   SelfContained
  App.xaml / App.xaml.cs             App : WidgetHostApplication -> new SnipRecordPlugin()
  Program.cs                         custom Main, activation routing
  ActivationHub.cs                   static event ProtocolActivated(Uri) on UI thread
  SnipRecordPlugin.cs                WidgetPluginBase, IConfigurableWidgetPlugin
  Views/PreviewView.cs               Grid  (taskbar)        — TODO buttons
  Views/FlyoutView.cs                StackPanel (flyout)    — TODO actions + recent list; Refresh()
  Views/SettingsView.cs              StackPanel (settings)  — width NumberBox, SaveSettings(draft.ToJson())
  Services/AppSettings.cs            per-instance JSON settings (PreviewWidth, SaveFolder, RecordMicrophone)
  Services/CaptureResponseHandler.cs static Task HandleHeadlessAsync(Uri)
  Services/SnippingToolLauncher.cs   enum SnipMode; static Task<bool> LaunchAsync(SnipMode); RedirectScheme="sniprecord"
  Services/RecentStore.cs            record RecentItem(Path, CreatedAt, IsVideo); RecentStore(dataDirectory) -> recent.json
  Services/ColorPickerService.cs     Task<Windows.UI.Color?> PickAsync(); static ToHex(Color)
  Services/WindowRecorder.cs         IAsyncDisposable; StartWindowAsync(hwnd,..), StartRectangleAsync(RectInt32,..), StopAsync(), Completed
```

## Namespaces / classes
- `SnipRecord.ExtensionApp`: `App`, `Program` (const `InstanceKey = "SnipRecord.Host"`), `ActivationHub`, `SnipRecordPlugin` (const `PluginId`)
- `SnipRecord.ExtensionApp.Views`: `PreviewView`, `FlyoutView`, `SettingsView` (code-only UI, no .xaml — same as LyricsPlayer)
- `SnipRecord.ExtensionApp.Services`: `AppSettings`, `CaptureResponseHandler`, `SnippingToolLauncher`, `SnipMode`, `RecentStore`, `RecentItem`, `ColorPickerService`, `WindowRecorder`

## Identity values (keep in sync)
- PluginId `com.hq970123.sniprecord` — csproj `WidBarPluginId`, manifest `uap3:Properties/WidBarPluginId`, `SnipRecordPlugin.PluginId`
- Package Name `hq970123.SnipRecord`, Publisher `CN=hq970123` — manifest + build.yml `PUBLISHER`
- Version: manifest `1.0.0.0` is the literal CI's "Stamp version" step replaces; don't change it by hand.
- Protocol `sniprecord` (Application Id="App").
- Capabilities: runFullTrust, graphicsCaptureWithoutBorder, graphicsCaptureProgrammatic (rescap), microphone (DeviceCapability).

## Activation flow
1. WidBar starts the exe with `--widbar-pipe=<name>` → normal path: `FindOrRegisterForKey("SnipRecord.Host")`, subscribe `Activated` if current, `Application.Start` sets `ActivationHub.UiQueue`.
2. `sniprecord://...` launch → `Kind == Protocol` → if key owned by another process: `RedirectActivationToAsync` (CreateEvent + CoWaitForMultipleObjects) and exit; else `CaptureResponseHandler.HandleHeadlessAsync(uri)`, unregister key, exit.
3. Redirected activation in widget process → `ActivationHub.Raise` → `ProtocolActivated` on UI thread (latest URI buffered until first subscriber) → `SnipRecordPlugin.OnProtocolActivated`.

## Open questions
- Not compiled yet (no Windows toolchain here); only C# syntax-checked. First CI run is the real check.
- If WidBar runs more than one widget process, only the first owns the instance key.
- Whether WidBar 2 accepts the protocol extension / extra capabilities alongside the app extension is untested.
