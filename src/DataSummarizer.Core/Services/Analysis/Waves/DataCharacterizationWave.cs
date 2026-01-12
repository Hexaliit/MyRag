using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services.Analysis.Waves;

/// <summary>
/// Tenth wave: characterizes the data domain for analytics routing.
/// Detects if data is: sales, marketing, user/customer, logs, financial, inventory, etc.
/// </summary>
public class DataCharacterizationWave : IDataAnalysisWave
{
    private readonly ILogger<DataCharacterizationWave>? _logger;

    public DataCharacterizationWave(ILogger<DataCharacterizationWave>? logger = null)
    {
        _logger = logger;
    }

    public string Name => "DataCharacterizationWave";
    public int Priority => 30; // Run after most analysis is done
    public IReadOnlyList<string> Tags => ["characterization", "routing"];

    public Task<IEnumerable<DataSignal>> AnalyzeAsync(
        DataFile file,
        DataAnalysisContext context,
        CancellationToken ct = default)
    {
        var signals = new List<DataSignal>();
        var columns = context.GetColumnNames();
        var columnTypes = columns.ToDictionary(c => c, c => context.GetColumnType(c) ?? "unknown");

        // Detect data domain based on column names and types
        var domain = DetectDataDomain(columns, columnTypes);
        signals.Add(CreateSignal("characterization.domain", domain.domain, domain.confidence));

        // Detect data shape
        var shape = DetectDataShape(context);
        signals.Add(CreateSignal("characterization.shape", shape));

        // Detect temporal nature
        var hasTemporal = columns.Any(c =>
            context.GetColumnType(c) == "datetime" ||
            c.ToLowerInvariant().Contains("date") ||
            c.ToLowerInvariant().Contains("time"));
        signals.Add(CreateSignal("characterization.has_temporal", hasTemporal));

        // Detect if transactional
        var isTransactional = IsTransactionalData(columns, columnTypes);
        signals.Add(CreateSignal("characterization.is_transactional", isTransactional));

        // Suggest analytics types
        var analyticsTypes = SuggestAnalyticsTypes(domain.domain, hasTemporal, isTransactional, columnTypes);
        signals.Add(CreateSignal("characterization.suggested_analytics", analyticsTypes));

        // Detect audience segmentation potential
        var segmentationPotential = DetectSegmentationPotential(columns, columnTypes, context);
        signals.Add(CreateSignal("characterization.segmentation_potential", segmentationPotential));

        _logger?.LogDebug("DataCharacterizationWave: Domain={Domain}, Shape={Shape}, Analytics={Analytics}",
            domain.domain, shape, string.Join(",", analyticsTypes));

        return Task.FromResult<IEnumerable<DataSignal>>(signals);
    }

    private (string domain, double confidence) DetectDataDomain(
        IReadOnlyList<string> columns,
        Dictionary<string, string> columnTypes)
    {
        var columnsLower = columns.Select(c => c.ToLowerInvariant()).ToList();
        var scores = new Dictionary<string, double>();

        // Sales data indicators
        var salesIndicators = new[] { "order", "sale", "revenue", "invoice", "product", "quantity", "price", "discount", "tax", "total", "customer" };
        scores["sales"] = CalculateIndicatorScore(columnsLower, salesIndicators);

        // Marketing data indicators
        var marketingIndicators = new[] { "campaign", "click", "impression", "conversion", "channel", "source", "medium", "utm", "lead", "prospect" };
        scores["marketing"] = CalculateIndicatorScore(columnsLower, marketingIndicators);

        // User/Customer data indicators
        var userIndicators = new[] { "user", "customer", "member", "account", "profile", "email", "phone", "address", "signup", "registered" };
        scores["user_data"] = CalculateIndicatorScore(columnsLower, userIndicators);

        // Log data indicators
        var logIndicators = new[] { "log", "timestamp", "level", "message", "error", "warning", "info", "debug", "trace", "ip", "request", "response", "status_code" };
        scores["logs"] = CalculateIndicatorScore(columnsLower, logIndicators);

        // Financial data indicators
        var financialIndicators = new[] { "amount", "balance", "credit", "debit", "transaction", "payment", "fee", "interest", "currency", "exchange" };
        scores["financial"] = CalculateIndicatorScore(columnsLower, financialIndicators);

        // Inventory data indicators
        var inventoryIndicators = new[] { "sku", "stock", "inventory", "warehouse", "location", "shelf", "bin", "reorder", "supplier" };
        scores["inventory"] = CalculateIndicatorScore(columnsLower, inventoryIndicators);

        // IoT/Sensor data indicators
        var iotIndicators = new[] { "sensor", "device", "temperature", "humidity", "pressure", "reading", "measurement", "lat", "lon", "gps" };
        scores["iot"] = CalculateIndicatorScore(columnsLower, iotIndicators);

        // HR data indicators
        var hrIndicators = new[] { "employee", "department", "salary", "hire", "position", "title", "manager", "performance", "review" };
        scores["hr"] = CalculateIndicatorScore(columnsLower, hrIndicators);

        // Find highest scoring domain
        var best = scores.OrderByDescending(kv => kv.Value).First();

        // If no strong signal, return "general"
        if (best.Value < 0.2)
            return ("general", 0.5);

        return (best.Key, Math.Min(best.Value * 2, 1.0)); // Scale confidence
    }

