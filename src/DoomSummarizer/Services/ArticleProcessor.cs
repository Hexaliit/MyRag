using DoomSummarizer.Models;
using Mostlylucid.DocSummarizer.Config;
using Mostlylucid.DocSummarizer.Models;
using Mostlylucid.DocSummarizer.Services;
using Spectre.Console;

namespace DoomSummarizer.Services;

/// <summary>
/// Processes articles through DocSummarizer pipeline for signal extraction
/// and segment-based summarization with evidence tracking.
/// </summary>
public class ArticleProcessor : IDisposable
{
    private readonly SegmentExtractor _segmentExtractor;
    private bool _disposed;

    // Threshold for choosing summarization strategy
    private const int ShortContentThreshold = 500;   // Direct LLM
    private const int MediumContentThreshold = 2000; // Segment extraction
    private const int LongContentThreshold = 5000;   // Full BertRAG

    public ArticleProcessor(OnnxConfig? onnxConfig = null)
    {
        // Create extraction config
        var extractionConfig = new ExtractionConfig
        {
            MaxSegmentsToEmbed = 500,
            MmrLambda = 0.7,
            FallbackBucketSize = 10,
            MinSegmentLength = 20
        };

        // Use provided ONNX config or create default
        var config = onnxConfig ?? new OnnxConfig();

        // Create segment extractor
        _segmentExtractor = new SegmentExtractor(config, extractionConfig, verbose: false);
    }

