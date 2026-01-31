using System.Text.RegularExpressions;
using Mostlylucid.Summarizer.Core.Analysis;
using DoomWriter.Models;

namespace DoomWriter.Waves;

/// <summary>
/// Extracts text segments (paragraphs) with salience scoring.
/// Fast lane, high priority — entity extraction depends on segments.
/// </summary>
public sealed partial class SegmentExtractionWave : ITypedAnalysisWave<string>
{
    public string Name => "doc.segments";
    public string Description => "Extract paragraphs with salience scoring";
    public int Priority => 85;
    public IReadOnlyList<string> Tags => [SignalTags.Structure, SignalTags.Content];
    public bool Enabled { get; set; } = true;

    public bool ShouldRun(string content, AnalysisContext context) =>
        !string.IsNullOrWhiteSpace(content);

    public Task<IEnumerable<Signal>> AnalyzeAsync(
        string markdown, AnalysisContext context, CancellationToken ct = default)
    {
        var segments = new List<AnalyzedSegment>();
        var paragraphs = ParagraphSplitRegex().Split(markdown);
        var charOffset = 0;
        var position = 0;

        var inCodeBlock = false;

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                charOffset += para.Length;
                continue;
            }

            // Track fenced code block state — code lines within fences
            // get high vocabulary uniqueness (identifiers) and would dominate
            // the signal panel if scored as prose.
            if (trimmed.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                charOffset += para.Length;
                continue;
            }

            if (inCodeBlock)
            {
                charOffset += para.Length;
                continue;
            }

            // Skip headings (handled by HeadingExtractionWave)
            if (trimmed.StartsWith('#'))
            {
                charOffset += para.Length;
                continue;
            }

            var firstLine = trimmed.Split('\n')[0];
            if (firstLine.Length > 80)
                firstLine = firstLine[..77] + "...";

            // Salience: vocabulary richness + length factor
            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var uniqueRatio = words.Length > 0
                ? (float)words.Distinct(StringComparer.OrdinalIgnoreCase).Count() / words.Length
                : 0f;
            var lengthFactor = Math.Min(1f, words.Length / 50f);
            var salience = uniqueRatio * 0.6f + lengthFactor * 0.4f;

            segments.Add(new AnalyzedSegment
            {
                Text = trimmed,
                FirstLine = firstLine,
                Salience = salience,
                Position = position,
                CharOffset = charOffset,
                EntityNames = ExtractInlineEntities(trimmed),
                Kind = SegmentKind.Prose
            });

            position++;
            charOffset += para.Length;
        }

        // Cache segments for downstream waves
        context.SetCached("segments", segments);

        var signals = new List<Signal>
        {
            new()
            {
                Key = "doc.structure.segments",
                Value = segments,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Structure, SignalTags.Content]
            },
            new()
            {
                Key = "doc.structure.segment_count",
                Value = segments.Count,
                Confidence = 1.0,
                Source = Name,
                Tags = [SignalTags.Structure]
            }
        };

        return Task.FromResult<IEnumerable<Signal>>(signals);
    }

    /// <summary>
    /// Extract entities from a single segment using regex patterns.
    /// </summary>
    internal static List<string> ExtractInlineEntities(string text)
    {
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Capitalized multi-word names
        foreach (Match m in CapitalizedPhraseRegex().Matches(text))
        {
            var phrase = m.Value.Trim();
            if (phrase.Length >= 3 && !IsCommonPhrase(phrase))
                entities.Add(phrase);
        }

        // Backtick code references
        foreach (Match m in BacktickRegex().Matches(text))
        {
            var code = m.Groups[1].Value;
            if (code.Length >= 2)
                entities.Add(code);
        }

        return entities.ToList();
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphSplitRegex();

    [GeneratedRegex(@"(?<![#\[])(?:[A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)")]
    private static partial Regex CapitalizedPhraseRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex BacktickRegex();

    private static bool IsCommonPhrase(string phrase) =>
        phrase is "The" or "This" or "That" or "These" or "Those" or "Here"
            or "There" or "In The" or "On The" or "For The";
}
