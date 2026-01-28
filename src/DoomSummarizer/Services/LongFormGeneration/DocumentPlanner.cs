using System.Text;
using System.Text.Json;
using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Phase 2: Document Planning (Sentinel LLM).
/// Generates a JSON outline with theme keywords per section, then embeds each
/// section's keywords for deterministic evidence assignment.
/// This is one of only two phases that uses an LLM call.
/// </summary>
public static class DocumentPlanner
{
    /// <summary>
    /// Generate a document plan from the evidence corpus.
    /// Uses sentinel LLM for outline, then embeds theme keywords deterministically.
    /// </summary>
    public static async Task<DocumentPlan> CreatePlanAsync(
        EvidenceCorpus corpus,
        string query,
        QueryType queryType,
        string vibe,
        OllamaService ollama,
        Func<string, float[]> embedder,
        TemplateDefinition? templateDef = null,
        CancellationToken ct = default)
    {
        // If template has fixed sections, skip sentinel — build plan from YAML
        if (templateDef is { HasFixedSections: true })
            return BuildFromTemplate(corpus, query, queryType, templateDef, embedder);

        // If insufficient evidence, use a minimal plan
        if (corpus.Segments.Count < 20)
            return BuildMinimalPlan(corpus, query, queryType, embedder);

        // Sentinel-generated outline
        var outline = await GenerateOutlineAsync(corpus, query, queryType, ollama, templateDef, ct);
        return ConvertToPlan(outline, query, queryType, embedder);
    }

    private static async Task<SentinelOutline> GenerateOutlineAsync(
        EvidenceCorpus corpus,
        string query,
        QueryType queryType,
        OllamaService ollama,
        TemplateDefinition? templateDef,
        CancellationToken ct)
    {
        // Build evidence summary for sentinel — top articles by relevance
        var evidenceSummary = new StringBuilder();
        var topArticles = corpus.Articles.Values
            .OrderByDescending(a => a.Relevance)
            .Take(15);
        var idx = 0;
        foreach (var article in topArticles)
        {
            evidenceSummary.AppendLine($"[{idx}] \"{article.Title}\" ({article.Url})");
            if (!string.IsNullOrEmpty(article.Summary))
                evidenceSummary.AppendLine($"    {article.Summary}");
            idx++;
        }

        // Top entities for context
        var topEntities = string.Join(", ",
            corpus.GlobalEntities.Take(10).Select(e => e.Name));

        var outlineInstructions = templateDef?.OutlineInstructions
            ?? (queryType == QueryType.Timeline
                ? """
                  Create a CHRONOLOGICAL outline with eras/periods as sections.
                  Each section heading should include a year range.
                  Order sections from earliest to most recent.
                  """
                : """
                  Create a logical outline with 4-8 sections that flow naturally.
                  Start broad (context/background), go deep (key developments), end forward-looking.
                  """);

        var prompt = PromptTemplateService.Render("longform-outline", new Dictionary<string, object?>
        {
            ["QUERY"] = query,
            ["EVIDENCE"] = evidenceSummary.ToString(),
            ["OUTLINE_INSTRUCTIONS"] = outlineInstructions,
            ["TOP_ENTITIES"] = topEntities,
            ["SEGMENT_COUNT"] = corpus.Segments.Count.ToString()
        });

        try
        {
            var outlineJson = await ollama.SentinelGenerateAsync(prompt, null, 0.1, ct);
            var jsonStart = outlineJson.IndexOf('{');
            var jsonEnd = outlineJson.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var parsed = JsonSerializer.Deserialize<SentinelOutline>(
                    outlineJson[jsonStart..(jsonEnd + 1)],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed?.Sections.Count > 0)
                    return parsed;
            }
        }
        catch
        {
            // Fallback below
        }

