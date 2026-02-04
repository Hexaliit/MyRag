namespace Mostlylucid.Summarizer.Core.Analysis;

/// <summary>
///     Shared ephemeral context passed between analysis waves.
///     Allows waves to read results from higher-priority waves and share cached data.
///     Created per analysis run, discarded after.
/// </summary>
public class AnalysisContext
{
    private readonly Dictionary<string, object> _cache = new();
    private readonly Dictionary<string, List<Signal>> _signals = new();
    private readonly Dictionary<string, bool> _skippedWaves = new();

    /// <summary>
    ///     Route selection for adaptive analysis (fast/balanced/quality).
    /// </summary>
    public string? SelectedRoute { get; set; }

    /// <summary>
    ///     Check if we're in fast mode (skip expensive operations).
    /// </summary>
    public bool IsFastRoute => SelectedRoute == "fast";

    /// <summary>
    ///     Check if we're in quality mode (run everything).
    /// </summary>
    public bool IsQualityRoute => SelectedRoute == "quality";

    /// <summary>
    ///     Add a signal to the context.
    /// </summary>
    public void AddSignal(Signal signal)
    {
        if (!_signals.TryGetValue(signal.Key, out var list))
        {
            list = new List<Signal>();
            _signals[signal.Key] = list;
        }

        list.Add(signal);
    }

    /// <summary>
    ///     Add multiple signals.
    /// </summary>
    public void AddSignals(IEnumerable<Signal> signals)
    {
        foreach (var signal in signals)
            AddSignal(signal);
    }

    /// <summary>
    ///     Get all signals for a given key.
    /// </summary>
    public IEnumerable<Signal> GetSignals(string key)
    {
        return _signals.TryGetValue(key, out var signals) ? signals : [];
    }

    /// <summary>
    ///     Get the most confident signal for a key.
    /// </summary>
    public Signal? GetBestSignal(string key)
    {
        return GetSignals(key).OrderByDescending(s => s.Confidence).FirstOrDefault();
    }

    /// <summary>
    ///     Get typed value from the most confident signal.
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
        return _signals.TryGetValue(key, out var list) && list.Count > 0;
    }

    /// <summary>
    ///     Get all signals.
    /// </summary>
    public IEnumerable<Signal> GetAllSignals()
    {
        return _signals.Values.SelectMany(s => s);
    }

    /// <summary>
    ///     Get all signals with a specific tag.
    /// </summary>
    public IEnumerable<Signal> GetSignalsByTag(string tag)
    {
        return GetAllSignals().Where(s => s.Tags?.Contains(tag) == true);
    }

    /// <summary>
    ///     Cache arbitrary data for sharing between waves.
    /// </summary>
    public void SetCached<T>(string key, T value) where T : notnull
    {
        _cache[key] = value;
    }

    /// <summary>
    ///     Retrieve cached data.
    /// </summary>
    public T? GetCached<T>(string key)
    {
        return _cache.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    /// <summary>
    ///     Check if a key is cached.
    /// </summary>
    public bool HasCached(string key)
    {
        return _cache.ContainsKey(key);
    }

    /// <summary>
    ///     Clear all cached data.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    ///     Mark a wave as skipped by routing.
    /// </summary>
    public void SkipWave(string waveName)
    {
        _skippedWaves[waveName] = true;
    }

    /// <summary>
    ///     Check if a wave is skipped.
    /// </summary>
    public bool IsWaveSkipped(string waveName)
    {
        return _skippedWaves.TryGetValue(waveName, out var skipped) && skipped;
    }

    /// <summary>
    ///     Aggregate signals for a key using the specified strategy.
    /// </summary>
    public Signal? Aggregate(string key, AggregationStrategy strategy)
    {
        var signals = GetSignals(key).ToList();
        if (signals.Count == 0) return null;

        return strategy switch
        {
            AggregationStrategy.HighestConfidence => signals.OrderByDescending(s => s.Confidence).First(),
            AggregationStrategy.MostRecent => signals.OrderByDescending(s => s.Timestamp).First(),
            AggregationStrategy.WeightedAverage => AggregateWeightedAverage(signals),
            AggregationStrategy.Collect => new Signal
            {
                Key = key,
                Value = signals.Select(s => s.Value).ToList(),
                Confidence = signals.Average(s => s.Confidence),
                Source = "aggregated",
                Tags = signals.SelectMany(s => s.Tags ?? []).Distinct().ToList()
            },
            _ => signals.First()
        };
    }

    private static Signal AggregateWeightedAverage(List<Signal> signals)
    {
        var numericSignals = signals.Where(s => s.Value is double or float or int or long).ToList();
        if (numericSignals.Count == 0)
            return signals.OrderByDescending(s => s.Confidence).First();

        var totalWeight = numericSignals.Sum(s => s.Confidence);
        var weightedSum = numericSignals.Sum(s => Convert.ToDouble(s.Value) * s.Confidence);

        return new Signal
        {
            Key = signals[0].Key,
            Value = totalWeight > 0 ? weightedSum / totalWeight : 0,
            Confidence = numericSignals.Average(s => s.Confidence),
            Source = "aggregated_weighted",
            Tags = signals.SelectMany(s => s.Tags ?? []).Distinct().ToList()
        };
    }
}