using Windows.UI;

namespace SnipRecord.ExtensionApp.Services;

/// <summary>Screen color picker (magnifier overlay + click to pick). TODO.</summary>
public sealed class ColorPickerService
{
    /// <summary>Returns the picked color, or null when cancelled.</summary>
    public Task<Color?> PickAsync()
    {
        return Task.FromResult<Color?>(null);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
