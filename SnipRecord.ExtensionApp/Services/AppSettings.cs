using System.Text.Json;

namespace SnipRecord.ExtensionApp.Services;

/// <summary>Per-instance settings. WidBar stores this JSON for each placed copy.</summary>
public sealed class AppSettings
{
    public const int MinWidth = 48;
    public const int MaxWidth = 400;

    /// <summary>Taskbar slot width in logical pixels.</summary>
    public int PreviewWidth { get; set; } = 120;

    /// <summary>Folder for recordings; null = Videos\SnipRecord. TODO</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Also record the microphone. TODO</summary>
    public bool RecordMicrophone { get; set; }

    public static AppSettings FromJson(string? json)
    {
        try
        {
            var s = string.IsNullOrWhiteSpace(json)
                ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            s.PreviewWidth = Math.Clamp(s.PreviewWidth, MinWidth, MaxWidth);
            return s;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public AppSettings Clone() => FromJson(ToJson());
}
