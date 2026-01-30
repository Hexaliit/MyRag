using System.Collections.Concurrent;

namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
/// Thread-safe signal accumulator with query support.
/// </summary>
public sealed class SignalBag
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<Signal>> _byKey = new();
    private readonly ConcurrentBag<Signal> _all = new();

    public void Add(Signal signal)
    {
        _all.Add(signal);
        _byKey.GetOrAdd(signal.Key, _ => new ConcurrentBag<Signal>()).Add(signal);
    }

    public void AddRange(IEnumerable<Signal> signals)
    {
        foreach (var signal in signals)
            Add(signal);
    }

    /// <summary>
    /// Get the highest-confidence signal for a key.
    /// </summary>
    public Signal? Get(string key) =>
        _byKey.TryGetValue(key, out var bag)
            ? bag.OrderByDescending(s => s.Confidence).FirstOrDefault()
            : null;

    public T? GetValue<T>(string key)
    {
        var signal = Get(key);
        return signal?.Value is T typed ? typed : default;
    }

    public bool Has(string key) =>
        _byKey.TryGetValue(key, out var bag) && !bag.IsEmpty;

    public IReadOnlyList<Signal> GetByPrefix(string prefix)
    {
        var dotPrefix = prefix.EndsWith('*') ? prefix[..^1] : prefix + ".";
        return _byKey
            .Where(kv => kv.Key.StartsWith(dotPrefix, StringComparison.Ordinal)
                         || kv.Key.Equals(prefix.TrimEnd('*', '.'), StringComparison.Ordinal))
            .SelectMany(kv => kv.Value)
            .ToList();
    }

    public IReadOnlyList<Signal> GetAll(string key) =>
        _byKey.TryGetValue(key, out var bag) ? bag.ToList() : [];

    public IReadOnlyList<Signal> All => _all.ToList();
    public int Count => _all.Count;

    public IReadOnlyList<Signal> GetByTag(string tag) =>
        _all.Where(s => s.Tags?.Contains(tag) == true).ToList();
}
