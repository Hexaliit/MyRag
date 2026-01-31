using DoomSummarizer.Models;

namespace DoomSummarizer.Plugins;

/// <summary>
/// A splitter breaks structured content into a tree of <see cref="DocumentNode"/>s.
/// Different splitters handle different document structures (novels, plays, papers, etc.).
/// </summary>
public interface IDocumentSplitter
{
    /// <summary>Splitter name (e.g. "hierarchical-book", "academic-paper").</summary>
    string Name { get; }

    /// <summary>
    /// Estimate the best split depth for this content.
    /// Returns 0 if the splitter cannot handle this content.
    /// </summary>
    int EstimateDepth(string markdown, SplitContext context);

    /// <summary>Split the content into a hierarchical tree of document nodes.</summary>
    Task<DocumentNode> SplitAsync(string markdown, SplitOptions options, CancellationToken ct = default);
}

/// <summary>
/// Context for depth estimation — helps the splitter decide how deep to split.
/// </summary>
public record SplitContext
{
    /// <summary>Estimated word count of the document.</summary>
    public int WordCount { get; init; }

    /// <summary>Document title (may help with pattern matching).</summary>
    public string? Title { get; init; }

    /// <summary>Source file extension (e.g. ".pdf", ".txt").</summary>
    public string? FileExtension { get; init; }

    /// <summary>Detected document type hint.</summary>
    public string? DocumentType { get; init; }
}

/// <summary>
/// Options controlling how splitting is performed.
/// </summary>
public record SplitOptions
{
    /// <summary>Maximum depth to split to (0 = no limit).</summary>
    public int MaxDepth { get; init; }

    /// <summary>Minimum words per leaf node before further splitting stops.</summary>
    public int MinLeafWords { get; init; } = 100;

    /// <summary>Maximum words per leaf node (triggers further splitting).</summary>
    public int MaxLeafWords { get; init; } = 3000;

    /// <summary>Pattern name to use (e.g. "novel", "play", "anthology"). Null = auto-detect.</summary>
    public string? PatternName { get; init; }
}