    /// <summary>
    /// Process a content item into segments with signals.
    /// Returns processed article with segments, salience scores, and evidence.
    /// </summary>
    public async Task<ProcessedArticle> ProcessAsync(ContentItem item, CancellationToken ct = default)
    {
        var content = BuildArticleContent(item);
        var contentLength = content.Length;

        // Select processing strategy based on content length
        var strategy = contentLength switch
        {
            < ShortContentThreshold => ProcessingStrategy.DirectSummary,
            < MediumContentThreshold => ProcessingStrategy.BasicSegments,
            < LongContentThreshold => ProcessingStrategy.FullExtraction,
            _ => ProcessingStrategy.BertRag
        };

        var result = new ProcessedArticle
        {
            Item = item,
            Strategy = strategy,
            ContentLength = contentLength
        };

        try
        {
            if (strategy == ProcessingStrategy.DirectSummary)
            {
                // Short content - just create a single segment
                result.Segments =
                [
                    new ArticleSegment
                    {
                        Id = $"{item.Id}_0",
                        Text = content,
                        Type = SegmentType.Sentence,
                        SalienceScore = 1.0,
                        PositionWeight = 1.0
                    }
                ];
                result.TopSegments = result.Segments;
            }
            else
            {
                // Extract segments with signals
                var docId = item.Id;
                var contentType = DetectContentType(item);

                var extraction = await _segmentExtractor.ExtractAsync(
                    docId, content, contentType, ct);

                // Convert to our segment model
                result.Segments = extraction.AllSegments
                    .Select(s => new ArticleSegment
                    {
                        Id = s.Id,
                        Text = s.Text,
                        Type = s.Type,
                        Index = s.Index,
                        SalienceScore = s.SalienceScore,
                        PositionWeight = s.PositionWeight,
                        ContentHash = s.ContentHash,
                        SectionTitle = s.SectionTitle,
                        Embedding = s.Embedding
                    })
                    .ToList();

                // Get top segments by salience (already deduped within extraction)
                result.TopSegments = extraction.TopBySalience
                    .Take(strategy == ProcessingStrategy.BertRag ? 20 : 10)
                    .Select(s => new ArticleSegment
                    {
                        Id = s.Id,
                        Text = s.Text,
                        Type = s.Type,
                        Index = s.Index,
                        SalienceScore = s.SalienceScore,
                        PositionWeight = s.PositionWeight,
                        ContentHash = s.ContentHash,
                        SectionTitle = s.SectionTitle,
                        Embedding = s.Embedding
                    })
                    .ToList();

                result.Centroid = extraction.Centroid;
                result.DeduplicationStats = new DeduplicationStats
                {
                    OriginalCount = extraction.AllSegments.Count,
                    FinalCount = extraction.TopBySalience.Count,
                    DroppedCount = extraction.AllSegments.Count - extraction.TopBySalience.Count,
                    BoostedCount = 0
                };
            }
        }
        catch (Exception ex)
        {
            // Fallback to simple processing
            result.Segments =
            [
                new ArticleSegment
                {
                    Id = $"{item.Id}_0",
                    Text = content,
                    Type = SegmentType.Sentence,
                    SalienceScore = 1.0,
                    PositionWeight = 1.0
                }
            ];
            result.TopSegments = result.Segments;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Process multiple articles in batch.
    /// </summary>
    public async Task<List<ProcessedArticle>> ProcessBatchAsync(
        List<ContentItem> items,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<ProcessedArticle>();

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var item = items[i];
            var processed = await ProcessAsync(item, ct);
            results.Add(processed);

            progress?.Report((i + 1, items.Count));
        }

        return results;
    }

    /// <summary>
    /// Build summary context from top segments with evidence references.
    /// </summary>
    public string BuildSummaryContext(ProcessedArticle article, bool includeReferences = true)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# {article.Item.Title}");
        if (!string.IsNullOrEmpty(article.Item.Url))
            sb.AppendLine($"Source: {article.Item.Url}");
        sb.AppendLine();

        // Group by section if available
        var bySection = article.TopSegments
            .GroupBy(s => s.SectionTitle ?? "Content")
            .OrderByDescending(g => g.Max(s => s.SalienceScore));

        foreach (var section in bySection)
        {
            if (section.Key != "Content")
                sb.AppendLine($"## {section.Key}");

            foreach (var segment in section.OrderByDescending(s => s.SalienceScore).Take(5))
            {
                var salience = segment.SalienceScore;
                var marker = salience > 0.8 ? "[HIGH]" : salience > 0.5 ? "[MED]" : "";

                if (includeReferences)
                {
                    sb.AppendLine($"- {marker} {segment.Text} [ref:{segment.Id}]");
                }
                else
                {
                    sb.AppendLine($"- {segment.Text}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildArticleContent(ContentItem item)
    {
        var sb = new System.Text.StringBuilder();

        // Title as heading
        sb.AppendLine($"# {item.Title}");
        sb.AppendLine();

        // Metadata
        if (!string.IsNullOrEmpty(item.Author))
            sb.AppendLine($"By {item.Author}");
        if (item.Score > 0)
            sb.AppendLine($"Score: {item.Score}");
        sb.AppendLine();

        // Main content
        if (!string.IsNullOrEmpty(item.Content))
        {
            sb.AppendLine(item.Content);
        }

        // Append linked page content (one-hop followed links)
        if (item.LinkedPages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Referenced Links");
            sb.AppendLine();

            foreach (var linked in item.LinkedPages)
            {
                sb.AppendLine($"### {linked.Title}");
                if (!string.IsNullOrEmpty(linked.SiteName))
                    sb.AppendLine($"From: {linked.SiteName}");
                sb.AppendLine($"URL: {linked.Url}");
                sb.AppendLine();
                sb.AppendLine(linked.Content);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static ContentType DetectContentType(ContentItem item)
    {
        var source = item.Source.ToLowerInvariant();

        // Technical sources tend to be expository
        if (source is "hn" or "stackoverflow" or "lobsters" or "devto")
            return ContentType.Expository;

        // News is expository
        if (source is "bbc" or "guardian" or "ars" or "verge" or "techcrunch")
            return ContentType.Expository;

        // Reddit can be mixed
        if (source.StartsWith("reddit"))
        {
            // Check for technical subreddits
            if (item.Tags.Any(t => t is "programming" or "csharp" or "dotnet" or "python" or "rust"))
                return ContentType.Expository;
            return ContentType.Unknown; // Let extractor detect
        }

        return ContentType.Unknown;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _segmentExtractor?.Dispose();
    }
}

/// <summary>
/// Processing strategy based on content length/complexity.
/// </summary>
public enum ProcessingStrategy
{
    /// <summary>Short content - direct LLM summary, no segmentation.</summary>
    DirectSummary,

    /// <summary>Medium content - basic segment extraction.</summary>
    BasicSegments,

    /// <summary>Longer content - full signal extraction with MMR.</summary>
    FullExtraction,

    /// <summary>Very long content - full BertRAG with retrieval.</summary>
    BertRag
}

/// <summary>
/// Result of processing an article through the pipeline.
/// </summary>
public class ProcessedArticle
{
    public required ContentItem Item { get; init; }
    public ProcessingStrategy Strategy { get; set; }
    public int ContentLength { get; set; }
    public List<ArticleSegment> Segments { get; set; } = [];
    public List<ArticleSegment> TopSegments { get; set; } = [];
    public float[]? Centroid { get; set; }
    public DeduplicationStats? DeduplicationStats { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A segment extracted from an article with signals.
/// </summary>
public class ArticleSegment
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public SegmentType Type { get; init; }
    public int Index { get; init; }
    public double SalienceScore { get; set; }
    public double PositionWeight { get; set; }
    public string? ContentHash { get; init; }
    public string? SectionTitle { get; init; }
    public float[]? Embedding { get; set; }
    public bool IsCrossArticleDuplicate { get; set; }
}

/// <summary>
/// Statistics from deduplication.
/// </summary>
public class DeduplicationStats
{
    public int OriginalCount { get; init; }
    public int FinalCount { get; init; }
    public int DroppedCount { get; init; }
    public int BoostedCount { get; init; }
}
