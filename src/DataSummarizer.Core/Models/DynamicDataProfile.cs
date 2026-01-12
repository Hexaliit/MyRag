namespace Mostlylucid.DocSummarizer.Data.Models;

/// <summary>
/// Aggregates all signals from analysis waves into a unified data profile.
/// Provides type-safe access to signals and conversion to RAG-ready chunks.
/// </summary>
public class DynamicDataProfile
{
    private readonly Dictionary<string, List<DataSignal>> _signals = new();

    /// <summary>
    /// Source file path.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Time taken to profile.
    /// </summary>
    public TimeSpan ProfileTime { get; set; }

    /// <summary>
    /// Add a signal to the profile.
    /// </summary>
    public void AddSignal(DataSignal signal)
    {
        if (!_signals.ContainsKey(signal.Key))
        {
            _signals[signal.Key] = new List<DataSignal>();
        }
        _signals[signal.Key].Add(signal);
    }

    /// <summary>
    /// Add multiple signals.
    /// </summary>
    public void AddSignals(IEnumerable<DataSignal> signals)
    {
        foreach (var signal in signals)
        {
            AddSignal(signal);
        }
    }

    /// <summary>
    /// Get all signals for a key.
    /// </summary>
    public IEnumerable<DataSignal> GetSignals(string key)
    {
        return _signals.TryGetValue(key, out var signals) ? signals : Enumerable.Empty<DataSignal>();
    }

    /// <summary>
    /// Get the most confident signal for a key.
    /// </summary>
    public DataSignal? GetBestSignal(string key)
    {
        return GetSignals(key).OrderByDescending(s => s.Confidence).FirstOrDefault();
    }

    /// <summary>
    /// Get value from the most confident signal.
    /// </summary>
    public T? GetValue<T>(string key)
    {
        var signal = GetBestSignal(key);
        return signal?.Value is T value ? value : default;
    }

    /// <summary>
    /// Check if a signal exists.
    /// </summary>
    public bool HasSignal(string key)
    {
        return _signals.ContainsKey(key) && _signals[key].Count != 0;
    }

    /// <summary>
    /// Get all signals.
    /// </summary>
    public IEnumerable<DataSignal> GetAllSignals()
    {
        return _signals.Values.SelectMany(s => s);
    }

    /// <summary>
    /// Get signals by tag.
    /// </summary>
    public IEnumerable<DataSignal> GetSignalsByTag(string tag)
    {
        return GetAllSignals().Where(s => s.Tags?.Contains(tag) == true);
    }

    /// <summary>
    /// Get signals by source wave.
    /// </summary>
    public IEnumerable<DataSignal> GetSignalsBySource(string source)
    {
        return GetAllSignals().Where(s => s.Source == source);
    }

    /// <summary>
    /// Get all signal keys.
    /// </summary>
    public IEnumerable<string> GetSignalKeys()
    {
        return _signals.Keys;
    }

    /// <summary>
    /// Convert profile to dictionary for serialization.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var result = new Dictionary<string, object?>();

        foreach (var kvp in _signals)
        {
            var bestSignal = kvp.Value.OrderByDescending(s => s.Confidence).FirstOrDefault();
            if (bestSignal != null)
            {
                result[kvp.Key] = bestSignal.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// Convert profile to metadata dictionary suitable for ContentChunk.
    /// </summary>
    public Dictionary<string, object?> ToMetadata()
    {
        var metadata = ToDictionary();
        metadata["profile_time_ms"] = ProfileTime.TotalMilliseconds;
        metadata["source_path"] = SourcePath;
        return metadata;
    }

    /// <summary>
    /// Get a summary of the profile for RAG text representation.
    /// </summary>
    public string ToSummaryText()
    {
        var lines = new List<string>();

        // File identity
        var format = GetValue<string>(DataSignalKeys.Format);
        var rowCount = GetValue<long>(DataSignalKeys.RowCount);
        var columnCount = GetValue<int>(DataSignalKeys.ColumnCount);
        var columnNames = GetValue<string[]>(DataSignalKeys.ColumnNames);

        if (format != null)
            lines.Add($"Data file format: {format}");
        if (rowCount > 0)
            lines.Add($"Total rows: {rowCount:N0}");
        if (columnCount > 0)
            lines.Add($"Total columns: {columnCount}");
        if (columnNames != null && columnNames.Length > 0)
            lines.Add($"Columns: {string.Join(", ", columnNames)}");

        // Per-column summaries
        if (columnNames != null)
        {
            foreach (var col in columnNames)
            {
                var colLines = new List<string>();
                var colType = GetValue<string>(DataSignalKeys.SchemaType(col));
                if (colType != null)
                    colLines.Add($"type={colType}");

                var nullRate = GetBestSignal(DataSignalKeys.QualityNullRate(col));
                if (nullRate != null && nullRate.Value is double nr && nr > 0)
                    colLines.Add($"null_rate={nr:P1}");

                var cardinality = GetValue<int>(DataSignalKeys.ProfileCardinality(col));
                if (cardinality > 0)
                    colLines.Add($"cardinality={cardinality}");

                var mean = GetBestSignal(DataSignalKeys.StatsMean(col));
                if (mean != null && mean.Value is double m)
                    colLines.Add($"mean={m:F2}");

                var piiType = GetValue<string>(DataSignalKeys.PiiType(col));
                if (piiType != null)
                    colLines.Add($"pii={piiType}");

                if (colLines.Count > 0)
                    lines.Add($"  {col}: {string.Join(", ", colLines)}");
            }
        }

        // Quality alerts
        var duplicates = GetValue<int>(DataSignalKeys.DuplicateRows);
        if (duplicates > 0)
            lines.Add($"Warning: {duplicates} duplicate rows detected");

        return string.Join("\n", lines);
    }
}
