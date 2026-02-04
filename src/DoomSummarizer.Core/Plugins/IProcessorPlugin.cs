using DoomSummarizer.Models;
using Microsoft.Extensions.Logging;

namespace DoomSummarizer.Plugins;

/// <summary>
///     A processor plugin bundles readers, splitters, templates, prompts, and strategies
///     for a specific document domain (e.g. books, research papers, legal documents).
/// </summary>
public interface IProcessorPlugin
{
    /// <summary>Plugin identity and capabilities.</summary>
    ProcessorPluginMetadata Metadata { get; }

    /// <summary>Document readers this plugin provides or requires.</summary>
    IReadOnlyList<IDocumentReader> Readers { get; }

    /// <summary>Splitters this plugin provides (e.g. chapter splitter, act splitter).</summary>
    IReadOnlyList<IDocumentSplitter> Splitters { get; }

    /// <summary>YAML template definitions shipped as embedded resources.</summary>
    IReadOnlyList<TemplateDefinition> Templates { get; }

    /// <summary>
    ///     One-time initialization. Called once after registration.
    ///     Use to load embedded resources, validate dependencies, etc.
    /// </summary>
    Task InitializeAsync(ProcessorPluginServices services, CancellationToken ct = default);

    /// <summary>Can this plugin handle the given content?</summary>
    bool CanProcess(string markdown, ProcessingContext context);

    /// <summary>
    ///     Process: split + enrich + return structured result.
    ///     The plugin decides the best splitting strategy based on content analysis.
    /// </summary>
    Task<ProcessorResult> ProcessAsync(string markdown, ProcessorOptions options, CancellationToken ct = default);
}

/// <summary>
///     Describes a processor plugin's identity and capabilities.
/// </summary>
public record ProcessorPluginMetadata
{
    /// <summary>Unique plugin name (e.g. "books", "academic", "legal").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Short description.</summary>
    public required string Description { get; init; }

    /// <summary>Plugin version.</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>File extensions this plugin can process.</summary>
    public IReadOnlyList<string> SupportedExtensions { get; init; } = [];

    /// <summary>Minimum word count before this plugin activates (below this, simpler processing is fine).</summary>
    public int MinActivationWords { get; init; } = 5000;

    /// <summary>Document types this plugin handles.</summary>
    public IReadOnlyList<string> DocumentTypes { get; init; } = [];
}

/// <summary>
///     Service bag injected into processor plugins during initialization.
/// </summary>
public record ProcessorPluginServices
{
    /// <summary>Logging.</summary>
    public required ILoggerFactory LoggerFactory { get; init; }
}

/// <summary>
///     Context provided to <see cref="IProcessorPlugin.CanProcess" /> for activation decisions.
/// </summary>
public record ProcessingContext
{
    /// <summary>Estimated word count of the document.</summary>
    public int WordCount { get; init; }

    /// <summary>Source file name (for extension-based matching).</summary>
    public string? FileName { get; init; }

    /// <summary>Detected document type hint (e.g. "fiction", "academic").</summary>
    public string? DocumentType { get; init; }

    /// <summary>Document title.</summary>
    public string? Title { get; init; }
}

/// <summary>
///     Options for <see cref="IProcessorPlugin.ProcessAsync" />.
/// </summary>
public record ProcessorOptions
{
    /// <summary>Strategy name to use (null = auto-select).</summary>
    public string? Strategy { get; init; }

    /// <summary>Split options for the underlying splitter.</summary>
    public SplitOptions SplitOptions { get; init; } = new();

    /// <summary>Source file path (for reader selection).</summary>
    public string? FilePath { get; init; }

    /// <summary>Collection name for storage.</summary>
    public string? CollectionName { get; init; }
}

/// <summary>
///     Result of plugin processing.
/// </summary>
public record ProcessorResult
{
    /// <summary>The document tree produced by splitting.</summary>
    public required DocumentNode DocumentTree { get; init; }

    /// <summary>Detected document type.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Strategy that was used.</summary>
    public required string StrategyUsed { get; init; }

    /// <summary>Total leaf nodes (segments ready for embedding).</summary>
    public int LeafCount => DocumentTree.LeafCount;

    /// <summary>Maximum depth of the tree.</summary>
    public int MaxDepth => ComputeMaxDepth(DocumentTree);

    /// <summary>Structural weight assignments for position-aware retrieval.</summary>
    public StructuralWeights Weights { get; init; } = new();

    /// <summary>Per-node metadata (e.g. detected characters, themes per chapter).</summary>
    public Dictionary<string, Dictionary<string, string>> NodeMetadata { get; init; } = new();

    private static int ComputeMaxDepth(DocumentNode node)
    {
        return node.IsLeaf ? node.Level : node.Children.Max(c => ComputeMaxDepth(c));
    }
}

/// <summary>
///     Structural weight assignments for position-aware retrieval.
///     First/last chapters, climax sections, etc. get boosted during retrieval.
/// </summary>
public record StructuralWeights
{
    /// <summary>
    ///     Weight multipliers by node title or path.
    ///     Key: node breadcrumb path, Value: weight multiplier (1.0 = normal).
    ///     Opening/closing chapters, abstracts, conclusions get higher weights.
    /// </summary>
    public Dictionary<string, double> NodeWeights { get; init; } = new();

    /// <summary>
    ///     Weight multipliers by position category.
    ///     Standard categories: "opening", "closing", "climax", "introduction", "conclusion".
    /// </summary>
    public Dictionary<string, double> PositionWeights { get; init; } = new()
    {
        ["opening"] = 1.3, // First chapter/section — sets up context
        ["closing"] = 1.3, // Last chapter/section — resolution/conclusion
        ["introduction"] = 1.2, // Formal introduction sections
        ["conclusion"] = 1.2, // Formal conclusion sections
        ["abstract"] = 1.4, // Academic abstracts — highest density
        ["climax"] = 1.1 // Narrative climax (detected by position heuristic)
    };

    /// <summary>
    ///     Get the structural weight for a node. Checks explicit NodeWeights first,
    ///     then falls back to position-based defaults from PositionWeights.
    ///     Title-based detection is done at ingestion time by the plugin's ComputeStructuralWeights.
    /// </summary>
    public double GetWeight(int sequence, int siblingCount, string? title = null)
    {
        // Check explicit node weight first (populated by plugin during processing)
        if (title != null && NodeWeights.TryGetValue(title, out var explicitWeight))
            return explicitWeight;

        // Position-based fallbacks
        if (sequence == 0)
            return PositionWeights.GetValueOrDefault("opening", 1.0);

        if (siblingCount > 0 && sequence == siblingCount - 1)
            return PositionWeights.GetValueOrDefault("closing", 1.0);

        // Climax heuristic: ~70-80% through the narrative
        if (siblingCount >= 5)
        {
            var position = (double)sequence / siblingCount;
            if (position is >= 0.65 and <= 0.85)
                return PositionWeights.GetValueOrDefault("climax", 1.0);
        }

        return 1.0;
    }
}