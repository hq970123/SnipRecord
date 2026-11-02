namespace SnipRecord.ExtensionApp.Services;

public sealed record RecentItem(string Path, DateTimeOffset CreatedAt, bool IsVideo);

/// <summary>Recent screenshots / recordings, persisted under the widget data directory. TODO.</summary>
public sealed class RecentStore
{
    private readonly string _file;
    private readonly List<RecentItem> _items = new();

    public RecentStore(string dataDirectory)
    {
        _file = System.IO.Path.Combine(dataDirectory, "recent.json");
    }

    public string FilePath => _file;

    public int Count => _items.Count;

    public IReadOnlyList<RecentItem> Items => _items;

    public void Add(RecentItem item)
    {
        // TODO: dedupe, cap the list, persist.
        _items.Insert(0, item);
    }
}
