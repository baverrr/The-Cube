namespace TheCube.Core;

/// <summary>
/// Items kept for reuse, at most one per key, all made for one settings object and one display layout. Settings
/// are compared by reference: the app replaces its settings object whenever they change (it never edits one in
/// place for anything a kept item depends on). Every method returns the items the caller must now dispose of, so
/// nothing is dropped without being closed. Used on one thread.
/// </summary>
/// <typeparam name="TKey">What an item is kept for (a monitor).</typeparam>
/// <typeparam name="TItem">The kept item (a cube window).</typeparam>
public sealed class SparePool<TKey, TItem>(Func<TItem, TKey> keyOf)
    where TKey : notnull
    where TItem : class
{
    private readonly Dictionary<TKey, TItem> _items = [];
    private object? _settings;
    private int _layout;
    private bool _shutDown;

    public int Count => _items.Count;

    /// <summary>
    /// Keep <paramref name="item"/>, made for <paramref name="itemSettings"/> and display layout
    /// <paramref name="itemLayout"/>, given what's current now. Returns the items to dispose of: the item itself if
    /// it's stale or the pool is shut down, an older item for the same key, or everything kept for older settings.
    /// </summary>
    public IReadOnlyList<TItem> Add(TItem item, object itemSettings, int itemLayout, object currentSettings, int currentLayout)
    {
        if (_shutDown || !ReferenceEquals(itemSettings, currentSettings) || itemLayout != currentLayout)
        {
            return [item];
        }

        var dispose = new List<TItem>();
        if (!ReferenceEquals(_settings, currentSettings) || _layout != currentLayout)
        {
            dispose.AddRange(TakeAll());
            _settings = currentSettings;
            _layout = currentLayout;
        }

        var key = keyOf(item);
        if (_items.Remove(key, out var old) && !ReferenceEquals(old, item))
        {
            dispose.Add(old);
        }

        _items[key] = item;
        return dispose;
    }

    /// <summary>Take the item kept for <paramref name="key"/> if it was made for what's current now.</summary>
    public bool TryTake(TKey key, object currentSettings, int currentLayout, out TItem item)
    {
        if (ReferenceEquals(_settings, currentSettings) && _layout == currentLayout && _items.Remove(key, out var found))
        {
            item = found;
            return true;
        }

        item = null!;
        return false;
    }

    /// <summary>Empty the pool; returns everything that was in it.</summary>
    public IReadOnlyList<TItem> TakeAll()
    {
        var all = _items.Values.ToList();
        _items.Clear();
        _settings = null;
        return all;
    }

    /// <summary>Empty the pool for good (app exit): later <see cref="Add"/> calls hand the item straight back.</summary>
    public IReadOnlyList<TItem> ShutDown()
    {
        _shutDown = true;
        return TakeAll();
    }
}
