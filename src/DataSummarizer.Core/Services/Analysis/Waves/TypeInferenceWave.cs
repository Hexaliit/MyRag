using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
/// Third wave: infers semantic data types for each column.
/// Types: numeric, categorical, datetime, text, id, boolean
/// </summary>
public partial class TypeInferenceWave : IDataAnalysisWave
{
    private readonly ILogger<TypeInferenceWave>? _logger;

    // Threshold for categorical vs text (if distinct values / total < threshold, it's categorical)
    private const double CategoricalThreshold = 0.5;

    // Minimum distinct values to consider as ID
    private const int IdMinDistinct = 100;

    public TypeInferenceWave(ILogger<TypeInferenceWave>? logger = null)
    {
        _logger = logger;
    }

    public string Name => "TypeInferenceWave";
    public int Priority => 95;
    public IReadOnlyList<string> Tags => [DataSignalTags.Schema];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Need column names from identity wave
        return context.GetColumnNames().Count > 0;
    }

    public Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var columns = context.GetColumnNames();
        var samples = context.GetCached<List<Dictionary<string, object?>>>("sample.rows");

        if (samples == null || samples.Count == 0)
        {
            _logger?.LogWarning("TypeInferenceWave: No sample data available");
            return Task.FromResult<IEnumerable<DataSignal>>(signals);
        }

        foreach (var column in columns)
        {
            ct.ThrowIfCancellationRequested();

            var values = samples
                .Select(row => row.TryGetValue(column, out var v) ? v : null)
                .Where(v => v != null)
                .ToList();

            if (values.Count == 0)
            {
                signals.Add(CreateSignal(DataSignalKeys.SchemaType(column), "unknown", 0.5));
                continue;
            }

            var (type, confidence) = InferType(column, values);
            signals.Add(CreateSignal(DataSignalKeys.SchemaType(column), type, confidence));

            // Check if nullable
            var nullCount = samples.Count - values.Count;
            var nullable = nullCount > 0;
            signals.Add(CreateSignal(DataSignalKeys.SchemaNullable(column), nullable, 1.0));
        }

        _logger?.LogDebug("TypeInferenceWave: Inferred types for {Count} columns", columns.Count);

        return Task.FromResult<IEnumerable<DataSignal>>(signals);
    }

    private (string type, double confidence) InferType(string columnName, List<object?> values)
    {
        // Check for boolean first (fastest)
        if (IsBooleanColumn(values))
            return ("boolean", 0.95);

        // Check for numeric
        var numericCount = values.Count(IsNumeric);
        var numericRatio = (double)numericCount / values.Count;
        if (numericRatio > 0.9)
            return ("numeric", numericRatio);

        // Check for datetime
        var dateCount = values.Count(IsDateTime);
        var dateRatio = (double)dateCount / values.Count;
        if (dateRatio > 0.8)
            return ("datetime", dateRatio);

        // Check for ID-like columns (name heuristic + high cardinality)
        if (IsIdLikeColumn(columnName, values))
            return ("id", 0.85);

        // Check cardinality for categorical vs text
        var distinctValues = values.Select(v => v?.ToString()).Distinct().Count();
        var cardinalityRatio = (double)distinctValues / values.Count;

        if (cardinalityRatio < CategoricalThreshold)
            return ("categorical", 1.0 - cardinalityRatio);

        // Default to text
        return ("text", 0.7);
    }

    private static bool IsBooleanColumn(List<object?> values)
    {
        var boolValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "yes", "no", "1", "0", "y", "n", "t", "f"
        };

        return values.All(v => v != null && boolValues.Contains(v.ToString()!));
    }

    private static bool IsNumeric(object? value)
    {
        if (value == null) return false;

        return value switch
        {
            int or long or float or double or decimal => true,
            string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _),
            _ => false
        };
    }

    private static bool IsDateTime(object? value)
    {
        if (value == null) return false;

        return value switch
        {
            DateTime => true,
            DateTimeOffset => true,
            string s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                        DateTimeRegex().IsMatch(s),
            _ => false
        };
    }

    private static bool IsIdLikeColumn(string columnName, List<object?> values)
    {
        // Check column name for ID patterns
        var nameLower = columnName.ToLowerInvariant();
        var idPatterns = new[] { "id", "_id", "key", "uuid", "guid", "code", "number", "no", "num" };

        if (idPatterns.Any(p => nameLower.Contains(p) || nameLower.EndsWith(p)))
        {
            // Verify high cardinality
            var distinctCount = values.Select(v => v?.ToString()).Distinct().Count();
            return distinctCount >= Math.Min(IdMinDistinct, values.Count * 0.9);
        }

        return false;
    }

    private DataSignal CreateSignal(string key, object? value, double confidence)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = confidence,
            Tags = [DataSignalTags.Schema]
        };
    }

    // Regex for common date formats
    [GeneratedRegex(@"^\d{4}[-/]\d{2}[-/]\d{2}|\d{2}[-/]\d{2}[-/]\d{4}")]
    private static partial Regex DateTimeRegex();
}
