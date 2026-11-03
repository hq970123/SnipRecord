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

## UI + wiring (step 3)
Compile-checked on Linux: `work/sr-check-ui/check.csproj` compiles **all** ExtensionApp .cs files (except App.xaml.cs,
replaced by `AppStub.cs`) against Microsoft.WindowsAppSDK 2.3.1 + WidBar.SDK 2.0.5 + Vortice → C# 0 errors / 0 warnings
(only the later MakePri.exe step fails on Linux). SDK streamed into /tmp/dotnet (not persistent; /tmp gets wiped).
- `SnipRecordPlugin` is the controller (IWidgetFlyoutLifecycle too): `SnipAsync(SnipMode?)`, `RecordRectangleAsync()`
  (Snipping Tool video), `RecordWindowAsync()` (WindowRecorder.StartWindowPickAsync; toggles stop if recording),
  `StopRecordingAsync()`, `PickColorAsync()` (PickAsync(200)), `SetRecordMicrophone`, `CopyColor(hex)`; state
  `IsWindowRecording/RecordingElapsed/RecorderWarning/StatusMessage/StatusIsError`; 500 ms DispatcherTimer while recording.
  Uses `RecentStore.Shared`; `SaveShared()` on init + every draft; RecentStore.Changed / DiscoverCompleted marshalled to UI;
  protocol activations → `CaptureResponseHandler.HandleAsync(uri,_settings,_recent)`, Failed → status line. DiscoverAsync at
  init when capabilities are missing or > 1 day old.
- Flyout microphone toggle can only persist to shared settings.json (no WidBar settings context in the flyout), so at init
  `RecordMicrophone` is taken from settings.json when that file exists.
- `AppSettings.DefaultSnipMode` (SnipMode, Rectangle/Window/FreeForm; normalized).
- `Views/FlyoutCloser`: WidBar.SDK 2.0.5 has no public close API → reflection on internal
  `WidBar.SDK.Hosting.FlyoutHostWindow` (static `Instances`, `_pluginContent`, `IsOpen`, public `Dismiss()`); fallback hides
  the AppWindow from `XamlRoot.ContentIslandEnvironment.AppWindowId`. Called before snip / record / pick.
- Views: `PreviewView` (3 icon buttons E722/E714/EF3C, record MenuFlyout 矩形/窗口, red stop+timer while window recording),
  `FlyoutView` (tiles, `Segmented` mode rows, mic toggle, warning box, status, recent 8 captures with thumbnail/copy/reveal,
  recent colour swatches, 清空 links), `SettingsView` (default mode, delay, auto-save, image/video folders with
  Microsoft.Windows.Storage.Pickers.FolderPicker(windowId), colour format, mic, width), `UiHelpers`.
- GitHub: repo hq970123/SnipRecord could not be created (see daily report); repo zipped to work/SnipRecord.zip.

## Annotation editor (integration)
Files (all new): `Views/Editor/AnnotationEditor.cs` (public API, window, toolbar, save/copy/close),
`Views/Editor/AnnotationEditor.Input.cs` (pointer/keyboard/inline text/on-screen drawing), `Views/Editor/EditorUi.cs`
(local UI helpers — no dependency on other view files), `Services/Editor/EditorModel.cs` (annotation records, document,
undo/redo commands), `Services/Editor/AnnotationRenderer.cs` (Win2D drawing shared by screen + export, save, clipboard).

**NuGet the integrator must add to SnipRecord.ExtensionApp.csproj:**
```xml
<PackageReference Include="Microsoft.Graphics.Win2D" Version="1.4.0" />
```
1.4.0 (2026-03) is the first Win2D built on the split `Microsoft.WindowsAppSDK.WinUI` package (dependency ≥ 1.8.260204000);
with WinAppSDK 2.3.1 NuGet resolves WinUI 2.3.0 (checked in project.assets.json). Native `Microsoft.Graphics.Canvas.dll`
ships for win-x64/win-arm64 → flows into the self-contained publish/MSIX automatically. No manifest change needed.