    private static double CalculateIndicatorScore(List<string> columns, string[] indicators)
    {
        var matches = columns.Count(c => indicators.Any(i => c.Contains(i)));
        return (double)matches / Math.Max(columns.Count, 1);
    }

    private static string DetectDataShape(DataAnalysisContext context)
    {
        var rowCount = context.GetRowCount();
        var colCount = context.GetColumnNames().Count;

        if (colCount == 0) return "empty";
        if (rowCount == 0) return "empty";

        var ratio = (double)rowCount / colCount;

        return ratio switch
        {
            > 1000 => "tall", // Many rows, few columns (typical transactional)
            > 10 => "balanced",
            > 1 => "wide", // Few rows, many columns (typical for pivoted/aggregated)
            _ => "very_wide"
        };
    }

    private static bool IsTransactionalData(
        IReadOnlyList<string> columns,
        Dictionary<string, string> columnTypes)
    {
        var columnsLower = columns.Select(c => c.ToLowerInvariant()).ToList();

        // Check for transaction-like columns
        var hasId = columnsLower.Any(c => c.EndsWith("_id") || c.EndsWith("id") || c == "id");
        var hasDate = columnTypes.Values.Any(t => t == "datetime");
        var hasAmount = columnsLower.Any(c => c.Contains("amount") || c.Contains("total") || c.Contains("price"));

        return hasId && hasDate && hasAmount;
    }

    private static List<string> SuggestAnalyticsTypes(
        string domain,
        bool hasTemporal,
        bool isTransactional,
        Dictionary<string, string> columnTypes)
    {
        var suggestions = new List<string>();

        // Based on domain
        switch (domain)
        {
            case "sales":
                suggestions.Add("revenue_analysis");
                suggestions.Add("product_performance");
                suggestions.Add("customer_segmentation");
                break;
            case "marketing":
                suggestions.Add("campaign_performance");
                suggestions.Add("conversion_funnel");
                suggestions.Add("attribution_analysis");
                break;
            case "user_data":
                suggestions.Add("user_segmentation");
                suggestions.Add("cohort_analysis");
                suggestions.Add("retention_analysis");
                break;
            case "logs":
                suggestions.Add("error_analysis");
                suggestions.Add("performance_monitoring");
                suggestions.Add("anomaly_detection");
                break;
            case "financial":
                suggestions.Add("trend_analysis");
                suggestions.Add("forecasting");
                suggestions.Add("risk_assessment");
                break;
            case "inventory":
                suggestions.Add("stock_optimization");
                suggestions.Add("demand_forecasting");
                suggestions.Add("reorder_analysis");
                break;
        }

        // Based on data characteristics
        if (hasTemporal)
        {
            suggestions.Add("time_series_analysis");
            suggestions.Add("trend_detection");
        }

        if (isTransactional)
        {
            suggestions.Add("transaction_analysis");
        }

        var numericCount = columnTypes.Count(kv => kv.Value == "numeric");
        if (numericCount >= 3)
        {
            suggestions.Add("correlation_analysis");
        }

        return suggestions.Distinct().ToList();
    }

    private static string DetectSegmentationPotential(
        IReadOnlyList<string> columns,
        Dictionary<string, string> columnTypes,
        DataAnalysisContext context)
    {
        // Check for categorical columns that could be segments
        var categoricalColumns = columnTypes.Where(kv => kv.Value == "categorical").Select(kv => kv.Key).ToList();

        // Check cardinality for categorical columns
        var goodSegmentColumns = categoricalColumns.Where(col =>
        {
            var cardinality = context.GetValue<int>(DataSignalKeys.ProfileCardinality(col));
            return cardinality >= 2 && cardinality <= 50; // Good range for segmentation
        }).ToList();

        if (goodSegmentColumns.Count == 0)
            return "low";
        if (goodSegmentColumns.Count <= 2)
            return "medium";
        return "high";
    }

    private DataSignal CreateSignal(string key, object? value, double confidence = 1.0)
    {
        return new DataSignal
        {
            Key = key,
            Value = value,
            Source = Name,
            Confidence = confidence,
            Tags = ["characterization", "routing"]
        };
    }
}
