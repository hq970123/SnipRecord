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

## Snip & Color (step 2B)
Namespace `SnipRecord.ExtensionApp.Services` (picker internals in `.Services.ColorPicker`).
Compile-checked on Linux with `/tmp/dotnet/dotnet build` of `work/sr-check-snip/check.csproj`
(net10.0-windows10.0.19041.0, EnableWindowsTargeting; its Usage.cs mirrors the existing call sites
in Program.cs / SnipRecordPlugin.cs / FlyoutView.cs): 0 errors, 0 warnings. Logic smoke-tested
(formatters, URIs, RecentStore across two instances, response parsing). Win32/GDI behaviour untested.

### Shared data folder — `AppPaths.DataDirectory`
ApplicationData.Current.LocalFolder (package identity) → else `%LOCALAPPDATA%\Packages\<PFN>\LocalState`
derived from the WindowsApps install folder name (same physical folder, for a widget process started
without identity) → else `%LOCALAPPDATA%\SnipRecord`. Files: `settings.json`, `recent.json`, `snippingtool.json`.
`AtomicFile` (internal, in AppSettings.cs): temp file + `File.Move(overwrite)`, retries on IOException.

### AppSettings (new fields, old JSON still loads)
- `ImageSaveFolder` (null = Pictures\Screenshots\SnipRecord), `SaveFolder` = videos (null = Videos\SnipRecord)
- `DelaySeconds` 0/3/5/10 (`AllowedDelays`, snapped in `Normalize()`), `SnipAutoSave` (adds `&auto-save`)
- `ColorFormat` "HEX" | "RGB" | "HSL" (`ColorFormatHex/Rgb/Hsl` consts)
- `GetImageFolder()`, `GetVideoFolder()`, `Normalize()`
- `static LoadShared()`, `bool SaveShared()`, `static SharedFilePath` — **plugin must call `settings.SaveShared()`
  in InitializeAsync and OnSettingsDraftChanged** so the headless handler / launcher see the same settings.

### SnippingToolLauncher
- `Task<bool> LaunchAsync(SnipMode)` (shared settings) / `LaunchAsync(SnipMode, AppSettings, CancellationToken)`:
  waits DelaySeconds, then `Launcher.LaunchUriAsync`. Image: `ms-screenclip://capture/image?{rectangle|window|freeform}&enabledModes=SnippingAllModes[&auto-save]&user-agent=SnipRecord&api-version=1.2&redirect-uri=sniprecord://capture-response&x-request-correlation-id=GUID`;
  `SnipMode.Recording` → `capture/video?enabledModes=RecordAllModes…`. `SnipMode.FullScreen` has no protocol
  mode → launched as `rectangle` (user picks fullscreen in the toolbar).
- `BuildCaptureUri(...)`, `BuildDiscoverUri()`, `LastCorrelationId`
- `Task<bool> DiscoverAsync()` → response `sniprecord://discover-response?...&discover={json}` is handled by
  CaptureResponseHandler → `Capabilities` (SnippingToolCapabilities: Supported, Version, SupportedVersions,
  SupportsImage/Video, CheckedAt; persisted in snippingtool.json), `bool? IsSupported`, event `DiscoverCompleted`.
  Supported = supportedVersions contains 1.2 && capture/image present.
- `Task<bool> IsProtocolAvailableAsync()` (Launcher.QueryUriSupportAsync).
- Consts: `RedirectScheme`, `CaptureResponseHost`, `DiscoverResponseHost`, `CaptureRedirectUri`, `DiscoverRedirectUri`, `UserAgent`, `ApiVersion`.

### CaptureResponseHandler
- `Task<CaptureResult> HandleAsync(Uri, AppSettings? settings = null, RecentStore? recent = null)` — for the plugin
  (persists `settings` via SaveShared; null recent = `RecentStore.Shared`).
- `Task<CaptureResult> HandleHeadlessAsync(Uri)` — loads settings.json, uses a non-watching RecentStore on the shared folder.
- `CaptureResult(Status, FilePath, IsVideo, Error, Code, CorrelationId)`, `IsSuccess`; `CaptureStatus` = Saved | Cancelled (499, show nothing)
  | Failed (Error is Chinese user text) | Discovered | Ignored. Never throws.
