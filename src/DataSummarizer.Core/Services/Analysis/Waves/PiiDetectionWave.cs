using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
///     Ninth wave: detects Personally Identifiable Information (PII) in columns.
///     Uses regex patterns to identify emails, phones, SSNs, etc.
/// </summary>
public partial class PiiDetectionWave : IDataAnalysisWave
{
    private readonly ILogger<PiiDetectionWave>? _logger;

    public PiiDetectionWave(ILogger<PiiDetectionWave>? logger = null)
    {
        _logger = logger;
    }

    public string Name => "PiiDetectionWave";
    public int Priority => 40;
    public IReadOnlyList<string> Tags => [DataSignalTags.Pii];

    public bool ShouldRun(DataFile file, DataAnalysisContext context)
    {
        // Only run if enabled and we have text/categorical columns
        var textColumns = context.GetColumnsOfType("text")
            .Concat(context.GetColumnsOfType("categorical"))
            .ToList();

        return context.IsFeatureEnabled("pii") && textColumns.Count > 0;
    }

    public Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var samples = context.GetCached<List<Dictionary<string, object?>>>("sample.rows");

        if (samples == null || samples.Count == 0) return Task.FromResult<IEnumerable<DataSignal>>(signals);

        // Check text and categorical columns
        var candidateColumns = context.GetColumnsOfType("text")
            .Concat(context.GetColumnsOfType("categorical"))
            .ToList();

        // Also check columns based on name heuristics
        var allColumns = context.GetColumnNames();
        var piiNamePatterns = new[]
            { "email", "phone", "ssn", "social", "address", "name", "first", "last", "dob", "birth" };
        var nameMatches = allColumns.Where(c =>
            piiNamePatterns.Any(p => c.ToLowerInvariant().Contains(p)));

        candidateColumns = candidateColumns.Union(nameMatches).Distinct().ToList();

        foreach (var column in candidateColumns)
        {
            ct.ThrowIfCancellationRequested();

            var values = samples
                .Select(row => row.TryGetValue(column, out var v) ? v?.ToString() : null)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Take(100) // Check first 100 non-null values
                .ToList();

            if (values.Count == 0) continue;

            var (detected, piiType, confidence) = DetectPii(column, values!);

            signals.Add(new DataSignal
            {
                Key = DataSignalKeys.PiiDetected(column),
                Value = detected,
                Source = Name,
                Confidence = confidence,
                Tags = [DataSignalTags.Pii]
            });

            if (detected && piiType != null)
                signals.Add(new DataSignal
                {
                    Key = DataSignalKeys.PiiType(column),
                    Value = piiType,
                    Source = Name,
                    Confidence = confidence,
                    Tags = [DataSignalTags.Pii]
                });
        }

        _logger?.LogDebug("PiiDetectionWave: Checked {Count} columns for PII", candidateColumns.Count);

        return Task.FromResult<IEnumerable<DataSignal>>(signals);
    }

    private (bool detected, string? type, double confidence) DetectPii(string columnName, List<string> values)
    {
        var nameLower = columnName.ToLowerInvariant();

        // Check by column name first (high confidence)
        if (nameLower.Contains("email") || nameLower.Contains("e_mail"))
        {
            var emailMatches = values.Count(v => EmailRegex().IsMatch(v));
            if (emailMatches > values.Count * 0.5)
                return (true, "email", 0.95);
        }

        if (nameLower.Contains("phone") || nameLower.Contains("tel") || nameLower.Contains("mobile"))
        {
            var phoneMatches = values.Count(v => PhoneRegex().IsMatch(v));
            if (phoneMatches > values.Count * 0.3)
                return (true, "phone", 0.85);
        }

        if (nameLower.Contains("ssn") || nameLower.Contains("social"))
        {
            var ssnMatches = values.Count(v => SsnRegex().IsMatch(v));
            if (ssnMatches > values.Count * 0.3)
                return (true, "ssn", 0.9);
        }

        if (nameLower.Contains("address") || nameLower.Contains("street") || nameLower.Contains("city"))
            return (true, "address", 0.7);

        if (nameLower.Contains("first") && nameLower.Contains("name"))
            return (true, "first_name", 0.8);

        if (nameLower.Contains("last") && nameLower.Contains("name"))
            return (true, "last_name", 0.8);

        if (nameLower == "name" || nameLower.EndsWith("_name"))
            return (true, "name", 0.6);

        if (nameLower.Contains("dob") || nameLower.Contains("birth"))
            return (true, "date_of_birth", 0.85);

        // Check by pattern (lower confidence)
        var emailCount = values.Count(v => EmailRegex().IsMatch(v));
        if (emailCount > values.Count * 0.7)
            return (true, "email", 0.8);

        var phoneCount = values.Count(v => PhoneRegex().IsMatch(v));
        if (phoneCount > values.Count * 0.5)
            return (true, "phone", 0.7);

        var ssnCount = values.Count(v => SsnRegex().IsMatch(v));
        if (ssnCount > values.Count * 0.5)
            return (true, "ssn", 0.85);

        var creditCardCount = values.Count(v => CreditCardRegex().IsMatch(v));
        if (creditCardCount > values.Count * 0.3)
            return (true, "credit_card", 0.9);

        var ipCount = values.Count(v => IpAddressRegex().IsMatch(v));
        if (ipCount > values.Count * 0.5)
            return (true, "ip_address", 0.8);

        return (false, null, 0.0);
    }

    // Regex patterns for PII detection
    [GeneratedRegex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[\+]?[(]?[0-9]{1,3}[)]?[-\s\.]?[(]?[0-9]{1,4}[)]?[-\s\.]?[0-9]{1,4}[-\s\.]?[0-9]{1,9}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^\d{3}-\d{2}-\d{4}$")]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|3[47][0-9]{13}|6(?:011|5[0-9]{2})[0-9]{12})$")]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$")]
    private static partial Regex IpAddressRegex();
}