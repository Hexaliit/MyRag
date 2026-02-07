using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Models;
using DomainClassifier.Core.Services.Onnx;
using DomainClassifier.Financial.Extractors;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Financial;

/// <summary>
///     Financial domain plugin. Phase 1: keyword classification + regex entity extraction.
///     Phase 2 (future): FinancialBERT-Sentiment ONNX model for sentiment analysis.
/// </summary>
public class FinancialDomainPlugin : IDomainPlugin
{
    private readonly IOnnxClassifierService _classifierService;
    private readonly ILogger<FinancialDomainPlugin> _logger;

    public FinancialDomainPlugin(
        IOnnxClassifierService classifierService,
        ILogger<FinancialDomainPlugin> logger)
    {
        _classifierService = classifierService;
        _logger = logger;
    }

    public string DomainId => "financial";
    public string Name => "Financial Domain";

    public IReadOnlyList<string> EntityTypes =>
    [
        "ticker", "amount", "currency", "financial_date",
        "instrument", "percentage"
    ];

    public Task<DomainClassification> ClassifyAsync(
        string text,
        CancellationToken ct = default)
    {
        // Phase 1: keyword-based classification (fast, no model needed)
        var result = FinancialKeywordClassifier.Classify(text);
        return Task.FromResult(result);
    }

    public async Task<DomainEnrichmentResult> EnrichAsync(
        IReadOnlyList<ContentChunk> chunks,
        CancellationToken ct = default)
    {
        var allEntities = new List<DomainEntity>();
        var signals = new List<DomainSignal>();

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            // Extract entities using regex-based extractors (Phase 1)
            allEntities.AddRange(TickerExtractor.Extract(chunk.Text));
            allEntities.AddRange(AmountExtractor.ExtractAmounts(chunk.Text));
            allEntities.AddRange(AmountExtractor.ExtractPercentages(chunk.Text));
            allEntities.AddRange(FinancialDateExtractor.Extract(chunk.Text));
        }

        // Phase 2: Attempt ONNX sentiment analysis (optional, may fail if model not available)
        await TryAddSentimentSignalsAsync(chunks, signals, ct);

        // Compute aggregate signals
        var uniqueTickers = allEntities
            .Where(e => e.EntityType == "ticker")
            .Select(e => e.Text)
            .Distinct()
            .ToList();

        if (uniqueTickers.Count > 0)
            signals.Add(new DomainSignal(
                "financial.tickers_mentioned",
                uniqueTickers,
                0.95,
                DomainId));

        // Guess document sub-type from entity patterns
        var docSubtype = InferDocumentSubtype(allEntities, chunks);
        if (docSubtype != null)
            signals.Add(new DomainSignal(
                "financial.document_subtype",
                docSubtype,
                0.7,
                DomainId));

        var classification = FinancialKeywordClassifier.Classify(
            string.Join("\n", chunks.Take(3).Select(c => c.Text)));

        _logger.LogDebug(
            "Financial enrichment: {EntityCount} entities, {SignalCount} signals from {ChunkCount} chunks",
            allEntities.Count, signals.Count, chunks.Count);

        return new DomainEnrichmentResult(
            Classifications: [classification],
            Entities: allEntities,
            Signals: signals);
    }

    private async Task TryAddSentimentSignalsAsync(
        IReadOnlyList<ContentChunk> chunks,
        List<DomainSignal> signals,
        CancellationToken ct)
    {
        try
        {
            var model = ClassifierModelRegistry.FinancialBertSentiment;

            // Only attempt if the model is already initialized (don't force download during enrichment)
            var sampleText = string.Join(" ", chunks.Take(3).Select(c =>
                c.Text.Length > 200 ? c.Text[..200] : c.Text));

            var result = await _classifierService.ClassifyAsync(model, sampleText, ct);

            signals.Add(new DomainSignal(
                "financial.sentiment",
                result.Label,
                result.Confidence,
                DomainId));

            signals.Add(new DomainSignal(
                "financial.sentiment_scores",
                result.AllScores,
                result.Confidence,
                DomainId));
        }
        catch (Exception ex)
        {
            // Sentiment analysis is optional - don't fail enrichment if model not available
            _logger.LogDebug(ex,
                "Financial sentiment analysis unavailable (ONNX model not downloaded). " +
                "Continuing with regex-based enrichment only");
        }
    }

    private static string? InferDocumentSubtype(
        List<DomainEntity> entities,
        IReadOnlyList<ContentChunk> chunks)
    {
        var text = string.Join(" ", chunks.Take(5).Select(c => c.Text)).ToLowerInvariant();

        if (text.Contains("10-k") || text.Contains("annual report"))
            return "annual_report";
        if (text.Contains("10-q") || text.Contains("quarterly report"))
            return "quarterly_report";
        if (text.Contains("earnings call") || text.Contains("conference call"))
            return "earnings_call";
        if (text.Contains("prospectus") || text.Contains("s-1"))
            return "prospectus";
        if (text.Contains("press release") || text.Contains("8-k"))
            return "press_release";

        var hasMultipleTickers = entities.Count(e => e.EntityType == "ticker") > 3;
        var hasAmounts = entities.Any(e => e.EntityType == "amount");

        if (hasMultipleTickers && hasAmounts)
            return "financial_analysis";
        if (hasAmounts)
            return "financial_document";

        return null;
    }
}