- 200: `SharedStorageAccessManager.RedeemTokenForFileAsync` → stream-copied to `SnipRecord_yyyyMMdd_HHmmss[_n].ext`
  in the image/video folder (custom folder failure → default folder) → `RecentStore.AddCapture`. Clipboard untouched
  (Snipping Tool already copies images). Video = .mp4/.mov/… or content type video/*.
- Query parsed with WwwFormUrlDecoder into a dictionary (it throws on missing names); manual fallback parser.

### RecentStore (recent.json: `{captures:[{path,kind:"image"|"video",time}], colors:["#RRGGBB"]}`)
- `RecentStore()` (shared folder, watcher on), `RecentStore(string dataDirectory, bool watch = true)`, `static Shared`, `IDisposable`
- `Items` (≤8, newest first, snapshot), `Count`, `Colors` (≤12), `Add(RecentItem)`, `AddCapture(path, isVideo)`, `AddColor(hex)`,
  `Remove(path)`, `RemoveMissing()`, `ClearCaptures()`, `ClearColors()`, `Reload()`, `FilePath`
- event `Changed` — **any thread** (watcher / caller); marshal to UI with DispatcherQueue.
- Each mutation: named mutex `Local\SnipRecord.RecentStore` → re-read disk → change → atomic write. Watcher debounced 150 ms, fires only if content changed.

### ColorPickerService
- `Task<Color?> PickAsync()` / `PickAsync(int startDelayMs)` (Windows.UI.Color; null = cancelled/failed); concurrent call returns the running task.
- ctor `()` (RecentStore.Shared + settings.json) or `(RecentStore? recent, Func<AppSettings>? settings)`.
- event `ColorPicked(Color, string copiedText)` (thread-pool thread), `IsPicking`, `static ToHex(Color)`, `static Format(Color, string? format)`.
- Implementation (`Services/ColorPicker/`): `PickerSession` runs on its own STA thread with its own message loop, sets
  thread DPI to PerMonitorV2, BitBlt(SRCCOPY|CAPTUREBLT) of the virtual screen into a top-down 32-bpp DIB **before** any
  window shows, one WS_POPUP|TOPMOST|TOOLWINDOW window per monitor painting the frozen image, loupe 11×11 px at 10×
  (scaled by monitor DPI) + crosshair + HEX/RGB text. Left click (press+release on overlay) / Enter / Space picks; arrows
  nudge 1 px (Shift 10); Esc / right click / 2-minute timeout cancel. Keys via WH_KEYBOARD_LL (swallowed while picking).
  Clipboard via Win32 CF_UNICODETEXT (`Win32Clipboard`), format per `AppSettings.ColorFormat` (`ColorFormatter`); HEX added to RecentStore.

### Required follow-ups outside 2B's files (not done — files owned elsewhere)
1. SnipRecordPlugin.InitializeAsync: `_recent = RecentStore.Shared;` (or `new RecentStore()`), **not** `new RecentStore(context.DataDirectory)`
   — the headless handler writes to `AppPaths.DataDirectory`, so the WidBar data dir would never see headless captures.
2. SnipRecordPlugin: call `_settings.SaveShared()` after loading/changing settings; in OnProtocolActivated use
   `var r = await CaptureResponseHandler.HandleAsync(uri, _settings, _recent);` and show `r.Error` when `r.Status == CaptureStatus.Failed`.
3. Subscribe `_recent.Changed` → `DispatcherQueue.TryEnqueue(_flyout.Refresh)`; dispose not needed for Shared.
4. Close/hide the flyout before `PickAsync(200)` so it is not in the frozen screenshot.
5. Optionally call `SnippingToolLauncher.DiscoverAsync()` once at startup and grey out buttons when `IsSupported == false`.

## WindowRecorder (step 2A — window recording)
Files: `Services/WindowRecorder.cs` (public facade) + `Services/Recording/*` (namespace
`SnipRecord.ExtensionApp.Services.Recording`, all `internal`):
- `Win32.cs` user32/gdi32/dwmapi/shcore P/Invoke + helpers (frame bounds, cloaked, DPI scale, work area, ExcludeFromCapture).
- `Win32Window.cs` raw Win32 window base, `UiThread` (dedicated STA thread + message loop, PMv2 DPI, `Post`/`InvokeAsync`/`Shutdown`), `SolidOverlayWindow` (click-through tint or hollow frame).
- `WindowPicker.cs` picker: alpha-1 layered catcher over the virtual screen + tint/frame highlight. The window under the cursor is found by walking `EnumWindows` in Z-order (WindowFromPoint would hit the catcher); skips own process, invisible/minimized/cloaked, WS_EX_TRANSPARENT, taskbar/desktop classes, untitled tool windows. Left click = select, Esc (WM_KEYDOWN + GetAsyncKeyState poll, works without focus) / right click = cancel.
- `RecordingOverlay.cs` red 2px frame outside the target + `RecordingControlBar` (GDI, timer, pause/resume, stop; draggable; auto-follows the window above its top edge, inside it if no room). Both `WDA_EXCLUDEFROMCAPTURE` (fallback WDA_MONITOR). 100 ms follow timer; target gone → stop.
- `Direct3DInterop.cs` D3D11 device (Vortice, BGRA, multithread-protected, WARP fallback), IDirect3DDevice/IDirect3DSurface bridging, `IDirect3DDxgiInterfaceAccess::GetInterface` and `IGraphicsCaptureItemInterop::CreateForWindow` via raw vtable delegates (RoGetActivationFactory), `IsBorderRequired=false` via IGraphicsCaptureSession3 (not in the 19041 projection).
- `CaptureEncoder.cs` WGC free-threaded pool → per-sample texture → `MediaStreamSample.CreateFromDirect3D11Surface` → `MediaStreamSource` → `MediaTranscoder.PrepareMediaStreamSourceTranscodeAsync` (H.264 MP4, AAC 48k/2ch/192k when audio). Output size = initial window size, even, capped to 4096x2304; resize → pool `Recreate` + D2D letterbox into the fixed size (crop fallback). 30 fps cap, bitrate = w*h*30*0.15 (1–60 Mbps). Samples are pushed via request deferrals (`SampleQueue`), never blocking MF threads.
- `AudioCapture.cs` WASAPI [ComImport] interop; `ActivateAudioInterfaceAsync` with `VAD\Process_Loopback` + PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE (build ≥ 20348), mic via default capture device id + AUTOCONVERTPCM; all 48 kHz stereo s16. `AudioMixer` mixes into one PCM stream paced by the media clock (120 ms latency, silence on underrun, backlog trimmed > 400 ms).
- `RecordingPrimitives.cs` `RecordingClock` (QPC; pause freezes media time, paused span subtracted from video + audio timestamps), `SampleQueue`, `PcmRingBuffer`.

Public API (`SnipRecord.ExtensionApp.Services.WindowRecorder`, IAsyncDisposable):
```
bool IsRecording; bool IsPaused; TimeSpan Elapsed; string? OutputPath;
bool AudioUnavailable; string? Warning            // non-fatal, Chinese text (no Win11 / audio / mic failure)
event EventHandler<string>? Completed             // saved MP4 path
event EventHandler<Exception>? Failed             // finalize failure / no frames (file deleted); start failures throw instead
event EventHandler? StateChanged                  // start / pause / resume / stop
static string BuildOutputPath(AppSettings?)       // <SaveFolder | Videos\SnipRecord>\窗口录屏_yyyyMMdd_HHmmss.mp4
static Task<IntPtr> PickWindowAsync(CancellationToken = default)   // Zero = cancelled
Task<bool> StartWindowPickAsync(AppSettings?, CancellationToken = default)  // false = cancelled; uses SaveFolder + RecordMicrophone
Task StartWindowAsync(IntPtr hwnd, string? outputPath, bool microphone)    // throws if it cannot start
Task StartRectangleAsync(RectInt32, string, bool)  // throws NotSupportedException (Snipping Tool does rectangles)
void Pause(); void Resume(); void TogglePause();
Task StopAsync()                                   // idempotent; Completed/Failed raised once
```
Events are posted to the SynchronizationContext current when Start* was called (call from the UI thread).
Stop is also triggered by the control bar, the target window closing, or `DisposeAsync`. File name collisions get
" (2)" etc. (StorageFolder GenerateUniqueName), so read the path from `Completed`/`OutputPath`.

NuGet added to SnipRecord.ExtensionApp.csproj: `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.Direct2D1` 3.8.3.

Verified: the Recording files + WindowRecorder + AppSettings compile with 0 errors/0 warnings on Linux
(.NET SDK 10.0.403, `net10.0-windows10.0.19041.0`, EnableWindowsTargeting, Vortice 3.8.3) — not run on Windows yet.
Risks / to verify on device:
- Built-in COM interop is required for the WASAPI [ComImport] interfaces and the managed completion-handler CCW
  (fine while the app is not trimmed/AOT; `PublishTrimmed` must stay off).
- IsBorderRequired may be refused without `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` (not called;
  the yellow border then just stays). Cursor capture uses IsCursorCaptureEnabled (19041+).
- Process loopback captures the HWND owner's process tree; UWP/ApplicationFrameHost windows will record silence.
- Static windows deliver no WGC frames → variable frame rate MP4 (expected); if MF stalls audio waiting for video
  the audio queue just grows in memory.
- Picker/Esc: SetForegroundWindow may be refused, so Esc is polled with GetAsyncKeyState (the key still reaches the
  foreground app).
