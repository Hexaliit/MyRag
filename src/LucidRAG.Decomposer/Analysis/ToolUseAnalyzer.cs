using System.Text.RegularExpressions;
using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
/// Detects tool-use patterns in queries. "Sources are tools"  -  the decomposer
/// routes sub-queries to specific tools based on query intent:
///
///   FileSystem → "go to C:/test, find markdown files"
///   Index      → "index those files, build a KB called 'myfiles'"
///   KbQuery    → "search the knowledge base for..."
///   Crawl      → "crawl https://example.com"
///   Analyze    → "tell me how long they are"
///   Transform  → "export as CSV", "convert to markdown"
///
/// Uses embedding archetype matching (not regex word lists) for intent detection,
/// with regex only for parameter extraction (paths, collection names, patterns).
/// </summary>
public partial class ToolUseAnalyzer : IQueryAnalyzer
{
    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<ToolUseAnalyzer>? _logger;

    private const float ToolMatchThreshold = 0.50f;

    // Archetype texts per tool kind (embedded lazily at first use)
    private static readonly Dictionary<ToolKind, string[]> ToolArchetypes = new()
    {
        [ToolKind.FileSystem] =
        [
            "Go to folder and find files",
            "List all files in directory matching pattern",
            "Read the files in this path",
            "Show me files with this name in that folder",
            "Open the documents in this directory"
        ],
        [ToolKind.Index] =
        [
            "Index these files into a knowledge base",
            "Build a collection from these documents",
            "Import and ingest content into the corpus",
            "Create a knowledge base called this name",
            "Add these documents to the database"
        ],
        [ToolKind.KbQuery] =
        [
            "Search the knowledge base for information",
            "Query the collection for matching documents",
            "Ask the indexed documents about this topic",
            "Find in my knowledge base",
            "Look up in the corpus for this answer"
        ],
        [ToolKind.Crawl] =
        [
            "Crawl this website and extract content",
            "Spider all pages on this site",
            "Scrape the website for information",
            "Fetch all pages from this domain",
            "Download and index this entire site"
        ],
        [ToolKind.Analyze] =
        [
            "Tell me how long the documents are",
            "Summarize the content and give statistics",
            "Count the words and calculate metrics",
            "Analyze the data and show trends",
            "Measure and compare the results"
        ],
        [ToolKind.Transform] =
        [
            "Export the results as CSV",
            "Convert the documents to markdown format",
            "Generate a report from the data",
            "Transform the output into a table",
            "Reformat the content as JSON"
        ]
    };

    // Cached archetype embeddings (computed once at first use)
    private Dictionary<ToolKind, float[][]>? _archetypeEmbeddings;

    public ToolUseAnalyzer(IEmbeddingService? embedding = null, ILogger<ToolUseAnalyzer>? logger = null)
    {
        _embedding = embedding;
        _logger = logger;
    }

    public async Task<QuerySignals> AnalyzeAsync(string query, QuerySignals existing, CancellationToken ct = default)
    {
        var tools = new List<ToolAction>(existing.DetectedTools);
        var nodes = new List<QueryNode>(existing.ProposedNodes);

        // Split query into clauses for per-clause tool detection
        var clauses = SplitIntoClauses(query);

        foreach (var clause in clauses)
        {
            var trimmed = clause.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < 5) continue;

            // Try deterministic extraction first (paths, collection names, URLs)
            var deterministicTool = ExtractDeterministic(trimmed);
            if (deterministicTool != null)
            {
                tools.Add(deterministicTool);
                nodes.Add(CreateToolNode(trimmed, deterministicTool));
                _logger?.LogDebug("Deterministic tool detection: {Tool} for clause: {Clause}",
                    deterministicTool.Tool, trimmed);
                continue;
            }

            // Fall back to embedding archetype matching
            if (_embedding != null)
            {
                var embeddingTool = await MatchToolArchetypeAsync(trimmed, existing.EmbeddingCache, ct);
                if (embeddingTool != null)
                {
                    // Enrich with extracted parameters
                    var enriched = EnrichWithParameters(trimmed, embeddingTool);
                    tools.Add(enriched);
                    nodes.Add(CreateToolNode(trimmed, enriched));
                    _logger?.LogDebug("Archetype tool detection: {Tool} for clause: {Clause}",
                        enriched.Tool, trimmed);
                }
            }
        }

        if (tools.Count > existing.DetectedTools.Count)
        {
            _logger?.LogDebug("Detected {Count} tool actions in query: {Query}",
                tools.Count - existing.DetectedTools.Count, query);
        }

        // If we detected tool actions, upgrade complexity
        var complexity = existing.Complexity;
        if (tools.Count > 0 && complexity == QueryComplexity.Simple)
            complexity = QueryComplexity.Moderate;
        if (tools.Count > 1)
            complexity = QueryComplexity.Complex;