**API** (namespace `SnipRecord.ExtensionApp.Views.Editor`, call on the UI thread):
```
static Task<AnnotationEditor> AnnotationEditor.OpenAsync(string imagePath, AnnotationEditorOptions? options = null)
    // loads the image first (throws FileNotFoundException / decoder exception), then opens + activates the window.
    // Same path already open → that window is brought to front and returned.
class AnnotationEditorOptions { EditorSaveMode DefaultSave = EditedCopy; bool AddToRecent = true; }
enum EditorSaveMode { EditedCopy /* <name>_edited.png beside the original, " (2)" if taken */, Overwrite }
AnnotationEditor: string ImagePath; string? SavedPath; bool IsDirty; Window Window;
    Task<string?> Completion   // completes on close with the last saved path (null if never saved)
    event EventHandler<string> Saved; event EventHandler Closed;
    void Activate(); void Close(bool force = false)
```
`AddToRecent` → `RecentStore.Shared.AddCapture(path, false)` after each save (so don't add it again in the plugin).

**UI hooks (suggested, not wired — files owned elsewhere):**
1. FlyoutView recent-capture rows (images only): an "标注" icon button (glyph `\uE70F` Edit) →
   `await AnnotationEditor.OpenAsync(item.Path)`; close the flyout first (FlyoutCloser) so the editor gets focus.
2. SnipRecordPlugin.OnProtocolActivated: on `CaptureStatus.Saved && !IsVideo`, optionally open the editor (make it a
   setting, e.g. `AppSettings.OpenEditorAfterSnip`, default off — Snipping Tool already shows its own markup UI).
3. Wrap the call in try/catch and show `ex.Message` in the status line (unreadable / deleted file).

**Behaviour:** tools 选择/移动(V) 画笔(P) 荧光笔(H) 箭头(A) 直线(L) 矩形(R) 椭圆(E) 文字(T) 马赛克(M) 序号标记(N) 裁剪(C);
Shift = 45° lines / square / circle; 6 colours + custom (ColorPicker flyout); 细/中/粗 (1/2/3). Colour/width also restyle the
selected object. Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) undo/redo (command stack: Add/Remove/Replace/Crop), Delete removes selection,
double-click text (select tool) or click it (text tool) to re-edit; Enter commits text, Shift+Enter new line. Ctrl+C copies,
Ctrl+S saves per `DefaultSave`, Ctrl+Shift+S 另存为 (FileSavePicker, PNG/JPEG/BMP), save SplitButton menu has all three;
复制并关闭 copies and closes without prompting. Esc: cancel drag → clear selection → close (ContentDialog 保存/不保存/取消
when dirty; title-bar close goes through the same prompt via AppWindow.Closing). Title shows ● when unsaved.
Crop = drag a region, applied on release (undoable); export is the crop at original pixel resolution.

**Design decisions:** Win2D over XAML shapes + RenderTargetBitmap — RTB renders at screen scale, needs the element live and
on-screen, and has size limits, so a "original resolution" export would be resampled; with Win2D the exact same drawing code
runs into a `CanvasRenderTarget` of crop size at 96 DPI (1 DIP = 1 px). Annotations are immutable records in original image
pixels (Id-stable; edits = replace), so select/move/delete/restyle and undo are uniform. Mosaic = whole image downscaled by the
block size (HighQualityCubic) and drawn back NearestNeighbor → cells aligned to the image grid, identical on screen and in export.
Original pixels are kept in memory: device-lost rebuilds never re-read the file (which Overwrite may have replaced) and
repeated overwrite-saves never double-bake annotations. Saves go through a temp file + File.Move(overwrite).
Window: `new Window()` in code, `MicaBackdrop` when `MicaController.IsSupported()` (else solid theme brush), client sized so
the image shows 1:1 in physical pixels (DPI of the monitor under the cursor via GetDpiForMonitor) plus toolbar, capped at 80% of
that monitor's work area (min 820×420 DIP); the view re-fits on resize/DPI change (XamlRoot.Changed).

**Compile check:** `work/sr-check-editor/check.csproj` (same shape as the old sr-check-ui: all ExtensionApp .cs except
App.xaml.cs + AppStub.cs, WinAppSDK 2.3.1, WidBar.SDK 2.0.5, Vortice 3.8.3, **+ Win2D 1.4.0**, win-x64) → C# 0 errors,
0 warnings; only the MakePri.exe step fails on Linux (expected). Not run on Windows.

**Risks / verify on device:**
- Own top-level Window in the widget process: WidBar.SDK itself creates `Microsoft.UI.Xaml.Window`s in this process
  (FlyoutHostWindow, SettingsHostWindow, StandaloneInfoWindow), so it is supported. The SDK never touches
  `Application.DispatcherShutdownMode` (default OnLastWindowClose): if the editor is ever the only open window when it closes,
  the process could exit — if that shows up, set `DispatcherShutdownMode = OnExplicitShutdown` in App's ctor.
- Foreground: opening from a protocol activation (no recent user input in our process) may leave the window behind others;
  `Activate()` + `SetForegroundWindow` is tried; from a flyout click it gets focus normally.
- Win2D 1.4.0 + WinAppSDK 2.x runtime pairing compiles but has not been run; fallback if it fails at runtime is an
  XAML-shapes renderer (model/commands are renderer-independent).
