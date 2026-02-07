using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Core.Services;

/// <summary>
///     Auto-discovers domain plugins from DI and dispatches classification/enrichment.
/// </summary>
public class DomainPluginRegistry : IDomainPluginRegistry
{
    private readonly ILogger<DomainPluginRegistry> _logger;
    private readonly Dictionary<string, IDomainPlugin> _byId;
    private readonly List<IDomainPlugin> _plugins;

    public DomainPluginRegistry(
        IEnumerable<IDomainPlugin> plugins,
        ILogger<DomainPluginRegistry> logger)
    {
        _logger = logger;
        _plugins = plugins.ToList();
        _byId = new Dictionary<string, IDomainPlugin>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in _plugins)
        {
            if (_byId.TryAdd(plugin.DomainId, plugin))
            {
                _logger.LogDebug("Registered domain plugin: {DomainId} ({Name})",
                    plugin.DomainId, plugin.Name);
            }
            else
            {
                _logger.LogWarning("Duplicate domain plugin ID: {DomainId}, skipping {Name}",
                    plugin.DomainId, plugin.Name);
            }
        }

        _logger.LogInformation("Domain plugin registry initialized with {Count} plugins: [{Plugins}]",
            _plugins.Count, string.Join(", ", _plugins.Select(p => p.DomainId)));
    }

    public IReadOnlyList<IDomainPlugin> GetAll() => _plugins;

    public IDomainPlugin? GetById(string domainId) =>
        _byId.GetValueOrDefault(domainId);

    public async Task<IReadOnlyList<DomainClassification>> ClassifyAsync(
        string text,
        CancellationToken ct = default)
    {
        if (_plugins.Count == 0)
            return [];

        var results = new List<DomainClassification>();

        foreach (var plugin in _plugins)
        {
            try
            {
                var classification = await plugin.ClassifyAsync(text, ct);
                results.Add(classification);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Domain plugin {DomainId} failed to classify text", plugin.DomainId);
            }
        }

        return results.OrderByDescending(c => c.Confidence).ToList();
    }

    public async Task<DomainEnrichmentResult> EnrichAsync(
        IReadOnlyList<ContentChunk> chunks,
        double classificationThreshold = 0.3,
        string? domainHint = null,
        CancellationToken ct = default)
    {
        if (_plugins.Count == 0 || chunks.Count == 0)
            return DomainEnrichmentResult.Empty;

        // If a domain hint is provided, skip classification and use the hinted plugin directly
        if (!string.IsNullOrEmpty(domainHint))
        {
            var hintedPlugin = GetById(domainHint);
            if (hintedPlugin != null)
            {
                _logger.LogInformation(
                    "Using domain hint '{DomainHint}', skipping classification",
                    domainHint);

                try
                {
                    var hintedEnrichment = await hintedPlugin.EnrichAsync(chunks, ct);
                    var hintedClassification = new DomainClassification(
                        hintedPlugin.DomainId, hintedPlugin.Name, 1.0,
                        new Dictionary<string, object?> { ["source"] = "hint" });
                    return hintedEnrichment with { Classifications = [hintedClassification] };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Domain enrichment failed for hinted plugin {DomainId}", domainHint);
                    return DomainEnrichmentResult.Empty;
                }
            }

            _logger.LogWarning(
                "Domain hint '{DomainHint}' did not match any registered plugin, falling back to auto-detection",
                domainHint);
        }

        // Combine chunk text for classification, targeting ~8000 chars for reliable keyword matching.
        // Individual segments may be very short (single sentences), so we need enough
        // combined text for classifiers to find patterns.
        // Skip the first 10% of chunks to avoid boilerplate (e.g. Gutenberg headers).
        var skipCount = chunks.Count > 20 ? chunks.Count / 10 : 0;
        var combinedSample = new System.Text.StringBuilder(8000);
        var step = Math.Max(1, (chunks.Count - skipCount) / Math.Min(50, chunks.Count));
        for (var i = skipCount; i < chunks.Count && combinedSample.Length < 8000; i += step)
        {
            combinedSample.Append(chunks[i].Text);
            combinedSample.Append('\n');
        }
        // If we still have room, fill from the middle of the document
        if (combinedSample.Length < 4000)
        {
            var mid = chunks.Count / 2;
            for (var i = mid; i < chunks.Count && combinedSample.Length < 8000; i++)
            {
                combinedSample.Append(chunks[i].Text);
                combinedSample.Append('\n');
            }
        }

        // Classify against all plugins
        var sampleText = combinedSample.ToString();
        _logger.LogDebug("Classification sample: {Length} chars, first 200: {Sample}",
            sampleText.Length, sampleText.Length > 200 ? sampleText[..200] : sampleText);
        var classifications = await ClassifyAsync(sampleText, ct);

        if (classifications.Count == 0)
            return DomainEnrichmentResult.Empty;

        // Filter by threshold
        var matchingClassifications = classifications
            .Where(c => c.Confidence >= classificationThreshold)
            .ToList();

        if (matchingClassifications.Count == 0)
        {
            _logger.LogDebug("No domain plugins matched above threshold {Threshold}. " +
                             "Best match: {DomainId} ({Confidence:P1})",
                classificationThreshold,
                classifications[0].DomainId,
                classifications[0].Confidence);
            return new DomainEnrichmentResult(classifications, [], []);
        }

        // Run enrichment with best-matching plugin
        var bestMatch = matchingClassifications[0];
        var plugin = GetById(bestMatch.DomainId);
        if (plugin == null)
            return new DomainEnrichmentResult(classifications, [], []);

        _logger.LogInformation("Running domain enrichment with {DomainId} plugin (confidence: {Confidence:P1})",
            plugin.DomainId, bestMatch.Confidence);

        try
        {
            var enrichment = await plugin.EnrichAsync(chunks, ct);

            // Merge classifications from all plugins into the result
            return enrichment with { Classifications = classifications };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain enrichment failed for plugin {DomainId}", plugin.DomainId);
            return new DomainEnrichmentResult(classifications, [], []);
        }
    }
}
