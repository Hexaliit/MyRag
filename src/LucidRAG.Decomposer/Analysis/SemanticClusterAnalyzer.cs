using System.Text.RegularExpressions;
using LucidRAG.Decomposer.Models;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;

namespace LucidRAG.Decomposer.Analysis;

/// <summary>
///     For queries without clear structural boundaries, uses embedding clustering
///     to detect multi-topic content. Splits query into overlapping n-gram windows,
///     embeds each, clusters by cosine similarity, and decomposes if 2+ distinct clusters.
/// </summary>
public partial class SemanticClusterAnalyzer : IQueryAnalyzer
{
    /// <summary>Minimum cosine similarity to consider two windows part of the same cluster.</summary>
    private const float ClusterThreshold = 0.50f;

    /// <summary>Minimum query word count to attempt clustering (short queries are single-topic).</summary>
    private const int MinWordCount = 8;

    /// <summary>N-gram window size for clustering.</summary>
    private const int WindowSize = 4;

    private readonly IEmbeddingService? _embedding;
    private readonly ILogger<SemanticClusterAnalyzer>? _logger;

    public SemanticClusterAnalyzer(IEmbeddingService? embedding = null, ILogger<SemanticClusterAnalyzer>? logger = null)
    {
        _embedding = embedding;
        _logger = logger;
    }

    public async Task<QuerySignals> AnalyzeAsync(string query, QuerySignals existing, CancellationToken ct = default)
    {
        // Skip if already decomposed by structural analyzer
        if (existing.ProposedNodes.Count > 1) return existing;
        if (_embedding == null) return existing;

        // Strip references (URLs, file paths, DOIs) before windowing  -  these create
        // noisy clusters since URL fragments are semantically meaningless as n-grams.
        var cleanedQuery = StripReferences(query);

        var words = cleanedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < MinWordCount) return existing;

        // Generate overlapping n-gram windows
        var windows = GenerateWindows(words, WindowSize);
        if (windows.Count < 2) return existing;

        // Embed all windows in a single batch
        var embeddings = await _embedding.EmbedBatchAsync(windows, ct);

        // Agglomerative clustering
        var clusters = AgglomerativeCluster(windows.ToArray(), embeddings, ClusterThreshold);

        if (clusters.Count < 2)
            return existing;

        _logger?.LogDebug("Semantic clustering found {Count} topic clusters in: {Query}",
            clusters.Count, query);

        var nodes = new List<QueryNode>(existing.ProposedNodes);
        var embeddingCache = new Dictionary<string, float[]>(existing.EmbeddingCache);

        foreach (var cluster in clusters.Take(4)) // Max 4 clusters
        {
            var clusterQuery = cluster.RepresentativePhrase;
            if (clusterQuery.Length < 5) continue;

            if (!embeddingCache.TryGetValue(clusterQuery, out var emb))
            {
                emb = await _embedding.EmbedAsync(clusterQuery, ct);
                embeddingCache[clusterQuery] = emb;
            }

            nodes.Add(new QueryNode
            {
                Query = clusterQuery,
                Type = QueryNodeType.Atomic,
                Embedding = emb,
                Depth = 0
            });
        }

        return existing with
        {
            ProposedNodes = nodes,
            EmbeddingCache = embeddingCache
        };
    }

    /// <summary>
    ///     Strip URLs, file paths, and DOIs from query text before windowing.
    ///     These reference tokens create noisy n-gram windows with no semantic value.
    /// </summary>
    internal static string StripReferences(string query)
    {
        // URLs: https://... or http://...
        var cleaned = ReferencePattern().Replace(query, " ");
        // Collapse multiple spaces
        return MultiSpacePattern().Replace(cleaned, " ").Trim();
    }

    [GeneratedRegex(
        @"https?://\S+|(?:[A-Za-z]:\\[^\s<>""']+|/(?:home|usr|var|tmp|etc|opt)/\S+|~/\S+)|(?:https?://doi\.org/)?10\.\d{4,}/\S+",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpacePattern();

    /// <summary>Generate overlapping windows of the given size from word array.</summary>
    internal static List<string> GenerateWindows(string[] words, int windowSize)
    {
        var windows = new List<string>();
        for (var i = 0; i <= words.Length - windowSize; i++)
            windows.Add(string.Join(" ", words.Skip(i).Take(windowSize)));
        return windows;
    }

    /// <summary>
    ///     Simple agglomerative clustering: assign each window to the first cluster
    ///     with similarity >= threshold, or create a new cluster.
    /// </summary>
    internal static List<SemanticCluster> AgglomerativeCluster(
        string[] windows, float[][] embeddings, float threshold)
    {
        var clusters = new List<SemanticCluster>();

        for (var i = 0; i < windows.Length; i++)
        {
            var assigned = false;
            foreach (var cluster in clusters)
            {
                var sim = ComplexityClassifier.CosineSimilarity(
                    embeddings[i], cluster.CentroidEmbedding);

                if (sim >= threshold)
                {
                    cluster.AddWindow(windows[i], embeddings[i]);
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
            {
                var newCluster = new SemanticCluster(windows[i], embeddings[i]);
                clusters.Add(newCluster);
            }
        }

        return clusters;
    }
}

/// <summary>
///     A cluster of semantically similar n-gram windows.
/// </summary>
internal class SemanticCluster
{
    private readonly List<float[]> _embeddings = [];
    private readonly List<string> _windows = [];

    public SemanticCluster(string initialWindow, float[] embedding)
    {
        _windows.Add(initialWindow);
        _embeddings.Add(embedding);
        CentroidEmbedding = embedding;
    }

    /// <summary>Running centroid embedding (average of all window embeddings).</summary>
    public float[] CentroidEmbedding { get; private set; }

    /// <summary>The longest window in the cluster (best representative phrase).</summary>
    public string RepresentativePhrase => _windows.OrderByDescending(w => w.Length).First();

    public void AddWindow(string window, float[] embedding)
    {
        _windows.Add(window);
        _embeddings.Add(embedding);

        // Update centroid (running average)
        var dim = CentroidEmbedding.Length;
        var newCentroid = new float[dim];
        for (var d = 0; d < dim; d++)
        {
            var sum = 0f;
            foreach (var emb in _embeddings)
                sum += emb[d];
            newCentroid[d] = sum / _embeddings.Count;
        }

        CentroidEmbedding = newCentroid;
    }
}