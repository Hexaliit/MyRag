using System.Text;
using DoomSummarizer.Helpers;
using DoomSummarizer.Models;
using DoomSummarizer.Plugins;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Splitters;

/// <summary>
///     Main splitter — adaptive depth based on document length and structure.
///     The longer and more structured the document, the deeper the split:
///     paragraphs -> sections -> chapters -> parts -> works.
/// </summary>
public class HierarchicalBookSplitter : IDocumentSplitter
{
    private readonly ILogger<HierarchicalBookSplitter> _logger;

    public HierarchicalBookSplitter(ILogger<HierarchicalBookSplitter> logger)
    {
        _logger = logger;
    }

    public string Name => "hierarchical-book";

    public int EstimateDepth(string markdown, SplitContext context)
    {
        var wordCount = context.WordCount > 0
            ? context.WordCount
            : WordCounter.Count(markdown);

        if (wordCount < 5_000) return 0; // No split needed
        if (wordCount < 20_000) return 1; // Section-level only
        if (wordCount < 100_000) return 2; // Chapter -> section
        return 3; // Work -> chapter -> section
    }

    public Task<DocumentNode> SplitAsync(string markdown, SplitOptions options, CancellationToken ct = default)
    {
        // Pattern is selected by the caller (BookProcessorPlugin) based on detection.
        // Fall back to "novel" only when no pattern was specified.
        var patternName = options.PatternName ?? "novel";
        var pattern = StructuralPatterns.Get(patternName);

        _logger.LogDebug("Splitting with pattern '{Pattern}'", patternName);

        if (pattern == null)
            // Fall back to heading-based splitting
            return Task.FromResult(SplitByHeadings(markdown, options));

        var hierarchy = StructuralPatterns.GetLevelHierarchy(patternName);
        var root = SplitRecursive(markdown, pattern, hierarchy, 0, options, ct);

        return Task.FromResult(root);
    }

    private DocumentNode SplitRecursive(
        string text,
        StructuralPattern pattern,
        IReadOnlyList<string> levelHierarchy,
        int currentLevelIndex,
        SplitOptions options,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var levelLabel = currentLevelIndex < levelHierarchy.Count
            ? levelHierarchy[currentLevelIndex]
            : "passage";

        // Check max depth
        if (options.MaxDepth > 0 && currentLevelIndex >= options.MaxDepth)
            return CreateLeafNode(text, "passage", 0, currentLevelIndex);

        // Try to find boundaries at the current level
        if (currentLevelIndex < levelHierarchy.Count)
        {
            var boundaries = ChapterDetector.DetectBoundaries(text, pattern, levelLabel);

            if (boundaries.Count >= 2) // Need at least 2 boundaries to split meaningfully
                return BuildTreeFromBoundaries(
                    text, boundaries, levelLabel, pattern, levelHierarchy,
                    currentLevelIndex, options, ct);
        }

        // No boundaries found at this level — try next level down
        if (currentLevelIndex + 1 < levelHierarchy.Count)
            return SplitRecursive(text, pattern, levelHierarchy, currentLevelIndex + 1, options, ct);

        // No more levels — return as leaf
        return CreateLeafNode(text, "passage", 0, currentLevelIndex);
    }

    private DocumentNode BuildTreeFromBoundaries(
        string text,
        List<DetectedBoundary> boundaries,
        string levelLabel,
        StructuralPattern pattern,
        IReadOnlyList<string> levelHierarchy,
        int currentLevelIndex,
        SplitOptions options,
        CancellationToken ct)
    {
        var children = new List<DocumentNode>();

        // Handle content before the first boundary (prologue/introduction)
        if (boundaries[0].CharOffset > 200)
        {
            var preamble = text[..boundaries[0].CharOffset].Trim();
            if (preamble.Length > 0)
            {
                var preambleWords = WordCounter.Count(preamble);
                if (preambleWords > options.MinLeafWords)
                    children.Add(CreateLeafNode(preamble, "preamble", 0, currentLevelIndex + 1));
            }
        }

        // Split text between boundaries
        for (var i = 0; i < boundaries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var start = boundaries[i].CharOffset;
            var end = i + 1 < boundaries.Count
                ? boundaries[i + 1].CharOffset
                : text.Length;

            var segment = text[start..end].Trim();
            if (string.IsNullOrWhiteSpace(segment)) continue;

            var segmentWords = WordCounter.Count(segment);
            var title = boundaries[i].MarkerText;
            var sequence = boundaries[i].SequenceNumber > 0
                ? boundaries[i].SequenceNumber
                : i + 1;

            // If segment is large enough and there are deeper levels, recurse
            if (segmentWords > options.MaxLeafWords && currentLevelIndex + 1 < levelHierarchy.Count)
            {
                var childNode = SplitRecursive(
                    segment, pattern, levelHierarchy,
                    currentLevelIndex + 1, options, ct);

                // Wrap the recursive result under this boundary's title
                children.Add(childNode with
                {
                    Title = title,
                    Sequence = sequence,
                    Level = currentLevelIndex + 1,
                    LevelLabel = levelLabel
                });
            }
            else
            {
                // Leaf node
                children.Add(new DocumentNode
                {
                    Title = title,
                    Sequence = sequence,
                    Level = currentLevelIndex + 1,
                    LevelLabel = levelLabel,
                    Content = segment
                });
            }
        }

        // Build root node
        return new DocumentNode
        {
            Title = "Document",
            Sequence = 0,
            Level = currentLevelIndex,
            LevelLabel = "root",
            Children = children
        };
    }

    /// <summary>
    ///     Fallback: split by markdown headings when no pattern matches.
    /// </summary>
    private static DocumentNode SplitByHeadings(string markdown, SplitOptions options)
    {
        var lines = markdown.Split('\n');
        var children = new List<DocumentNode>();
        var currentContent = new StringBuilder();
        var currentTitle = "Introduction";
        var sequence = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                // Flush current section
                if (currentContent.Length > 0)
                {
                    children.Add(new DocumentNode
                    {
                        Title = currentTitle,
                        Sequence = sequence++,
                        Level = 1,
                        LevelLabel = "section",
                        Content = currentContent.ToString().Trim()
                    });
                    currentContent.Clear();
                }

                currentTitle = trimmed.TrimStart('#').Trim();
            }

            currentContent.AppendLine(line);
        }

        // Flush remaining content
        if (currentContent.Length > 0)
            children.Add(new DocumentNode
            {
                Title = currentTitle,
                Sequence = sequence,
                Level = 1,
                LevelLabel = "section",
                Content = currentContent.ToString().Trim()
            });

        return new DocumentNode
        {
            Title = "Document",
            Sequence = 0,
            Level = 0,
            LevelLabel = "root",
            Children = children
        };
    }

    private static DocumentNode CreateLeafNode(string content, string levelLabel, int sequence, int level)
    {
        return new DocumentNode
        {
            Title = ExtractTitle(content) ?? $"Passage {sequence + 1}",
            Sequence = sequence,
            Level = level,
            LevelLabel = levelLabel,
            Content = content
        };
    }

    private static string? ExtractTitle(string content)
    {
        var firstLine = content.Split('\n', 2)[0].Trim();
        if (firstLine.StartsWith('#'))
            return firstLine.TrimStart('#').Trim();
        if (firstLine.Length < 80 && firstLine.Length > 3)
            return firstLine;
        return null;
    }
}