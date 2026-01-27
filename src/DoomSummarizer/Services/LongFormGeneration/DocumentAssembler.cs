using DoomSummarizer.Models;
using DoomSummarizer.Models.LongFormGeneration;

namespace DoomSummarizer.Services.LongFormGeneration;

/// <summary>
/// Phase 6: Assembly (Deterministic).
/// Converts a validated DocumentPlan into a BlogArticleResult for compatibility
/// with existing template rendering and output pipeline.
/// </summary>
public static class DocumentAssembler
{
    /// <summary>
    /// Assemble the final BlogArticleResult from a validated plan.
    /// </summary>
    public static BlogArticleResult Assemble(
        DocumentPlan plan,
        string introduction,
        string conclusion,
        ValidationResult? validation,
        EvidenceCorpus corpus)
    {
        var sections = plan.Sections
            .Where(s => s.GeneratedContent != null)
            .Select(s => new BlogSectionResult
            {
                Heading = s.Heading,
                Content = s.GeneratedContent!,
                SourceUrls = s.SourceUrls
            })
            .ToList();

        var allSourceUrls = sections
            .SelectMany(s => s.SourceUrls)
            .Distinct()
            .ToList();

        // Append validation metadata as HTML comment if available
        if (validation != null)
        {
            var meta = $"<!-- Validated: {DateTime.UtcNow:yyyy-MM-dd}, " +
                       $"Grounding: {validation.OverallGroundingScore:P0}, " +
                       $"Verified URLs: {validation.VerifiedUrlCount}, " +
                       $"Grounded Entities: {validation.GroundedEntityCount} -->";
            conclusion = conclusion + "\n\n" + meta;
        }

        return new BlogArticleResult
        {
            Title = plan.Title,
            Introduction = introduction,
            Sections = sections,
            Conclusion = conclusion,
            SourceUrls = allSourceUrls,
            QueryType = plan.QueryType
        };
    }
}