- Inline TextBox ↔ rendered text alignment is approximate (TextBox padding 0, border 1); font is Microsoft YaHei UI.
- Very large images (> device max bitmap size, usually 16384 px) fail to load → OpenAsync throws.
- Clipboard holds an in-memory PNG stream; `Clipboard.Flush()` keeps it after the editor closes.

## FullScreen / Hotkeys / GIF API
Compile-checked with `work/sr-check-editor/check.csproj` (all ExtensionApp .cs, .NET SDK 10.0): C# 0 errors / 0 warnings
(only MakePri fails on Linux). Not run on Windows. **Not wired into the plugin/views yet.**

**FullScreen** (`Services.FullScreen`): `enum FullScreenTarget {CurrentMonitor, AllMonitors}`;
`static Task<string?> FullScreenCapture.CaptureAsync(target, AppSettings?, RecentStore?, bool flash = true, CancellationToken = default)`
→ PNG path or null; `LastError` (Chinese, set when null) / `LastWarning` (folder fallback, clipboard failure); `IsCapturing`; never throws;
second concurrent call returns null ("正在截图"). Waits `DelaySeconds`; BitBlt SRCCOPY|CAPTUREBLT on a PMv2 thread (monitor under cursor or
virtual screen); PNG via BitmapEncoder; `<image folder>\全屏_yyyyMMdd_HHmmss[_n].png` (fallback Pictures\Screenshots\SnipRecord);
clipboard CF_DIB + "PNG" on an STA thread; `(recent ?? RecentStore.Shared).AddCapture(path,false)`; white fade window with WDA_EXCLUDEFROMCAPTURE.
Close the flyout before calling (it would be in the shot).

**Hotkeys** (`Services.Hotkeys`): `[Flags] HotkeyModifiers {None, Alt=1, Ctrl=2, Shift=4, Win=8}` (= MOD_*);
`readonly record struct Hotkey(Modifiers, VirtualKey)`: `None`, `IsEmpty`, `IsValid` (Ctrl/Alt/Win, or F1–F24), `ToString()` "Ctrl+Alt+A"/"",
`ToDisplayString()` (未设置), `static TryParse`, `KeyName(vk)`. `enum HotkeyAction {Snip, FullScreen, RecordRectangle, RecordWindow, ColorPick, OpenEditorOnLastCapture}`.
`HotkeyBindings`: indexer, `Defaults` (Ctrl+Alt+A/F/R/V/C, editor none), `GetDefault`, `AllActions`, `Clone`, `FromSettingsMap(IReadOnlyDictionary<string,string>?)`
(key = action name; missing → default, "" → disabled), `ToSettingsMap()`, `FindConflicts()` → `HotkeyConflict(Hotkey, Actions)`, `static GetDisplayName(action)` (中文).
`GlobalHotkeyService.Shared`: `Start()` (UI thread; captures SynchronizationContext), `Task<IReadOnlyDictionary<HotkeyAction,HotkeyRegistration>> RegisterAsync(bindings)`,
`UnregisterAllAsync()`, `Registrations`, `event HotkeyPressed(object, HotkeyAction)`, `Suspend()/Resume()` (ref-counted; while suspended RegisterAsync only probes),
`IsSuspended`, `Dispose()`. `HotkeyRegistration(Action, Hotkey, Status, ErrorCode)` + `StatusText`; `HotkeyRegistrationStatus {Registered, Disabled, InUse(1409 已被占用), Duplicate, Invalid, Failed}`.
`HotkeyRecorderBox : TextBox`: `Hotkey` property, `event HotkeyChanged(object, Hotkey)`; Backspace/Delete clears, Esc reverts, suspends the service while focused.
Settings persistence for bindings is not added to AppSettings yet (suggest `Dictionary<string,string>? Hotkeys`).

**GIF** (`Services.Gif`): `GifOptions {Fps=10, MaxWidth=800, LoopForever=true, TrimStartSeconds?, TrimEndSeconds?, FallbackFolder?}`, `From(fps,maxWidth,folder)`,
`SnapFps` (5/10/15/20), `ClampMaxWidth` (0 or 64–3840). `GifExportException`. `static Task<string> GifExporter.ExportAsync(mp4, GifOptions?, IProgress<double>?, CancellationToken)`
→ `<mp4 folder>\<name>.gif` / `<name> (2).gif` (fallback folder if not writable); frames via MediaComposition.GetThumbnailsAsync (NearestFrame, batches of 8),
WIC GIF encoder, per-frame `/grctlext/Delay` with accumulated rounding, NETSCAPE2.0 loop ext (ignored if rejected), max 12000 frames, `.part` temp deleted on failure.
`GifRecordingFlow(Func<bool> enabled, Func<GifOptions> options, RecentStore? recent)` (construct on UI thread): `Attach/Detach(WindowRecorder)`,
`HandleRecordingCompleted(mp4)` (if enabled: convert + delete mp4), `Task<GifConversionResult> ConvertAsync(mp4, options?, deleteMp4, progress, ct)` (serial, never throws),
`Cancel(path?)`, `PendingCount/IsBusy`, events `ExportStarted(string)`, `ProgressChanged(GifProgress)`, `ExportCompleted/ExportFailed(GifConversionResult)` (cancel → Failed, Cancelled=true).
Result: `GifConversionResult(Mp4Path, GifPath, Cancelled, Error, Mp4Deleted)`, `Succeeded`. Adds gif to RecentStore as image; removes the deleted mp4 entry.
Risks: GetThumbnailsAsync speed on long clips (~seconds per 100 frames); GIF palette is WIC's default (dithering quality untested).

