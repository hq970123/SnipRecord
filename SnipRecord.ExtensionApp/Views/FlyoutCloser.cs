using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WidBar.SDK;

namespace SnipRecord.ExtensionApp.Views;

/// <summary>
/// WidBar.SDK 2.0.5 has no public "close the flyout" call. The flyout window is the internal
/// <c>WidBar.SDK.Hosting.FlyoutHostWindow</c> with a public <c>Dismiss()</c>; we call it via
/// reflection (instances listed in its static <c>Instances</c> field). If that shape changes,
/// fall back to hiding the AppWindow that hosts <paramref name="content"/>.
/// </summary>
internal static class FlyoutCloser
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Close(UIElement? content)
    {
        try
        {
            if (TryDismissViaSdk(content)) return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] SDK flyout dismiss failed: {ex.Message}");
        }

        try
        {
            var root = content?.XamlRoot;
            var id = root?.ContentIslandEnvironment?.AppWindowId;
            if (id is { } windowId && windowId.Value != 0)
            {
                AppWindow.GetFromWindowId(windowId)?.Hide();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SnipRecord] flyout hide fallback failed: {ex.Message}");
        }
    }

    private static bool TryDismissViaSdk(UIElement? content)
    {
        var type = typeof(WidgetPluginBase).Assembly.GetType("WidBar.SDK.Hosting.FlyoutHostWindow");
        if (type is null) return false;
        var dismiss = type.GetMethod("Dismiss", AnyInstance, Type.EmptyTypes);
        if (dismiss is null) return false;
        var instancesField = type.GetField("Instances", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (instancesField?.GetValue(null) is not IEnumerable instances) return false;

        var isOpen = type.GetProperty("IsOpen", AnyInstance);
        var pluginContent = type.GetField("_pluginContent", AnyInstance);

        var windows = new List<object>();
        foreach (var entry in instances)
        {
            var window = entry;
            if (window is not null && !type.IsInstanceOfType(window))
            {
                // Dictionary entry (KeyValuePair<,>): take its Value.
                window = window.GetType().GetProperty("Value")?.GetValue(window);
            }
            if (window is not null && type.IsInstanceOfType(window)) windows.Add(window);
        }
        if (windows.Count == 0) return false;

        // Prefer the window that hosts our content; otherwise every open flyout of this process.
        var targets = windows.Where(w => content is not null && ContainsContent(pluginContent?.GetValue(w), content)).ToList();
        if (targets.Count == 0)
        {
            targets = windows.Where(w => isOpen?.GetValue(w) is not false).ToList();
        }
        foreach (var w in targets) dismiss.Invoke(w, null);
        return targets.Count > 0;
    }

    private static bool ContainsContent(object? hosted, UIElement content)
    {
        if (hosted is null) return false;
        if (ReferenceEquals(hosted, content)) return true;
        // The SDK may wrap our element; walk up from our element instead.
        DependencyObject? node = content;
        while (node is not null)
        {
            if (ReferenceEquals(node, hosted)) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
