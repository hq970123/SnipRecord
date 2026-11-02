using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace SnipRecord.ExtensionApp.Services;

public enum CaptureStatus
{
    /// <summary>Capture copied into the save folder and added to the recent list.</summary>
    Saved,
    /// <summary>User cancelled in Snipping Tool (code 499). Show nothing.</summary>
    Cancelled,
    /// <summary>Snipping Tool error or saving failed; see <see cref="CaptureResult.Error"/>.</summary>
    Failed,
    /// <summary>A discover response was processed; see <see cref="SnippingToolLauncher.Capabilities"/>.</summary>
    Discovered,
    /// <summary>Not a URI we understand.</summary>
    Ignored,
}

/// <param name="Error">Chinese, user-facing; null unless <see cref="CaptureStatus.Failed"/>.</param>
public sealed record CaptureResult(
    CaptureStatus Status,
    string? FilePath = null,
    bool IsVideo = false,
    string? Error = null,
    int Code = 0,
    string? CorrelationId = null)
{
    public bool IsSuccess => Status == CaptureStatus.Saved;
}

/// <summary>
/// Handles sniprecord:// activations: Snipping Tool's capture response
/// (sniprecord://capture-response?code=..&amp;reason=..&amp;file-access-token=..) and the
/// discover response (sniprecord://discover-response?code=..&amp;discover={json}).
/// Never throws; failures come back as <see cref="CaptureStatus.Failed"/>.
/// </summary>
public static class CaptureResponseHandler
{
    private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".webm" };

    /// <summary>
    /// Protocol launch with no widget process: reads settings.json + recent.json from
    /// <see cref="AppPaths.DataDirectory"/>, handles the URI, returns when the file is saved.
    /// </summary>
    public static async Task<CaptureResult> HandleHeadlessAsync(Uri uri)
    {
        using var recent = new RecentStore(AppPaths.DataDirectory, watch: false);
        return await HandleAsync(uri, AppSettings.LoadShared(), recent);
    }

    /// <summary>
    /// Used by the running plugin. <paramref name="settings"/> = the instance's current settings
    /// (null = settings.json); they are also persisted to settings.json for the headless path.
    /// <paramref name="recent"/> = the plugin's store (null = <see cref="RecentStore.Shared"/>).
    /// </summary>
    public static async Task<CaptureResult> HandleAsync(Uri uri, AppSettings? settings = null, RecentStore? recent = null)
    {
        try
        {
            if (uri is null || !string.Equals(uri.Scheme, SnippingToolLauncher.RedirectScheme, StringComparison.OrdinalIgnoreCase))
            {
                return new CaptureResult(CaptureStatus.Ignored);
            }

            if (settings is null) settings = AppSettings.LoadShared();
            else settings.SaveShared();

            var query = ParseQuery(uri.Query);
            query.TryGetValue("code", out var codeText);
            query.TryGetValue("reason", out var reason);
            query.TryGetValue("x-request-correlation-id", out var correlationId);

            if (string.Equals(uri.Host, SnippingToolLauncher.DiscoverResponseHost, StringComparison.OrdinalIgnoreCase)
                || query.ContainsKey("discover"))
            {
                query.TryGetValue("discover", out var payload);
                SnippingToolLauncher.HandleDiscoverPayload(codeText, payload);
                return new CaptureResult(CaptureStatus.Discovered, Code: ParseCode(codeText), CorrelationId: correlationId);
            }

            if (codeText is null)
            {
                Debug.WriteLine($"[SnipRecord] activation without code ignored: {uri}");
                return new CaptureResult(CaptureStatus.Ignored);
            }

            if (correlationId is not null && SnippingToolLauncher.LastCorrelationId is { } last && last != correlationId)
            {
                // Only informative: a response may come from an earlier request or another process.
                Debug.WriteLine($"[SnipRecord] correlation id mismatch (expected {last}, got {correlationId})");
            }

            var code = ParseCode(codeText);
            switch (code)
            {
                case 200:
                    break;
                case 499:
                    return new CaptureResult(CaptureStatus.Cancelled, Code: 499, CorrelationId: correlationId);
                default:
                    return Fail(ErrorText(code, reason), code, correlationId);
            }

            if (!query.TryGetValue("file-access-token", out var token) || string.IsNullOrWhiteSpace(token))
            {
                return Fail("截图工具没有返回文件。", code, correlationId);
            }

            StorageFile file;
            try
            {
                file = await SharedStorageAccessManager.RedeemTokenForFileAsync(token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SnipRecord] RedeemTokenForFileAsync failed: {ex}");
                return Fail($"无法读取截图文件：{ex.Message}", code, correlationId);
            }

            var ext = NormalizeExtension(file);
            var isVideo = Array.IndexOf(VideoExtensions, ext) >= 0
                || (file.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false);
            if (string.IsNullOrEmpty(ext)) ext = isVideo ? ".mp4" : ".png";

            string saved;
            try
            {
                saved = await CopyToFolderAsync(file, isVideo ? settings.GetVideoFolder() : settings.GetImageFolder(), ext);
            }
            catch (Exception ex) when (isVideo ? settings.SaveFolder is not null : settings.ImageSaveFolder is not null)
            {
                // Custom folder unusable (removed drive, no permission): fall back to the default.
                Debug.WriteLine($"[SnipRecord] custom folder failed, using default: {ex.Message}");
                var defaults = new AppSettings();
                try
                {
                    saved = await CopyToFolderAsync(file, isVideo ? defaults.GetVideoFolder() : defaults.GetImageFolder(), ext);
                }
                catch (Exception ex2)
                {
                    return Fail($"保存失败：{ex2.Message}", code, correlationId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SnipRecord] save failed: {ex}");
                return Fail($"保存失败：{ex.Message}", code, correlationId);
            }

            try
            {
                (recent ?? RecentStore.Shared).AddCapture(saved, isVideo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SnipRecord] recent list update failed: {ex.Message}");
            }

            // Images: Snipping Tool already put the capture on the clipboard.
            return new CaptureResult(CaptureStatus.Saved, saved, isVideo, Code: code, CorrelationId: correlationId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] capture response failed: {ex}");
            return Fail($"处理截图结果时出错：{ex.Message}", 0, null);
        }
    }

    private static CaptureResult Fail(string error, int code, string? correlationId) =>
        new(CaptureStatus.Failed, Error: error, Code: code, CorrelationId: correlationId);

    private static string ErrorText(int code, string? reason) => code switch
    {
        400 => "截图请求无效（400）。请更新 Windows 截图工具后重试。",
        408 => "截图超时（408），请重试。",
        500 => "截图工具内部错误（500），请重试。",
        0 => $"截图工具返回了无法识别的结果{(string.IsNullOrWhiteSpace(reason) ? "。" : $"：{reason}")}",
        _ => $"截图工具返回错误 {code}{(string.IsNullOrWhiteSpace(reason) ? "。" : $"：{reason}")}",
    };

    private static int ParseCode(string? codeText) =>
        int.TryParse(codeText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0;

    /// <summary>WwwFormUrlDecoder throws on unknown names, so copy everything into a dictionary.</summary>
    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query) || query == "?") return result;
        try
        {
            var decoder = new WwwFormUrlDecoder(query);
            foreach (var entry in decoder)
            {
                if (entry?.Name is { Length: > 0 } name && !result.ContainsKey(name))
                {
                    result[name] = entry.Value ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] WwwFormUrlDecoder failed ({ex.Message}); using fallback parser");
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = part.IndexOf('=');
                var name = Uri.UnescapeDataString((i < 0 ? part : part[..i]).Replace('+', ' '));
                var value = i < 0 ? string.Empty : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
                if (name.Length > 0 && !result.ContainsKey(name)) result[name] = value;
            }
        }
        return result;
    }

    private static string NormalizeExtension(StorageFile file)
    {
        var ext = file.FileType;
        if (string.IsNullOrWhiteSpace(ext)) ext = System.IO.Path.GetExtension(file.Name);
        if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
        ext = ext.Trim().ToLowerInvariant();
        return ext.StartsWith('.') ? ext : "." + ext;
    }

    private static async Task<string> CopyToFolderAsync(StorageFile file, string folder, string ext)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        using var source = await file.OpenStreamForReadAsync();
        for (var n = 0; ; n++)
        {
            var name = n == 0 ? $"SnipRecord_{stamp}{ext}" : $"SnipRecord_{stamp}_{n}{ext}";
            var target = System.IO.Path.Combine(folder, name);
            FileStream dest;
            try
            {
                dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            }
            catch (IOException) when (n < 100 && File.Exists(target))
            {
                continue; // name taken; try the next suffix
            }

            try
            {
                await using (dest)
                {
                    await source.CopyToAsync(dest);
                }
            }
            catch
            {
                try { File.Delete(target); } catch { }
                throw;
            }
            return target;
        }
    }
}
