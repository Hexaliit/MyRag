namespace Mostlylucid.DocSummarizer.Data.Models;

/// <summary>
///     Represents a single analytical signal contributed by a data analysis wave.
///     Signals are atomic units of information with confidence and provenance.
/// </summary>
public record DataSignal
{
    /// <summary>
    ///     Unique key identifying what this signal measures (e.g., "identity.row_count", "stats.column.mean").
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    ///     The measured value. Can be any serializable type.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    ///     Confidence score (0.0 - 1.0) indicating reliability of this signal.
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    ///     Source analyzer that produced this signal (e.g., "IdentityWave", "StatisticsWave").
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    ///     When this signal was generated.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     Optional metadata about how this signal was computed.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    ///     Data type of the value for serialization/deserialization.
    /// </summary>
    public string? ValueType { get; init; }

    /// <summary>
    ///     Optional tags for categorization (e.g., "identity", "quality", "statistics").
    /// </summary>
    public List<string>? Tags { get; init; }
}

/// <summary>
///     Aggregation strategy for combining multiple signals for the same key.
/// </summary>
public enum SignalAggregationStrategy
{
    /// <summary>
    ///     Take the signal with highest confidence.
    /// </summary>
    HighestConfidence,

    /// <summary>
    ///     Take the most recent signal.
    /// </summary>
    MostRecent,

    /// <summary>
    ///     Average numeric values weighted by confidence.
    /// </summary>
    WeightedAverage,

    /// <summary>
    ///     Majority vote for categorical values.
    /// </summary>
    MajorityVote,

    /// <summary>
    ///     Merge all signals into a collection.
    /// </summary>
    Collect
}

/// <summary>
///     Tags for categorizing data signals.
/// </summary>
public static class DataSignalTags
{
    public const string Identity = "identity";
    public const string Schema = "schema";
    public const string Profile = "profile";
    public const string Quality = "quality";
    public const string Statistics = "statistics";
    public const string Outlier = "outlier";
    public const string Correlation = "correlation";
    public const string Pii = "pii";
    public const string Sample = "sample";
}

/// <summary>
///     Well-known signal keys for data analysis.
/// </summary>
public static class DataSignalKeys
{
    // Identity signals
    public const string Format = "identity.format";
    public const string SizeBytes = "identity.size_bytes";
    public const string RowCount = "identity.row_count";
    public const string ColumnCount = "identity.column_count";
    public const string ColumnNames = "identity.column_names";

    // Sample signals
    public const string SampleRows = "sample.rows";
    public const string SampleSize = "sample.size";

    // Quality signals
    public const string DuplicateRows = "quality.duplicate_rows";

    // Schema pattern for per-column signals
    public static string SchemaType(string column)
    {
        return $"schema.{column}.type";
    }

    public static string SchemaNullable(string column)
    {
        return $"schema.{column}.nullable";
    }

    // Profile pattern for per-column signals
    public static string ProfileCardinality(string column)
    {
        return $"profile.{column}.cardinality";
    }

    public static string ProfileTopValues(string column)
    {
        return $"profile.{column}.top_values";
    }

    public static string ProfileDistribution(string column)
    {
        return $"profile.{column}.distribution";
    }

    // Quality pattern for per-column signals
    public static string QualityNullRate(string column)
    {
        return $"quality.{column}.null_rate";
    }

    public static string QualityCompleteness(string column)
    {
        return $"quality.{column}.completeness";
    }

    // Statistics pattern for per-column signals
    public static string StatsMin(string column)
    {
        return $"stats.{column}.min";
    }

    public static string StatsMax(string column)
    {
        return $"stats.{column}.max";
    }

    public static string StatsMean(string column)
    {
        return $"stats.{column}.mean";
    }

    public static string StatsStdDev(string column)
    {
        return $"stats.{column}.std_dev";
    }

    public static string StatsQuartiles(string column)
    {
        return $"stats.{column}.quartiles";
    }

    // Outlier pattern for per-column signals
    public static string OutlierCount(string column)
    {
        return $"outlier.{column}.count";
    }

    public static string OutlierIqrBounds(string column)
    {
        return $"outlier.{column}.iqr_bounds";
    }

    // Correlation pattern
    public static string Correlation(string col1, string col2)
    {
        return $"correlation.{col1}_{col2}";
    }

    // PII pattern for per-column signals
    public static string PiiDetected(string column)
    {
        return $"pii.{column}.detected";
    }

    public static string PiiType(string column)
    {
        return $"pii.{column}.type";
    }
}