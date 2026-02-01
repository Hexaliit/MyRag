using System.Text.RegularExpressions;

namespace DoomSummarizer.Services;

/// <summary>
/// TextRank-style sentence centrality extraction.
/// Selects the most informative sentences from a text by:
/// 1. Splitting into sentences
/// 2. Computing pairwise embedding similarity (graph edges)
/// 3. Running PageRank-style power iteration to find central sentences
/// 4. Returning top sentences in original order (preserves narrative flow)
///
/// Based on: Mihalcea & Tarau (TextRank, 2004), Erkan & Radev (LexRank, 2004).
/// Uses local ONNX embeddings — deterministic, cheap, no LLM needed.
/// </summary>
public static partial class TextRankExtractor
{
    private const float SimilarityThreshold = 0.15f;
    private const float DampingFactor = 0.85f;
    private const int MaxIterations = 20;
    private const float ConvergenceThreshold = 0.001f;

    /// <summary>
    /// Extract the most central/informative sentences from text up to maxChars.
    /// Falls back to simple truncation if text is short or embedding fails.
    /// </summary>
    /// <param name="text">Full article text (Markdown or plain text).</param>
    /// <param name="embedder">Embedding function (e.g. EmbeddingService.Embed).</param>
    /// <param name="maxChars">Maximum output length in characters.</param>
    /// <returns>Most informative sentences joined, respecting original order.</returns>
    public static string ExtractKeySentences(string text, Func<string, float[]> embedder, int maxChars = 800)
        => ExtractKeySentencesCore(text, embedder, null, maxChars);

    /// <summary>
    /// Batch-optimized overload: embeds all sentences in a single batch call
    /// instead of N sequential calls. Significantly faster with ONNX runtime.
    /// </summary>
    public static string ExtractKeySentences(string text, Func<string, float[]> embedder,
        Func<string[], float[][]>? batchEmbedder, int maxChars = 800)
        => ExtractKeySentencesCore(text, embedder, batchEmbedder, maxChars);

    private static string ExtractKeySentencesCore(string text, Func<string, float[]> embedder,
        Func<string[], float[][]>? batchEmbedder, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Length <= maxChars) return text;

        // Split into sentences
        var sentences = SplitSentences(text);
        if (sentences.Count <= 3)
            return text.Length > maxChars ? text[..maxChars] + "..." : text;

        // Embed sentences — batch when possible, sequential fallback
        var embeddings = new float[sentences.Count][];
        try
        {
            if (batchEmbedder != null)
            {
                // Batch path: collect embeddable sentences, single ONNX forward pass
                var embeddableIndices = new List<int>();
                var embeddableTexts = new List<string>();
                for (var i = 0; i < sentences.Count; i++)
                {
                    if (sentences[i].Length >= 20)
                    {
                        embeddableIndices.Add(i);
                        embeddableTexts.Add(sentences[i]);
                    }
                }

                if (embeddableTexts.Count > 0)
                {
                    var batchResults = batchEmbedder(embeddableTexts.ToArray());
                    for (var i = 0; i < embeddableIndices.Count; i++)
                        embeddings[embeddableIndices[i]] = batchResults[i];
                }
            }
            else
            {
                // Sequential fallback
                for (var i = 0; i < sentences.Count; i++)
                {
                    if (sentences[i].Length >= 20)
                        embeddings[i] = embedder(sentences[i]);
                }
            }
        }
        catch
        {
            // Embedding failure — fall back to simple truncation
            return text[..maxChars] + "...";
        }

        // Build similarity graph + run PageRank
        var scores = ComputeTextRank(embeddings, sentences.Count);

        // Select top sentences by centrality, maintaining original order
        var indexed = scores
            .Select((score, idx) => (idx, score))
            .OrderByDescending(x => x.score)
            .ToList();

        // Greedily select sentences until we hit maxChars
        var selectedIndices = new SortedSet<int>();
        var totalChars = 0;
        foreach (var (idx, _) in indexed)
        {
            if (totalChars + sentences[idx].Length > maxChars && selectedIndices.Count > 0)
                break;
            selectedIndices.Add(idx);
            totalChars += sentences[idx].Length + 1; // +1 for space
        }

        // Join in original order to preserve narrative flow
        return string.Join(" ", selectedIndices.Select(i => sentences[i]));
    }

    /// <summary>
    /// Split text into sentences, stripping Markdown formatting.
    /// </summary>
    private static List<string> SplitSentences(string text)
    {
        // Strip Markdown headings, code fences, list markers
        text = MarkdownHeading().Replace(text, "");
        text = text.Replace("```", "").Replace("~~~", "");
        text = MarkdownListMarker().Replace(text, "");

        // Split on sentence boundaries: .!? followed by space or newline
        var raw = SentenceBoundary().Split(text);
        var sentences = new List<string>();

        foreach (var s in raw)
        {
            var trimmed = s.Trim();
            // Skip empty, very short (likely fragments), or Markdown artifacts
            if (trimmed.Length < 15) continue;
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|')) continue; // Table rows
            if (trimmed.All(c => c is '-' or '=' or '*' or '_')) continue; // Horizontal rules

            sentences.Add(trimmed);
        }

        return sentences;
    }

    /// <summary>
    /// Compute TextRank scores using power iteration on the similarity graph.
    /// </summary>
    private static float[] ComputeTextRank(float[][] embeddings, int n)
    {
        // Build adjacency matrix (undirected weighted graph)
        var graph = new float[n, n];
        for (var i = 0; i < n; i++)
        {
            if (embeddings[i] == null) continue;
            for (var j = i + 1; j < n; j++)
            {
                if (embeddings[j] == null) continue;
                var sim = CosineSimilarity(embeddings[i], embeddings[j]);
                if (sim > SimilarityThreshold)
                {
                    graph[i, j] = sim;
                    graph[j, i] = sim;
                }
            }
        }

        // Compute row sums for normalization
        var rowSums = new float[n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                rowSums[i] += graph[i, j];

        // Power iteration (PageRank)
        var scores = new float[n];
        Array.Fill(scores, 1.0f / n);
        var newScores = new float[n];

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            var maxDiff = 0f;
            for (var i = 0; i < n; i++)
            {
                var sum = 0f;
                for (var j = 0; j < n; j++)
                {
                    if (rowSums[j] > 0)
                        sum += graph[j, i] / rowSums[j] * scores[j];
                }
                newScores[i] = (1 - DampingFactor) / n + DampingFactor * sum;
                maxDiff = Math.Max(maxDiff, Math.Abs(newScores[i] - scores[i]));
            }

            (scores, newScores) = (newScores, scores);
            if (maxDiff < ConvergenceThreshold) break;
        }

        return scores;
    }

    /// <summary>
    /// Delegates to SIMD-accelerated <see cref="VectorMath.CosineSimilarity"/>.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b) =>
        VectorMath.CosineSimilarity(a, b);

    [GeneratedRegex(@"^#{1,6}\s+.*$", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^\s*[-*+]\s+|\s*\d+\.\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownListMarker();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z\""'\(\[])", RegexOptions.Compiled)]
    private static partial Regex SentenceBoundary();
}
