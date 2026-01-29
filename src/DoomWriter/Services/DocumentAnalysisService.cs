using System.Text.RegularExpressions;
using DoomWriter.Models;

namespace DoomWriter.Services;

/// <summary>
/// Reactive document analysis pipeline.
/// On every content change (debounced), extracts signals:
/// headings, segments, entities, topics, drift.
/// </summary>
public partial class DocumentAnalysisService
{
    private readonly WriterSettingsService _settings;
    private CancellationTokenSource? _debounceCts;
    private readonly SemaphoreSlim _analysisLock = new(1, 1);

    /// <summary>
    /// Raised when analysis completes with new signals.
    /// </summary>
    public event EventHandler<DocumentSignals>? AnalysisCompleted;

    public DocumentAnalysisService(WriterSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Analyze markdown content. Debounced — cancels any pending analysis.
    /// </summary>
    public async Task AnalyzeAsync(string markdown)
    {
        // Cancel any pending debounced analysis
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;

        try
        {
            // Debounce wait
            await Task.Delay(_settings.Config.DebounceMs, ct);

            await _analysisLock.WaitAsync(ct);
            try
            {
                var signals = await RunPipelineAsync(markdown, ct);
                AnalysisCompleted?.Invoke(this, signals);
            }
            finally
            {
                _analysisLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when debounce is superseded by a newer change
        }
    }

    private async Task<DocumentSignals> RunPipelineAsync(string markdown, CancellationToken ct)
    {
        var signals = new DocumentSignals();

        // 1. Parse headings for TOC
        var headings = ExtractHeadings(markdown);
        signals.Headings = headings;

        // 2. Extract segments (paragraphs separated by blank lines)
        var segments = ExtractSegments(markdown);
        signals.Segments = segments;
        signals.SegmentCount = segments.Count;

        // 3. Word count
        signals.WordCount = CountWords(markdown);

        // 4. Entity extraction (regex-based for speed — NER via Core in Phase 2)
        var entities = ExtractEntitiesBasic(markdown, segments);
        signals.Entities = entities;
        signals.EntityCount = entities.Count;

        // 5. Topic inference (simple: use heading text as topic proxy)
        var topics = InferTopics(headings, segments);
        signals.Topics = topics;
        signals.DominantTopic = topics.Count > 0
            ? topics.MaxBy(t => t.Score)?.Topic ?? ""
            : "";

        // 6. Drift detection (placeholder — full embedding-based in Phase 2)
        signals.DriftScore = 0f;
        signals.CoherenceScore = 1f;

        return signals;
    }

    // --- Heading extraction ---

    private static List<HeadingItem> ExtractHeadings(string markdown)
    {
        var headings = new List<HeadingItem>();
        var lines = markdown.Split('\n');
        var charOffset = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var match = HeadingRegex().Match(line);
            if (match.Success)
            {
                headings.Add(new HeadingItem
                {
                    Level = match.Groups[1].Value.Length,
                    Text = match.Groups[2].Value.Trim(),
                    LineNumber = i + 1,
                    CharOffset = charOffset
                });
            }
            charOffset += lines[i].Length + 1; // +1 for \n
        }

        return headings;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    // --- Segment extraction ---

    private static List<AnalyzedSegment> ExtractSegments(string markdown)
    {
        var segments = new List<AnalyzedSegment>();
        var paragraphs = ParagraphSplitRegex().Split(markdown);
        var charOffset = 0;
        var position = 0;

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                charOffset += para.Length;
                continue;
            }

            // Skip headings as standalone segments (they're in the TOC)
            if (trimmed.StartsWith('#'))
            {
                charOffset += para.Length;
                continue;
            }

            var firstLine = trimmed.Split('\n')[0];
            if (firstLine.Length > 80)
                firstLine = firstLine[..77] + "...";

            // Simple salience heuristic: longer paragraphs with more varied vocabulary score higher
            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var uniqueRatio = words.Length > 0
                ? (float)words.Distinct(StringComparer.OrdinalIgnoreCase).Count() / words.Length
                : 0f;
            var lengthFactor = Math.Min(1f, words.Length / 50f);
            var salience = (uniqueRatio * 0.6f + lengthFactor * 0.4f);

            segments.Add(new AnalyzedSegment
            {
                Text = trimmed,
                FirstLine = firstLine,
                Salience = salience,
                Position = position,
                CharOffset = charOffset,
                EntityNames = ExtractInlineEntities(trimmed)
            });

            position++;
            charOffset += para.Length;
        }

        return segments;
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphSplitRegex();

    // --- Basic entity extraction (regex-based) ---

    private static List<TrackedEntity> ExtractEntitiesBasic(string markdown, List<AnalyzedSegment> segments)
    {
        var entityMentions = new Dictionary<string, (string Type, int Count, List<int> Sections)>(
            StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < segments.Count; i++)
        {
            foreach (var name in segments[i].EntityNames)
            {
                if (entityMentions.TryGetValue(name, out var existing))
                {
                    existing.Count++;
                    if (!existing.Sections.Contains(i))
                        existing.Sections.Add(i);
                    entityMentions[name] = existing;
                }
                else
                {
                    entityMentions[name] = (InferEntityType(name), 1, [i]);
                }
            }
        }

        return entityMentions.Select(kv => new TrackedEntity
        {
            Name = kv.Key,
            Type = kv.Value.Type,
            MentionCount = kv.Value.Count,
            SectionIndices = kv.Value.Sections
        }).ToList();
    }

    /// <summary>
    /// Extract entities from a single segment using regex patterns.
    /// Finds: capitalized multi-word phrases, backtick code, bold terms.
    /// </summary>
    private static List<string> ExtractInlineEntities(string text)
    {
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Capitalized multi-word names (e.g., "Hacker News", "OpenAI")
        foreach (Match m in CapitalizedPhraseRegex().Matches(text))
        {
            var phrase = m.Value.Trim();
            if (phrase.Length >= 3 && !IsCommonPhrase(phrase))
                entities.Add(phrase);
        }

        // Backtick code references (e.g., `EmbeddingService`)
        foreach (Match m in BacktickRegex().Matches(text))
        {
            var code = m.Groups[1].Value;
            if (code.Length >= 2)
                entities.Add(code);
        }

        return entities.ToList();
    }

    [GeneratedRegex(@"(?<![#\[])(?:[A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)")]
    private static partial Regex CapitalizedPhraseRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex BacktickRegex();

    private static bool IsCommonPhrase(string phrase) =>
        phrase is "The" or "This" or "That" or "These" or "Those" or "Here"
            or "There" or "In The" or "On The" or "For The";

    private static string InferEntityType(string name)
    {
        // Simple heuristic type inference
        if (name.Contains('.') || name.All(c => char.IsLetterOrDigit(c)))
            return "MISC"; // Code-like
        if (name.EndsWith("Service") || name.EndsWith("API") || name.EndsWith("Inc") || name.EndsWith("Corp"))
            return "ORG";
        return "MISC";
    }

    // --- Topic inference ---

    private static List<TopicScore> InferTopics(List<HeadingItem> headings, List<AnalyzedSegment> segments)
    {
        var topics = new List<TopicScore>();

        // Use heading text as topic proxy for nearby segments
        for (int i = 0; i < headings.Count; i++)
        {
            var heading = headings[i];
            var nextHeadingOffset = i + 1 < headings.Count ? headings[i + 1].CharOffset : int.MaxValue;

            var sectionSegments = segments.Where(s =>
                s.CharOffset >= heading.CharOffset && s.CharOffset < nextHeadingOffset).ToList();

            if (sectionSegments.Count > 0)
            {
                topics.Add(new TopicScore
                {
                    Topic = heading.Text,
                    Score = sectionSegments.Average(s => s.Salience),
                    SectionIndex = i
                });
            }
        }

        return topics;
    }

    // --- Word count ---

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
