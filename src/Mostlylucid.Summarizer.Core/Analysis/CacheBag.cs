using System.Collections.Concurrent;

namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
/// Thread-safe object cache shared between waves during a coordinator run.
/// </summary>
public sealed class CacheBag
{
    private readonly ConcurrentDictionary<string, object> _cache = new();

    public void Set<T>(string key, T value) where T : notnull => _cache[key] = value;

    public T? Get<T>(string key) =>
        _cache.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public bool Has(string key) => _cache.ContainsKey(key);

    public bool TryGet<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    public T GetOrCreate<T>(string key, Func<T> factory) where T : notnull =>
        (T)_cache.GetOrAdd(key, _ => factory());

    public bool Remove(string key) => _cache.TryRemove(key, out _);

    public IReadOnlyList<string> Keys => _cache.Keys.ToList();
}