        // Default fallback outline
        return new SentinelOutline
        {
            Title = $"{query} — A Deep Dive",
            ThemeDescription = query,
            Sections =
            [
                new() { Heading = "Background", ThemeKeywords = $"background context history {query}" },
                new() { Heading = "Key Developments", ThemeKeywords = $"developments findings breakthroughs {query}" },
                new() { Heading = "Current State", ThemeKeywords = $"current state latest recent {query}" }
            ],
            ConclusionAngle = "What's next"
        };
    }

    // Minimum similarity between section heading and query to keep the section
    private const float SectionRelevanceThreshold = 0.25f;

    private static DocumentPlan ConvertToPlan(
        SentinelOutline outline, string query, QueryType queryType, Func<string, float[]> embedder)
    {
        // Embed the query for relevance checking
        var queryEmbedding = embedder(query);

        var sections = outline.Sections
            .Select(s =>
            {
                // Combine section heading with query to anchor theme embedding (ComoRAG-inspired)
                var anchoredKeywords = !string.IsNullOrWhiteSpace(s.ThemeKeywords)
                    ? $"{s.ThemeKeywords} {query}"
                    : $"{s.Heading} {query}";

                return new PlannedSection
                {
                    Heading = s.Heading,
                    ThemeKeywords = !string.IsNullOrWhiteSpace(s.ThemeKeywords)
                        ? s.ThemeKeywords
                        : s.Heading,
                    TargetWords = s.TargetWords > 0 ? s.TargetWords : 300,
                    // Anchor theme embedding to query for better relevance matching
                    ThemeEmbedding = embedder(anchoredKeywords)
                };
            })
            .Where(s =>
            {
                // ComoRAG-style relevance gate: filter out sections that don't relate to query
                if (s.ThemeEmbedding == null) return true;
                var similarity = EmbeddingService.CosineSimilarity(s.ThemeEmbedding, queryEmbedding);
                return similarity >= SectionRelevanceThreshold;
            })
            .ToList();

        // Ensure we have at least 3 sections after filtering
        if (sections.Count < 3)
        {
            // Fallback: keep top 3 by relevance
            sections = outline.Sections
                .Select(s => new
                {
                    Section = new PlannedSection
                    {
                        Heading = s.Heading,
                        ThemeKeywords = !string.IsNullOrWhiteSpace(s.ThemeKeywords) ? s.ThemeKeywords : s.Heading,
                        TargetWords = s.TargetWords > 0 ? s.TargetWords : 300,
                        ThemeEmbedding = embedder($"{s.Heading} {query}")
                    },
                    Relevance = EmbeddingService.CosineSimilarity(embedder(s.Heading), queryEmbedding)
                })
                .OrderByDescending(x => x.Relevance)
                .Take(4)
                .Select(x => x.Section)
                .ToList();
        }

        var themeDesc = !string.IsNullOrWhiteSpace(outline.ThemeDescription)
            ? outline.ThemeDescription
            : query;

        return new DocumentPlan
        {
            Title = !string.IsNullOrWhiteSpace(outline.Title) ? outline.Title : query,
            ThemeDescription = themeDesc,
            Sections = sections,
            ConclusionAngle = outline.ConclusionAngle ?? "forward-looking insights",
            KeyEntities = outline.KeyEntities,
            ThemeEmbedding = embedder(themeDesc),
            QueryType = queryType
        };
    }

    private static DocumentPlan BuildFromTemplate(
        EvidenceCorpus corpus, string query, QueryType queryType,
        TemplateDefinition templateDef, Func<string, float[]> embedder)
    {
        var sections = templateDef.Sections.Select(s => new PlannedSection
        {
            Heading = s.Heading,
            ThemeKeywords = !string.IsNullOrWhiteSpace(s.Prompt) ? s.Prompt : s.Heading,
            TargetWords = s.TargetWords,
            Notes = s.Prompt,
            ThemeEmbedding = embedder(!string.IsNullOrWhiteSpace(s.Prompt)
                ? s.Prompt
                : s.Heading)
        }).ToList();

        return new DocumentPlan
        {
            Title = query,
            ThemeDescription = query,
            Sections = sections,
            ConclusionAngle = templateDef.Conclusion?.Prompt ?? "forward-looking insights",
            ThemeEmbedding = embedder(query),
            QueryType = queryType
        };
    }

    private static DocumentPlan BuildMinimalPlan(
        EvidenceCorpus corpus, string query, QueryType queryType, Func<string, float[]> embedder)
    {
        // With < 20 segments, use 3 sections
        var sections = new List<PlannedSection>
        {
            new()
            {
                Heading = "Background",
                ThemeKeywords = $"background context overview {query}",
                TargetWords = 250,
                ThemeEmbedding = embedder($"background context overview {query}")
            },
            new()
            {
                Heading = "Key Findings",
                ThemeKeywords = $"findings results developments {query}",
                TargetWords = 300,
                ThemeEmbedding = embedder($"findings results developments {query}")
            },
            new()
            {
                Heading = "Outlook",
                ThemeKeywords = $"future outlook implications {query}",
                TargetWords = 200,
                ThemeEmbedding = embedder($"future outlook implications {query}")
            }
        };

        return new DocumentPlan
        {
            Title = $"{query} — Overview",
            ThemeDescription = query,
            Sections = sections,
            ConclusionAngle = "What's next",
            ThemeEmbedding = embedder(query),
            QueryType = queryType
        };
    }
}
