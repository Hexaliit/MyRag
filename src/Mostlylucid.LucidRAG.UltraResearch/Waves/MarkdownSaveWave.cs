using System.Text;
using LucidRAG.UltraResearch.Signals;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Analysis;

namespace LucidRAG.UltraResearch.Waves;

/// <summary>
///     Assembles YAML frontmatter and saves paper as .md file.
///     IO lane — writes to disk.
/// </summary>
public sealed class MarkdownSaveWave : ITypedAnalysisWave<FetchCandidate>
{
    private readonly ILogger<MarkdownSaveWave> _logger;

    public MarkdownSaveWave(ILogger<MarkdownSaveWave> logger)
    {
        _logger = logger;
    }

    public string Name => "research.markdown_save";
    public string Description => "Assemble frontmatter and save paper as markdown";
    public int Priority => 60;
    public IReadOnlyList<string> Tags => ["io", SignalTags.Content];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(FetchCandidate content, AnalysisContext context)
    {
        // Need title and content
        return context.HasSignal(ResearchSignalKeys.PaperMetadataTitle) &&
               context.HasSignal(ResearchSignalKeys.PaperContentText);
    }

    public async Task<IEnumerable<Signal>> AnalyzeAsync(
        FetchCandidate candidate, AnalysisContext context, CancellationToken ct = default)
    {
        var signals = new List<Signal>();

        var title = context.GetValue<string>(ResearchSignalKeys.PaperMetadataTitle);
        var content = context.GetValue<string>(ResearchSignalKeys.PaperContentText);
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content)) return signals;

        var authors = context.GetValue<List<string>>(ResearchSignalKeys.PaperMetadataAuthors) ?? [];
        var year = context.GetValue<int?>(ResearchSignalKeys.PaperMetadataYear);
        var doi = context.GetValue<string>(ResearchSignalKeys.PaperMetadataDoi);
        var arxivId = context.GetValue<string>(ResearchSignalKeys.PaperMetadataArxivId);
        var venueQuality = context.GetValue<double?>(ResearchSignalKeys.PaperVenueQuality) ?? 0.0;
        var venueName = context.GetValue<string>(ResearchSignalKeys.PaperVenueName);
        var citationCount = context.GetValue<int?>(ResearchSignalKeys.PaperCitationsCount) ?? 0;
        var influentialCitations = context.GetValue<int?>(ResearchSignalKeys.PaperCitationsInfluential) ?? 0;

        // Get data directory from cache (set by PaperCoordinator)
        var dataDir = context.GetCached<string>("data_dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lucidrag");

        var dir = Path.Combine(dataDir, "ultraresearch");
        Directory.CreateDirectory(dir);

        var sanitizedId = candidate.Id.Replace("/", "_").Replace(":", "_").Replace(".", "_");
        var filePath = Path.Combine(dir, $"{sanitizedId}.md");

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(title)}\"");
        if (authors.Count > 0)
            sb.AppendLine($"authors: \"{EscapeYaml(string.Join(", ", authors.Take(10)))}\"");
        if (year.HasValue)
            sb.AppendLine($"year: {year.Value}");
        if (!string.IsNullOrEmpty(doi))
            sb.AppendLine($"doi: \"{doi}\"");
        if (!string.IsNullOrEmpty(arxivId))
            sb.AppendLine($"arxiv_id: \"{arxivId}\"");

        var sourceUrl = !string.IsNullOrEmpty(arxivId)
            ? $"https://arxiv.org/abs/{arxivId}"
            : !string.IsNullOrEmpty(doi)
                ? $"https://doi.org/{doi}"
                : null;
        if (!string.IsNullOrEmpty(sourceUrl))
            sb.AppendLine($"source_url: \"{sourceUrl}\"");
        if (!string.IsNullOrEmpty(venueName))
            sb.AppendLine($"venue: \"{EscapeYaml(venueName)}\"");
        if (venueQuality > 0)
            sb.AppendLine($"venue_quality: {venueQuality:F2}");
        if (citationCount > 0)
            sb.AppendLine($"citation_count: {citationCount}");
        if (influentialCitations > 0)
            sb.AppendLine($"influential_citations: {influentialCitations}");
        sb.AppendLine($"fetched_at: \"{DateTimeOffset.UtcNow:O}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine(content);

        await File.WriteAllTextAsync(filePath, sb.ToString(), ct);

        signals.Add(new Signal
        {
            Key = ResearchSignalKeys.PaperFilePath,
            Value = filePath,
            Confidence = 1.0,
            Source = Name,
            Tags = [SignalTags.Content]
        });

        _logger.LogDebug("MarkdownSaveWave: saved to {Path}", filePath);
        return signals;
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
    }
}