        return existing with
        {
            DetectedTools = tools,
            ProposedNodes = nodes,
            Complexity = complexity
        };
    }

    /// <summary>
    /// Deterministic tool extraction  -  handles clear structural signals
    /// like file paths, collection names, and crawl URLs without embeddings.
    /// </summary>
    private static ToolAction? ExtractDeterministic(string clause)
    {
        var lower = clause.ToLowerInvariant();

        // FileSystem: clause contains a file path + action verb
        var pathMatch = FilePathInClause().Match(clause);
        if (pathMatch.Success && HasFileVerb(lower))
        {
            var path = pathMatch.Value;
            var pattern = ExtractFilePattern(lower);
            return new ToolAction
            {
                Tool = ToolKind.FileSystem,
                Intent = clause,
                Parameters = new Dictionary<string, string>
                {
                    ["path"] = path,
                    ["pattern"] = pattern ?? "*",
                    ["recursive"] = lower.Contains("recursive") || lower.Contains("all sub") ? "true" : "false"
                }
            };
        }

        // Index: explicit "index", "build a kb", "create a knowledge base", "build a collection"
        if (HasIndexVerb(lower))
        {
            var collectionName = ExtractCollectionName(clause);
            return new ToolAction
            {
                Tool = ToolKind.Index,
                Intent = clause,
                Parameters = new Dictionary<string, string>
                {
                    ["collection"] = collectionName ?? "default"
                }
            };
        }

        // Crawl: "crawl" + URL
        if (lower.Contains("crawl") || lower.Contains("spider") || lower.Contains("scrape"))
        {
            var urls = ReferenceExtractor.ExtractUrls(clause);
            if (urls.Count > 0)
            {
                return new ToolAction
                {
                    Tool = ToolKind.Crawl,
                    Intent = clause,
                    Parameters = new Dictionary<string, string>
                    {
                        ["url"] = urls[0],
                        ["depth"] = "2"
                    }
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Match clause against tool archetype embeddings.
    /// Returns the best matching tool if score >= threshold, null otherwise.
    /// </summary>
    private async Task<ToolAction?> MatchToolArchetypeAsync(
        string clause,
        Dictionary<string, float[]> embeddingCache,
        CancellationToken ct)
    {
        await EnsureArchetypeEmbeddingsAsync(ct);

        // Get or compute clause embedding
        if (!embeddingCache.TryGetValue(clause, out var clauseEmbedding))
        {
            clauseEmbedding = await _embedding!.EmbedAsync(clause, ct);
            embeddingCache[clause] = clauseEmbedding;
        }

        var bestTool = (ToolKind?)null;
        var bestScore = 0f;

        foreach (var (tool, archetypeEmbeddings) in _archetypeEmbeddings!)
        {
            foreach (var archetype in archetypeEmbeddings)
            {
                var sim = ComplexityClassifier.CosineSimilarity(clauseEmbedding, archetype);
                if (sim > bestScore)
                {
                    bestScore = sim;
                    bestTool = tool;
                }
            }
        }

        if (bestTool != null && bestScore >= ToolMatchThreshold)
        {
            return new ToolAction
            {
                Tool = bestTool.Value,
                Intent = clause,
            };
        }

        return null;
    }

    /// <summary>
    /// Enrich a tool action detected via archetypes with extracted parameters.
    /// </summary>
    private static ToolAction EnrichWithParameters(string clause, ToolAction action)
    {
        var parameters = new Dictionary<string, string>(action.Parameters);

        switch (action.Tool)
        {
            case ToolKind.FileSystem:
                var path = FilePathInClause().Match(clause);
                if (path.Success) parameters["path"] = path.Value;
                var pattern = ExtractFilePattern(clause.ToLowerInvariant());
                if (pattern != null) parameters["pattern"] = pattern;
                break;

            case ToolKind.Index:
                var collection = ExtractCollectionName(clause);
                if (collection != null) parameters["collection"] = collection;
                break;

            case ToolKind.Crawl:
                var urls = ReferenceExtractor.ExtractUrls(clause);
                if (urls.Count > 0) parameters["url"] = urls[0];
                break;

            case ToolKind.KbQuery:
                var kbCollection = ExtractCollectionName(clause);
                if (kbCollection != null) parameters["collection"] = kbCollection;
                break;

            case ToolKind.Analyze:
                if (clause.Contains("long", StringComparison.OrdinalIgnoreCase)
                    || clause.Contains("length", StringComparison.OrdinalIgnoreCase))
                    parameters["metric"] = "length";
                if (clause.Contains("count", StringComparison.OrdinalIgnoreCase)
                    || clause.Contains("how many", StringComparison.OrdinalIgnoreCase))
                    parameters["metric"] = "count";
                break;

            case ToolKind.Transform:
                if (clause.Contains("csv", StringComparison.OrdinalIgnoreCase))
                    parameters["output_format"] = "csv";
                if (clause.Contains("json", StringComparison.OrdinalIgnoreCase))
                    parameters["output_format"] = "json";
                if (clause.Contains("markdown", StringComparison.OrdinalIgnoreCase))
                    parameters["output_format"] = "markdown";
                break;
        }

        return action with { Parameters = parameters };
    }

    private static QueryNode CreateToolNode(string clause, ToolAction action)
    {
        return new QueryNode
        {
            Query = clause,
            Type = QueryNodeType.ToolAction,
            ToolAction = action,
            Depth = 0
        };
    }

    private async Task EnsureArchetypeEmbeddingsAsync(CancellationToken ct)
    {
        if (_archetypeEmbeddings != null) return;

        // Flatten all tool archetype texts into a single batch call, then slice results back.
        var allTexts = new List<string>();
        var slices = new List<(ToolKind tool, int offset, int count)>();

        foreach (var (tool, texts) in ToolArchetypes)
        {
            slices.Add((tool, allTexts.Count, texts.Length));
            allTexts.AddRange(texts);
        }

        var allEmbeddings = await _embedding!.EmbedBatchAsync(allTexts, ct);

        _archetypeEmbeddings = new Dictionary<ToolKind, float[][]>();
        foreach (var (tool, offset, count) in slices)
        {
            _archetypeEmbeddings[tool] = allEmbeddings[offset..(offset + count)];
        }
    }

    /// <summary>
    /// Split query into clauses on sentence boundaries and common verb-phrase boundaries.
    /// Tool-use queries often chain actions: "go to X, index Y, then tell me Z"
    /// </summary>
    private static List<string> SplitIntoClauses(string query)
    {
        // Split on sentence-ending punctuation, commas followed by verb phrases,
        // and "then"/"and then" connectors
        var clauses = new List<string>();
        var parts = ClauseSplitter().Split(query);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length >= 5) clauses.Add(trimmed);
        }

        // If no splits found, return the whole query
        return clauses.Count == 0 ? [query] : clauses;
    }

    private static bool HasFileVerb(string lower) =>
        lower.Contains("go to") || lower.Contains("find") || lower.Contains("list") ||
        lower.Contains("read") || lower.Contains("open") || lower.Contains("show") ||
        lower.Contains("get") || lower.Contains("look in") || lower.Contains("folder") ||
        lower.Contains("directory");

    private static bool HasIndexVerb(string lower) =>
        lower.Contains("index") || lower.Contains("ingest") || lower.Contains("import") ||
        (lower.Contains("build") && (lower.Contains("knowledge") || lower.Contains("kb") || lower.Contains("collection"))) ||
        (lower.Contains("create") && (lower.Contains("knowledge") || lower.Contains("kb") || lower.Contains("collection")));

    private static string? ExtractFilePattern(string lower)
    {
        // "files with 'summarizer' in the name" → *summarizer*
        var withMatch = Regex.Match(lower, @"with\s+['""]?(\w+)['""]?\s+in\s+the\s+name");
        if (withMatch.Success) return $"*{withMatch.Groups[1].Value}*";

        // "named 'summarizer'" or "called 'summarizer'"
        var namedMatch = Regex.Match(lower, @"(?:named|called)\s+['""]?(\w+)['""]?");
        if (namedMatch.Success) return $"*{namedMatch.Groups[1].Value}*";

        // "markdown files" → *.md
        if (lower.Contains("markdown")) return "*.md";
        if (lower.Contains("json files")) return "*.json";
        if (lower.Contains("text files") || lower.Contains("txt files")) return "*.txt";
        if (lower.Contains("pdf files")) return "*.pdf";
        if (lower.Contains("csv files")) return "*.csv";

        return null;
    }

    private static string? ExtractCollectionName(string clause)
    {
        // "called 'myfiles'" or "named 'myfiles'"
        var match = Regex.Match(clause, @"(?:called|named)\s+['""](\w+)['""]", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;

        // "called myfiles" (no quotes)
        match = Regex.Match(clause, @"(?:called|named)\s+(\w+)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    // File path in clause: Windows (C:\...) or Unix (/home/..., ~/...)
    [GeneratedRegex(@"(?:[A-Za-z]:[/\\][^\s,;""']+|/(?:home|usr|var|tmp|etc|opt)/[^\s,;""']+|~/[^\s,;""']+)", RegexOptions.None)]
    private static partial Regex FilePathInClause();

    // Clause splitter: split on period, semicolon, "then", "and then", comma + verb
    [GeneratedRegex(@"[.;]\s+|(?:,?\s*(?:and\s+)?then\s+)|,\s+(?=(?:go|find|index|build|create|tell|show|list|read|crawl|search|query|import|analyze|export|convert|open|get))", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseSplitter();
}
