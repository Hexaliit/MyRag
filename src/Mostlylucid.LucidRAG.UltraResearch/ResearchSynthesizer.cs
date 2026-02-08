using System.Text;
using System.Text.RegularExpressions;
using DoomSummarizer.Services;
using LucidRAG.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LucidRAG.UltraResearch;

/// <summary>
///     Generates a research synthesis from an indexed corpus after a session completes.
///     Uses LLM when available, falls back to a structural summary.
/// </summary>
public class ResearchSynthesizer
{
    private readonly RagDocumentsDbContext _db;
    private readonly ILogger<ResearchSynthesizer> _logger;
    private readonly OllamaService? _ollama;

    public ResearchSynthesizer(
        RagDocumentsDbContext db,
        ILogger<ResearchSynthesizer> logger,
        OllamaService? ollama = null)
    {
        _db = db;
        _logger = logger;
        _ollama = ollama;
    }

    public async Task<ResearchSynthesis> SynthesizeAsync(
        UltraResearchState state,
        Guid collectionId,
        CancellationToken ct)
    {
        // Gather data
        var documents = await _db.Documents
            .Where(d => d.CollectionId == collectionId)
            .OrderByDescending(d => d.SegmentCount)
            .Take(50)
            .Select(d => new { d.Name, d.Metadata, d.SegmentCount })
            .ToListAsync(ct);

        var topEntities = await GetTopEntitiesByTypeAsync(collectionId, ct);

        var lastCheckpoint = state.Checkpoints.LastOrDefault();

        // Try LLM synthesis
        var llmAvailable = _ollama != null && await CheckLlmAvailableAsync();
        if (llmAvailable)
        {
            try
            {
                var prompt = BuildSynthesisPrompt(state, documents.Select(d => d.Name).ToList(),
                    topEntities, lastCheckpoint);

                var response = await _ollama!.GenerateAsync(prompt, temperature: 0.6, ct: ct);

                if (!string.IsNullOrWhiteSpace(response))
                {
                    var synthesis = ParseSynthesisResponse(response);
                    synthesis.IsLlmGenerated = true;
                    _logger.LogInformation("LLM synthesis generated ({Length} chars)", response.Length);
                    return synthesis;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM synthesis failed, falling back to structural");
            }
        }

        // Structural fallback
        return BuildStructuralSynthesis(state, documents.Select(d => d.Name).ToList(),
            topEntities, lastCheckpoint);
    }

    private string BuildSynthesisPrompt(
        UltraResearchState state,
        List<string> paperNames,
        Dictionary<string, List<string>> topEntities,
        SentinelCheckpoint? lastCheckpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a research analyst. Synthesize this research corpus on: \"{state.Topic}\"");
        sb.AppendLine();

        // Corpus statistics
        sb.AppendLine("## Corpus Statistics");
        sb.AppendLine($"- Papers: {state.PapersIngested}, Entities: {topEntities.Values.Sum(v => v.Count)}");
        if (lastCheckpoint != null)
            sb.AppendLine($"- Convergence ratio: {lastCheckpoint.NewInfoRatio:F3}");
        sb.AppendLine($"- Stop reason: {state.StopReason}");
        var elapsed = (state.CompletedAt ?? DateTimeOffset.UtcNow) - state.StartedAt;
        sb.AppendLine($"- Duration: {elapsed:hh\\:mm\\:ss}");
        sb.AppendLine();

        // Paper titles
        sb.AppendLine("## Paper Titles (top 30)");
        foreach (var name in paperNames.Take(30))
            sb.AppendLine($"- {name}");
        sb.AppendLine();

        // Key entities
        sb.AppendLine("## Key Entities");
        foreach (var (type, entities) in topEntities)
            sb.AppendLine($"- {type}: {string.Join(", ", entities.Take(10))}");
        sb.AppendLine();

        // Sentinel analysis
        if (lastCheckpoint != null)
        {
            sb.AppendLine("## Sentinel Analysis");
            if (!string.IsNullOrEmpty(lastCheckpoint.SentinelAnalysis))
                sb.AppendLine(lastCheckpoint.SentinelAnalysis);
            if (lastCheckpoint.IdentifiedGaps.Count > 0)
            {
                sb.AppendLine("### Identified Gaps");
                foreach (var gap in lastCheckpoint.IdentifiedGaps)
                    sb.AppendLine($"- {gap}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Task");
        sb.AppendLine("Write a research synthesis in Markdown with these sections:");
        sb.AppendLine("1. **Executive Summary** (2-3 paragraphs)");
        sb.AppendLine("2. **Key Findings** (bulleted list)");
        sb.AppendLine("3. **Remaining Gaps** (bulleted list)");
        sb.AppendLine("4. **Suggested Further Research** (bulleted list)");

        return sb.ToString();
    }

    private static ResearchSynthesis ParseSynthesisResponse(string response)
    {
        var synthesis = new ResearchSynthesis
        {
            SynthesisMarkdown = response
        };

        synthesis.KeyFindings = ExtractBulletSection(response, "Key Findings");
        synthesis.RemainingGaps = ExtractBulletSection(response, "Remaining Gaps");
        synthesis.FurtherResearch = ExtractBulletSection(response, "Suggested Further Research");

        return synthesis;
    }

    private static List<string> ExtractBulletSection(string markdown, string sectionName)
    {
        var results = new List<string>();

        // Find the section header (## or **header**)
        var pattern = $@"(?:^##\s*\**{Regex.Escape(sectionName)}\**|^\**{Regex.Escape(sectionName)}\**)";
        var headerMatch = Regex.Match(markdown, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!headerMatch.Success) return results;

        var startIdx = headerMatch.Index + headerMatch.Length;
        var remaining = markdown[startIdx..];

        // Read lines until next header or end
        foreach (var line in remaining.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("## ") || trimmed.StartsWith("**") && trimmed.EndsWith("**") && !trimmed.StartsWith("- **"))
                break;
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                results.Add(trimmed[2..].Trim());
        }

        return results;
    }

    private ResearchSynthesis BuildStructuralSynthesis(
        UltraResearchState state,
        List<string> paperNames,
        Dictionary<string, List<string>> topEntities,
        SentinelCheckpoint? lastCheckpoint)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Research Synthesis: {state.Topic}");
        sb.AppendLine();
        sb.AppendLine("*Structural summary (LLM unavailable)*");
        sb.AppendLine();

        // Executive summary
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();
        var elapsed = (state.CompletedAt ?? DateTimeOffset.UtcNow) - state.StartedAt;
        sb.AppendLine($"This research session indexed **{state.PapersIngested}** papers on \"{state.Topic}\" " +
                       $"over {elapsed:hh\\:mm\\:ss} ({state.Iteration} iterations). " +
                       $"The session ended because: {state.StopReason}.");
        sb.AppendLine();

        if (topEntities.Count > 0)
        {
            var totalEntities = topEntities.Values.Sum(v => v.Count);
            sb.AppendLine($"The corpus contains **{totalEntities}** distinct entities across " +
                           $"**{topEntities.Count}** entity types.");
        }
        sb.AppendLine();

        // Papers
        sb.AppendLine("## Papers Indexed");
        sb.AppendLine();
        foreach (var name in paperNames.Take(30))
            sb.AppendLine($"- {name}");
        if (paperNames.Count > 30)
            sb.AppendLine($"- ... and {paperNames.Count - 30} more");
        sb.AppendLine();

        // Key entities
        var keyFindings = new List<string>();
        if (topEntities.Count > 0)
        {
            sb.AppendLine("## Key Findings");
            sb.AppendLine();
            foreach (var (type, entities) in topEntities)
            {
                var finding = $"{type}: {string.Join(", ", entities.Take(5))}";
                sb.AppendLine($"- {finding}");
                keyFindings.Add(finding);
            }
            sb.AppendLine();
        }

        // Remaining gaps
        var gaps = new List<string>();
        if (lastCheckpoint?.IdentifiedGaps.Count > 0)
        {
            sb.AppendLine("## Remaining Gaps");
            sb.AppendLine();
            foreach (var gap in lastCheckpoint.IdentifiedGaps)
            {
                sb.AppendLine($"- {gap}");
                gaps.Add(gap);
            }
            sb.AppendLine();
        }

        // Further research
        var further = new List<string>();
        if (lastCheckpoint?.SuggestedQueries.Count > 0)
        {
            sb.AppendLine("## Suggested Further Research");
            sb.AppendLine();
            foreach (var query in lastCheckpoint.SuggestedQueries)
            {
                sb.AppendLine($"- {query}");
                further.Add(query);
            }
            sb.AppendLine();
        }

        return new ResearchSynthesis
        {
            SynthesisMarkdown = sb.ToString(),
            KeyFindings = keyFindings,
            RemainingGaps = gaps,
            FurtherResearch = further,
            IsLlmGenerated = false
        };
    }

    private async Task<Dictionary<string, List<string>>> GetTopEntitiesByTypeAsync(
        Guid collectionId, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>();

        var entities = await _db.DocumentEntityLinks
            .Where(del => _db.Documents
                .Where(d => d.CollectionId == collectionId)
                .Select(d => d.Id)
                .Contains(del.DocumentId))
            .Join(_db.Entities, del => del.EntityId, e => e.Id, (del, e) => e)
            .GroupBy(e => new { e.EntityType, e.CanonicalName })
            .Select(g => new { g.Key.EntityType, g.Key.CanonicalName, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(100)
            .ToListAsync(ct);

        foreach (var entity in entities)
        {
            if (!result.ContainsKey(entity.EntityType))
                result[entity.EntityType] = [];

            if (result[entity.EntityType].Count < 20)
                result[entity.EntityType].Add(entity.CanonicalName);
        }

        return result;
    }

    private async Task<bool> CheckLlmAvailableAsync()
    {
        try
        {
            return _ollama != null && await _ollama.IsAvailableAsync();
        }
        catch
        {
            return false;
        }
    }
}
