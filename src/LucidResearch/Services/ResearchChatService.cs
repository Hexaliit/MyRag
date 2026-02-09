using System.Text;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidResearch.Services;

/// <summary>
///     Lightweight Q&A service for asking questions against the research corpus.
///     Uses semantic search over the DuckDB vector store + optional LLM synthesis.
/// </summary>
public class ResearchChatService
{
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<ResearchChatService> _logger;
    private readonly IVectorStore _vectorStore;
    private readonly ILlmService? _llm;
    private readonly string _collectionName;

    public ResearchChatService(
        IEmbeddingService embeddings,
        IVectorStore vectorStore,
        ILogger<ResearchChatService> logger,
        ILlmService? llm = null,
        string collectionName = "ragdocuments")
    {
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _logger = logger;
        _llm = llm;
        _collectionName = collectionName;
    }

    /// <summary>
    ///     Ask a question against the research corpus. Returns formatted answer with sources.
    /// </summary>
    public async Task<ChatResult> AskAsync(string query, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 1. Embed the query
        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _embeddings.EmbedAsync(query, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to embed query");
            return new ChatResult { Answer = $"Embedding failed: {ex.Message}", SearchTimeMs = stopwatch.ElapsedMilliseconds };
        }

        // 2. Search the vector store
        var segments = await _vectorStore.SearchAsync(_collectionName, queryEmbedding, topK: 15, ct: ct);

        if (segments.Count == 0)
        {
            return new ChatResult
            {
                Answer = "No relevant segments found. Make sure research has been ingested.",
                SearchTimeMs = stopwatch.ElapsedMilliseconds
            };
        }

        // 3. Build context from top segments
        var contextBuilder = new StringBuilder();
        var sources = new List<ChatSource>();

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var text = !string.IsNullOrEmpty(seg.Text) ? seg.Text : $"[Segment {seg.Id}]";
            contextBuilder.AppendLine($"[{i + 1}] {text}");
            contextBuilder.AppendLine();

            sources.Add(new ChatSource
            {
                Index = i + 1,
                SegmentId = seg.Id,
                Section = seg.SectionTitle,
                Score = seg.QuerySimilarity,
                Type = seg.Type.ToString()
            });
        }

        // 4. Try LLM synthesis, fall back to formatted search results
        string answer;
        if (_llm != null)
        {
            try
            {
                var prompt = BuildSynthesisPrompt(query, contextBuilder.ToString());
                answer = await _llm.GenerateAsync(prompt, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM synthesis failed, falling back to search results");
                answer = FormatSearchResults(query, segments);
            }
        }
        else
        {
            answer = FormatSearchResults(query, segments);
        }

        stopwatch.Stop();
        return new ChatResult
        {
            Answer = answer,
            Sources = sources,
            SegmentCount = segments.Count,
            SearchTimeMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static string BuildSynthesisPrompt(string query, string context)
    {
        return $"""
                You are a research assistant. Answer the user's question based ONLY on the provided research segments.
                Cite sources using [N] notation. If the segments don't contain enough information, say so.

                ## Research Context
                {context}

                ## Question
                {query}

                ## Answer (cite sources with [N])
                """;
    }

    private static string FormatSearchResults(string query, List<Mostlylucid.DocSummarizer.Models.Segment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Search results for: {query}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();

        var shown = 0;
        foreach (var seg in segments.Take(10))
        {
            shown++;
            var typeTag = seg.Type switch
            {
                Mostlylucid.DocSummarizer.Models.SegmentType.CodeBlock => "[CODE]",
                Mostlylucid.DocSummarizer.Models.SegmentType.TableRow => "[TABLE]",
                Mostlylucid.DocSummarizer.Models.SegmentType.Quote => "[QUOTE]",
                Mostlylucid.DocSummarizer.Models.SegmentType.Heading => "[HEAD]",
                Mostlylucid.DocSummarizer.Models.SegmentType.ListItem => "[LIST]",
                _ => "[TEXT]"
            };

            var preview = string.IsNullOrEmpty(seg.Text)
                ? $"(segment {seg.Id}, score: {seg.QuerySimilarity:F3})"
                : seg.Text.Length > 200
                    ? seg.Text[..200] + "..."
                    : seg.Text;

            var section = !string.IsNullOrEmpty(seg.SectionTitle)
                ? $" ({seg.SectionTitle})"
                : "";

            sb.AppendLine($"  [{shown}] {typeTag}{section}  score: {seg.QuerySimilarity:F3}");
            sb.AppendLine($"      {preview}");
            sb.AppendLine();
        }

        if (segments.Count > 10)
            sb.AppendLine($"  ... and {segments.Count - 10} more results");

        return sb.ToString();
    }
}

public class ChatResult
{
    public string Answer { get; init; } = "";
    public List<ChatSource> Sources { get; init; } = [];
    public int SegmentCount { get; init; }
    public long SearchTimeMs { get; init; }
}

public class ChatSource
{
    public int Index { get; init; }
    public string SegmentId { get; init; } = "";
    public string? Section { get; init; }
    public double Score { get; init; }
    public string Type { get; init; } = "";
}
