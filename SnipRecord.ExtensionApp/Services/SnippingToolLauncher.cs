using System.Diagnostics;
using System.Text.Json;
using Windows.System;

namespace SnipRecord.ExtensionApp.Services;

public enum SnipMode
{
    Rectangle,
    Window,
    /// <summary>The protocol has no fullscreen mode parameter: launched as rectangle with all snip modes enabled.</summary>
    FullScreen,
    FreeForm,
    /// <summary>Snipping Tool screen recording (rectangle only).</summary>
    Recording,
}

/// <summary>Result of the last ms-screenclip://discover round trip (persisted to snippingtool.json).</summary>
public sealed class SnippingToolCapabilities
{
    public bool Supported { get; set; }
    public string? Version { get; set; }
    public List<string> SupportedVersions { get; set; } = new();
    public bool SupportsImage { get; set; }
    public bool SupportsVideo { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>
/// Launches Snipping Tool through the ms-screenclip: protocol (api-version 1.2) with a
/// sniprecord:// redirect. Must use Windows.System.Launcher.LaunchUriAsync so Snipping Tool
/// sees our package identity; the response comes back as a protocol activation that
/// <see cref="CaptureResponseHandler"/> handles.
/// </summary>
public static class SnippingToolLauncher
{
    public const string RedirectScheme = "sniprecord";
    public const string CaptureResponseHost = "capture-response";
    public const string DiscoverResponseHost = "discover-response";
    public const string CaptureRedirectUri = RedirectScheme + "://" + CaptureResponseHost;
    public const string DiscoverRedirectUri = RedirectScheme + "://" + DiscoverResponseHost;
    public const string UserAgent = "SnipRecord";
    public const string ApiVersion = "1.2";

    private const string CapabilitiesFileName = "snippingtool.json";
    private static readonly object Gate = new();
    private static SnippingToolCapabilities? _capabilities;
    private static bool _capabilitiesLoaded;

    /// <summary>Correlation id of the last capture request launched by this process.</summary>
    public static string? LastCorrelationId { get; private set; }

    /// <summary>Raised (any thread) after a discover response was parsed.</summary>
    public static event Action<SnippingToolCapabilities>? DiscoverCompleted;

    /// <summary>Last discover result (memory, else snippingtool.json); null if never discovered.</summary>
    public static SnippingToolCapabilities? Capabilities
    {
        get
        {
            lock (Gate)
            {
                if (!_capabilitiesLoaded)
                {
                    _capabilitiesLoaded = true;
                    _capabilities = LoadCapabilities();
                }
                return _capabilities;
            }
        }
    }

    /// <summary>true/false from the last discover response; null = unknown (never discovered).</summary>
    public static bool? IsSupported => Capabilities?.Supported;

    /// <summary>Builds the ms-screenclip capture URI (exposed for tests / logging).</summary>
    public static Uri BuildCaptureUri(SnipMode mode, bool autoSave, string correlationId)
    {
        var autoSaveParam = autoSave ? "&auto-save" : string.Empty;
        var common = $"&user-agent={UserAgent}&api-version={ApiVersion}&redirect-uri={CaptureRedirectUri}&x-request-correlation-id={correlationId}";

        if (mode == SnipMode.Recording)
        {
            return new Uri($"ms-screenclip://capture/video?enabledModes=RecordAllModes{autoSaveParam}{common}");
        }

        var modeParam = mode switch
        {
            SnipMode.Window => "window",
            SnipMode.FreeForm => "freeform",
            _ => "rectangle", // Rectangle, FullScreen (user switches to fullscreen in the toolbar)
        };
        return new Uri($"ms-screenclip://capture/image?{modeParam}&enabledModes=SnippingAllModes{autoSaveParam}{common}");
    }

    public static Uri BuildDiscoverUri() =>
        new($"ms-screenclip://discover?user-agent={UserAgent}&redirect-uri={DiscoverRedirectUri}");

    /// <summary>Launch with the shared settings (delay / auto-save from settings.json).</summary>
    public static Task<bool> LaunchAsync(SnipMode mode) => LaunchAsync(mode, AppSettings.LoadShared());

    /// <summary>
    /// Waits <see cref="AppSettings.DelaySeconds"/> (0/3/5/10), then launches Snipping Tool.
    /// Returns false if the launch failed or was cancelled during the delay.
    /// </summary>
    public static async Task<bool> LaunchAsync(SnipMode mode, AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var delay = Array.IndexOf(AppSettings.AllowedDelays, settings.DelaySeconds) >= 0 ? settings.DelaySeconds : 0;
        try
        {
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);

            var correlationId = Guid.NewGuid().ToString();
            var uri = BuildCaptureUri(mode, settings.SnipAutoSave, correlationId);
            LastCorrelationId = correlationId;
            Debug.WriteLine($"[SnipRecord] launching {uri}");
            return await Launcher.LaunchUriAsync(uri);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] LaunchUriAsync failed: {ex}");
            return false;
        }
    }

