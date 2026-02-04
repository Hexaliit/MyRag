namespace LucidRAG.Decomposer.Models;

/// <summary>
///     Represents a tool invocation that a QueryNode requires for execution.
///     "Sources are tools"  -  web search, file system, indexer, KB query, crawl
///     are all tools the decomposer routes to based on query intent.
///     Examples:
///     "Go to C:/test, index markdown files" → FileSystem + Index tools
///     "Search for latest AI news" → Search tool (existing source plugins)
///     "Crawl https://example.com" → Crawl tool (multi-page fetch)
///     "Tell me how long the documents are" → Analyze tool (runs after index)
/// </summary>
public record ToolAction
{
    /// <summary>Which tool capability this node requires.</summary>
    public required ToolKind Tool { get; init; }

    /// <summary>
    ///     Natural-language intent for the tool. Passed to the executor
    ///     so it knows what the tool should accomplish.
    ///     e.g., "Find markdown files matching '*summarizer*'"
    /// </summary>
    public required string Intent { get; init; }

    /// <summary>
    ///     Typed parameters for the tool. Keys are tool-specific.
    ///     FileSystem: path, pattern, recursive
    ///     Index: collection, format, source_filter
    ///     KbQuery: collection, top_k, min_relevance
    ///     Analyze: metric, output_format
    ///     Search: query, sources, max_items
    ///     Crawl: url, depth, pattern
    /// </summary>
    public Dictionary<string, string> Parameters { get; init; } = new();
}

/// <summary>
///     Tool capabilities the decomposer can route to.
///     Each maps to a concrete executor in the host application.
/// </summary>
public enum ToolKind
{
    /// <summary>
    ///     Web search via source plugins (search, gnews, hn, reddit, arxiv, etc.).
    ///     This is the default  -  existing DoomSummarizer fetch+score pipeline.
    /// </summary>
    Search,

    /// <summary>
    ///     Direct content fetch from a URL or file path.
    ///     Single-resource retrieval, not search.
    /// </summary>
    Fetch,

    /// <summary>
    ///     File system operations: find, list, read files.
    ///     Used when the query references a local path or asks to find files.
    ///     Params: path, pattern, recursive.
    /// </summary>
    FileSystem,

    /// <summary>
    ///     Index/ingest content into a knowledge base collection.
    ///     Used when the query asks to "index", "import", "build a KB", etc.
    ///     Params: collection, format, source_filter.
    /// </summary>
    Index,

    /// <summary>
    ///     Query an existing knowledge base collection.
    ///     Used when the query targets a named KB or asks about indexed content.
    ///     Params: collection, top_k, min_relevance.
    /// </summary>
    KbQuery,

    /// <summary>
    ///     Run analysis or computation on retrieved content.
    ///     Used for aggregation, measurement, statistics, summarization.
    ///     Params: metric, output_format.
    /// </summary>
    Analyze,

    /// <summary>
    ///     Multi-page web crawl starting from a URL.
    ///     Used when the query asks to "crawl", "spider", or "scrape" a site.
    ///     Params: url, depth, pattern.
    /// </summary>
    Crawl,

    /// <summary>
    ///     Transform or export data from one format to another.
    ///     Used for conversion, generation, or reformatting tasks.
    ///     Params: input_format, output_format, template.
    /// </summary>
    Transform
}