## Phase A wiring (settings + plugin)
Compile-checked with `work/sr-check-editor/check.csproj`: C# 0 errors / 0 warnings (MakePri fails on Linux, expected). Views untouched.
- csproj: `Microsoft.Graphics.Win2D 1.4.0` added. `App` ctor sets `DispatcherShutdownMode = OnExplicitShutdown` (property exists in
  WinUI 2.3.0's Microsoft.WinUI.dll; WidBar.SDK exits the process itself on host shutdown). App.xaml.cs is excluded from the check
  project, so this line was verified with a temporary probe partial class.
- AppSettings (old JSON loads, missing → defaults): `Dictionary<string,string>? Hotkeys` (null = defaults), `GetHotkeys()`,
  `SetHotkeys(HotkeyBindings?)` (null = reset), `HotkeysEnabled=true`, `FullScreenAllMonitors=false`, `GifFps=10` (SnapFps),
  `GifMaxWidth=800` (ClampMaxWidth), `RecordAsGif=false`, `OpenEditorAfterCapture=false`, `GetGifOptions()` (fallback folder = image folder).
- GifRecordingFlow: new `bool IsPending(string mp4)`.

**Public SnipRecordPlugin members for the views (all UI thread):**
```
Task SnipAsync(SnipMode? mode = null)                 // SnipMode.FullScreen (or default mode FullScreen) → FullScreenAsync()
Task FullScreenAsync(FullScreenTarget? target = null) // null = settings.FullScreenAllMonitors; closes flyout, 200 ms, capture, status, editor if OpenEditorAfterCapture
Task OpenEditorAsync(string imagePath)                // never throws; errors → status line
Task OpenEditorOnLastCaptureAsync()                   // newest existing image in Recent
Task<GifConversionResult> ExportGifAsync(string mp4, IProgress<double>? progress = null)  // keeps MP4; never throws
void CancelGif(string mp4);  bool IsExportingGif(string mp4);  bool IsGifBusy
IReadOnlyDictionary<HotkeyAction,HotkeyRegistration>? HotkeyStatus;  event Action? HotkeyStatusChanged
(existing) RecordRectangleAsync, RecordWindowAsync (toggle), StopRecordingAsync, PickColorAsync, SetRecordMicrophone,
CopyColor, ClearStatus, Settings, Recent, IsWindowRecording, IsPaused, RecordingElapsed, RecorderWarning,
StatusMessage, StatusIsError, event StateChanged
```
- Hotkeys: static owner guard — the first initialized instance owns `GlobalHotkeyService.Shared` (Start + RegisterAsync at init,
  UnregisterAllAsync when `HotkeysEnabled` is false); on dispose ownership passes to the next live instance (re-registers with its
  settings) or everything is unregistered. `OnSettingsDraftChanged` re-registers only when the enabled flag / bindings changed.
  InUse/Failed registrations → error status line. HotkeyPressed: Snip (default mode) / FullScreen / RecordRectangle / RecordWindow
  (toggle) / ColorPick / OpenEditorOnLastCapture. The settings view should edit bindings with `HotkeyRecorderBox` +
  `settings.SetHotkeys(...)` then `SaveSettings(draft.ToJson())`, and show `plugin.HotkeyStatus[action].StatusText`
  (SettingsView currently gets only the context — phase B needs to pass the plugin in for HotkeyStatus).
- GIF: flow created on the UI thread (init / EnsureRecorder), attached to the WindowRecorder (after the plugin's Completed handler);
  Snipping Tool video activations → `HandleRecordingCompleted` (converts + deletes MP4 only when `RecordAsGif`). Flow events →
  status line ("正在转换 GIF：x 42%", "GIF 已保存：…", "已取消 GIF 导出。", error text).
- Image activations (Snipping Tool) open the editor when `OpenEditorAfterCapture`.
- DisposeAsync: release/hand off hotkeys, cancel GIF jobs, detach flow, dispose recorder.