    /// <summary>Asks Snipping Tool for its capabilities; the answer arrives via sniprecord://discover-response.</summary>
    public static async Task<bool> DiscoverAsync()
    {
        try
        {
            return await Launcher.LaunchUriAsync(BuildDiscoverUri());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] discover launch failed: {ex}");
            return false;
        }
    }

    /// <summary>Whether some app (Snipping Tool) handles ms-screenclip: at all.</summary>
    public static async Task<bool> IsProtocolAvailableAsync()
    {
        try
        {
            var status = await Launcher.QueryUriSupportAsync(
                new Uri("ms-screenclip://capture/image?rectangle"), LaunchQuerySupportType.Uri);
            return status == LaunchQuerySupportStatus.Available;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] QueryUriSupportAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Parses a discover response JSON (value of the "discover" query parameter, already form-decoded),
    /// stores and persists the result. Called by <see cref="CaptureResponseHandler"/>.
    /// </summary>
    internal static SnippingToolCapabilities HandleDiscoverPayload(string? code, string? payload)
    {
        var caps = new SnippingToolCapabilities { CheckedAt = DateTimeOffset.Now };
        if (code == "200" && !string.IsNullOrWhiteSpace(payload))
        {
            if (!TryParseDiscover(payload, caps))
            {
                // Docs show the value may still be percent-encoded once more.
                TryParseDiscover(Uri.UnescapeDataString(payload), caps);
            }
        }

        lock (Gate)
        {
            _capabilities = caps;
            _capabilitiesLoaded = true;
        }
        try
        {
            AtomicFile.WriteAllText(CapabilitiesPath, JsonSerializer.Serialize(caps));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] snippingtool.json write failed: {ex.Message}");
        }

        try { DiscoverCompleted?.Invoke(caps); }
        catch (Exception ex) { Debug.WriteLine($"[SnipRecord] DiscoverCompleted handler threw: {ex}"); }
        return caps;
    }

    private static bool TryParseDiscover(string json, SnippingToolCapabilities caps)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("version", out var v)) caps.Version = ElementToString(v);
            caps.SupportedVersions.Clear();
            if (root.TryGetProperty("supportedVersions", out var sv) && sv.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in sv.EnumerateArray())
                {
                    var s = ElementToString(e);
                    if (!string.IsNullOrEmpty(s)) caps.SupportedVersions.Add(s);
                }
            }
            if (root.TryGetProperty("capabilities", out var cap) && cap.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in cap.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("path", out var p)) continue;
                    var path = p.GetString() ?? string.Empty;
                    if (path.Equals("capture/image", StringComparison.OrdinalIgnoreCase)) caps.SupportsImage = true;
                    if (path.Equals("capture/video", StringComparison.OrdinalIgnoreCase)) caps.SupportsVideo = true;
                }
            }

            var versionOk = caps.SupportedVersions.Count == 0
                ? VersionAtLeast(caps.Version, 1.2)
                : caps.SupportedVersions.Exists(s => VersionEquals(s, ApiVersion));
            caps.Supported = versionOk && caps.SupportsImage;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ElementToString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };

    private static bool VersionEquals(string s, string expected) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
        double.TryParse(expected, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) &&
        Math.Abs(a - b) < 0.0001;

    private static bool VersionAtLeast(string? s, double min) =>
        s is not null &&
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
        a >= min - 0.0001;

    private static string CapabilitiesPath => System.IO.Path.Combine(AppPaths.DataDirectory, CapabilitiesFileName);

    private static SnippingToolCapabilities? LoadCapabilities()
    {
        try
        {
            var text = AtomicFile.ReadAllText(CapabilitiesPath);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<SnippingToolCapabilities>(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] snippingtool.json read failed: {ex.Message}");
            return null;
        }
    }
}
