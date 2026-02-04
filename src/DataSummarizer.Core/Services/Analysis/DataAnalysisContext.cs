using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis;

/// <summary>
///     Shared context passed between analysis waves.
///     Allows waves to access results from higher-priority waves.
/// </summary>
public class DataAnalysisContext
{
    private readonly Dictionary<string, object> _cache = new();
    private readonly Dictionary<string, List<DataSignal>> _signals = new();

    /// <summary>
    ///     Add a signal to the context.
    /// </summary>
    public void AddSignal(DataSignal signal)
    {
        if (!_signals.ContainsKey(signal.Key)) _signals[signal.Key] = new List<DataSignal>();
        _signals[signal.Key].Add(signal);
    }

    /// <summary>
    ///     Add multiple signals to the context.
    /// </summary>
    public void AddSignals(IEnumerable<DataSignal> signals)
    {
        foreach (var signal in signals) AddSignal(signal);
    }

    /// <summary>
    ///     Get all signals for a given key.
    /// </summary>
    public IEnumerable<DataSignal> GetSignals(string key)
    {
        return _signals.TryGetValue(key, out var signals) ? signals : Enumerable.Empty<DataSignal>();
    }

    /// <summary>
    ///     Get the most confident signal for a key.
    /// </summary>
    public DataSignal? GetBestSignal(string key)
    {
        return GetSignals(key).OrderByDescending(s => s.Confidence).FirstOrDefault();
    }

    /// <summary>
    ///     Get value from the most confident signal.
    /// </summary>
    public T? GetValue<T>(string key)
    {
        var signal = GetBestSignal(key);
        return signal?.Value is T value ? value : default;
    }

    /// <summary>
    ///     Check if a signal exists for a key.
    /// </summary>
    public bool HasSignal(string key)
    {
        return _signals.ContainsKey(key) && _signals[key].Count != 0;
    }

    /// <summary>
    ///     Get all signals.
    /// </summary>
    public IEnumerable<DataSignal> GetAllSignals()
    {
        return _signals.Values.SelectMany(s => s);
    }

    /// <summary>
    ///     Get signals by tag.
    /// </summary>
    public IEnumerable<DataSignal> GetSignalsByTag(string tag)
    {
        return GetAllSignals().Where(s => s.Tags?.Contains(tag) == true);
    }

    /// <summary>
    ///     Get signals by source wave.
    /// </summary>
    public IEnumerable<DataSignal> GetSignalsBySource(string source)
    {
        return GetAllSignals().Where(s => s.Source == source);
    }

    /// <summary>
    ///     Cache arbitrary data for sharing between waves.
    /// </summary>
    public void SetCached<T>(string key, T value)
    {
        _cache[key] = value!;
    }

    /// <summary>
    ///     Retrieve cached data.
    /// </summary>
    public T? GetCached<T>(string key)
    {
        return _cache.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    /// <summary>
    ///     Clear all cached data (useful for freeing memory after analysis).
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    ///     Get column names from identity wave.
    /// </summary>
    public IReadOnlyList<string> GetColumnNames()
    {
        return GetValue<string[]>(DataSignalKeys.ColumnNames) ?? Array.Empty<string>();
    }

    /// <summary>
    ///     Get row count from identity wave.
    /// </summary>
    public long GetRowCount()
    {
        return GetValue<long>(DataSignalKeys.RowCount);
    }

    /// <summary>
    ///     Get the inferred type for a column.
    /// </summary>
    public string? GetColumnType(string column)
    {
        return GetValue<string>(DataSignalKeys.SchemaType(column));
    }

    /// <summary>
    ///     Get columns that have a specific inferred type.
    /// </summary>
    public IEnumerable<string> GetColumnsOfType(string type)
    {
        return GetColumnNames().Where(col => GetColumnType(col) == type);
    }

    /// <summary>
    ///     Check if analysis is enabled for a specific feature.
    /// </summary>
    public bool IsFeatureEnabled(string feature)
    {
        return GetValue<bool>($"config.enable.{feature}");
    }

    /// <summary>
    ///     Set feature enablement state.
    /// </summary>
    public void SetFeatureEnabled(string feature, bool enabled)
    {
        AddSignal(new DataSignal
        {
            Key = $"config.enable.{feature}",
            Value = enabled,
            Source = "Config",
            Confidence = 1.0
        });
    }
}