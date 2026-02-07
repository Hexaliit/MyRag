using DomainClassifier.Core.Interfaces;
using DomainClassifier.Core.Models;
using DomainClassifier.Technical.Extractors;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace DomainClassifier.Technical;

/// <summary>
///     Technical/academic domain plugin. Detects academic papers, surveys, theses, and documentation.
///     Extracts citations, bibliography entries, methodology terms (algorithms, datasets, frameworks).
/// </summary>
public class TechnicalDomainPlugin : IDomainPlugin
{
    private readonly ILogger<TechnicalDomainPlugin> _logger;

    public TechnicalDomainPlugin(ILogger<TechnicalDomainPlugin> logger)
    {
        _logger = logger;
    }

    public string DomainId => "technical";
    public string Name => "Technical/Academic Domain";

    public IReadOnlyList<string> EntityTypes =>
    [
        "citation_numbered", "citation_author_year",
        "bibliography_entry",
        "algorithm", "dataset", "framework", "statistical_method"
    ];

    public IReadOnlyList<string> QueryRelevanceTerms =>
    [
        "paper", "study", "research", "citation", "reference",
        "algorithm", "dataset", "methodology", "findings",
        "author", "published", "arxiv", "doi"
    ];

    public Task<DomainClassification> ClassifyAsync(
        string text,
        CancellationToken ct = default)
    {
        var result = TechnicalKeywordClassifier.Classify(text);
        return Task.FromResult(result);
    }

    public Task<DomainEnrichmentResult> EnrichAsync(
        IReadOnlyList<ContentChunk> chunks,
        CancellationToken ct = default)
    {
        var allEntities = new List<DomainEntity>();
        var signals = new List<DomainSignal>();

        // Combine all chunk text for document-level analysis
        var allText = string.Join("\n", chunks.Select(c => c.Text));

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            allEntities.AddRange(CitationExtractor.Extract(chunk.Text));
            allEntities.AddRange(MethodologyExtractor.Extract(chunk.Text));
        }

        // Bibliography extraction on full document (section is at the end)
        var bibEntries = BibliographyExtractor.Extract(allText);
        allEntities.AddRange(bibEntries);

        // Cross-reference: match numbered inline citations to bibliography entries
        CrossReferenceCitations(allEntities, bibEntries);

        // Compute signals
        var citationEntities = allEntities
            .Where(e => e.EntityType is "citation_numbered" or "citation_author_year")
            .ToList();

        signals.Add(new DomainSignal(
            "technical.citation_count",
            citationEntities.Count,
            0.95,
            DomainId));

        var hasBibliography = BibliographyExtractor.HasBibliography(allText);
        signals.Add(new DomainSignal(
            "technical.has_bibliography",
            hasBibliography,
            0.95,
            DomainId));

        signals.Add(new DomainSignal(
            "technical.bibliography_entry_count",
            bibEntries.Count,
            0.95,
            DomainId));

        // DOI count from bibliography
        var doiCount = bibEntries.Count(e =>
            e.Metadata?.ContainsKey("doi") == true && e.Metadata["doi"] != null);
        signals.Add(new DomainSignal(
            "technical.doi_count",
            doiCount,
            0.9,
            DomainId));

        // Citation style detection
        var hasNumbered = citationEntities.Any(e => e.EntityType == "citation_numbered");
        var hasAuthorYear = citationEntities.Any(e => e.EntityType == "citation_author_year");
        var citationStyle = (hasNumbered, hasAuthorYear) switch
        {
            (true, true) => "mixed",
            (true, false) => "numbered",
            (false, true) => "author_year",
            _ => "none"
        };
        signals.Add(new DomainSignal(
            "technical.citation_style",
            citationStyle,
            0.85,
            DomainId));

        // Methodology terms
        var methodTerms = allEntities
            .Where(e => e.EntityType is "algorithm" or "dataset" or "framework" or "statistical_method")
            .Select(e => e.Text)
            .Distinct()
            .ToList();

        if (methodTerms.Count > 0)
            signals.Add(new DomainSignal(
                "technical.methodology_terms",
                methodTerms,
                0.85,
                DomainId));

        // Document subtype inference
        var docSubtype = InferDocumentSubtype(allText, citationEntities.Count, hasBibliography, methodTerms.Count);
        signals.Add(new DomainSignal(
            "technical.document_subtype",
            docSubtype,
            0.7,
            DomainId));

        var classification = TechnicalKeywordClassifier.Classify(
            string.Join("\n", chunks.Take(3).Select(c => c.Text)));

        _logger.LogDebug(
            "Technical enrichment: {EntityCount} entities, {SignalCount} signals from {ChunkCount} chunks",
            allEntities.Count, signals.Count, chunks.Count);

        return Task.FromResult(new DomainEnrichmentResult(
            Classifications: [classification],
            Entities: allEntities,
            Signals: signals));
    }

    private static void CrossReferenceCitations(List<DomainEntity> allEntities, List<DomainEntity> bibEntries)
    {
        // Build a lookup from entry_number to bibliography entry
        var bibLookup = new Dictionary<string, DomainEntity>();
        foreach (var bib in bibEntries)
        {
            if (bib.Metadata?.TryGetValue("entry_number", out var num) == true && num is string numStr)
                bibLookup[numStr] = bib;
        }

        if (bibLookup.Count == 0) return;

        // Enrich numbered citations with resolved bibliography info
        foreach (var citation in allEntities.Where(e => e.EntityType == "citation_numbered"))
        {
            if (citation.Metadata?.TryGetValue("citation_key", out var key) != true) continue;
            if (key is not string keyStr) continue;

            if (bibLookup.TryGetValue(keyStr, out var bib) && bib.Metadata != null)
            {
                // Copy resolved metadata to citation
                if (bib.Metadata.TryGetValue("title", out var title))
                    citation.Metadata!["resolved_title"] = title;
                if (bib.Metadata.TryGetValue("authors", out var authors))
                    citation.Metadata!["resolved_authors"] = authors;
                if (bib.Metadata.TryGetValue("year", out var year))
                    citation.Metadata!["resolved_year"] = year;
                if (bib.Metadata.TryGetValue("doi", out var doi))
                    citation.Metadata!["resolved_doi"] = doi;
                if (bib.Metadata.TryGetValue("arxiv_id", out var arxiv))
                    citation.Metadata!["resolved_arxiv_id"] = arxiv;
            }
        }
    }

    private static string InferDocumentSubtype(
        string text, int citationCount, bool hasBibliography, int methodTermCount)
    {
        var textLower = text.ToLowerInvariant();

        // Survey/review indicators
        if ((textLower.Contains("survey") || textLower.Contains("literature review") ||
             textLower.Contains("systematic review")) && citationCount > 20)
            return "survey";

        // Thesis indicators
        if (textLower.Contains("thesis") || textLower.Contains("dissertation") ||
            (textLower.Contains("chapter") && textLower.Contains("acknowledgements")))
            return "thesis";

        // Research paper indicators
        if (hasBibliography && citationCount > 5 && methodTermCount > 2)
            return "research_paper";

        // Technical documentation
        if (methodTermCount > 3 && !hasBibliography)
            return "documentation";

        // Fallback
        if (hasBibliography || citationCount > 3)
            return "research_paper";

        return "documentation";
    }
}
