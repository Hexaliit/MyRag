using System.Text;
using System.Text.RegularExpressions;
using Mostlylucid.DocSummarizer.Services;

namespace DoomSummarizer.Services;

/// <summary>
/// Compresses conversation history into a dense context string that fits
/// within a target token budget. Uses a tiered strategy:
///   Small history:  Pass-through (no compression)
///   Medium history: Extractive (cosine similarity to current query)
///   Large history:  Abstractive (LLM summarization per chunk)
/// </summary>
public sealed partial class ContextCompressor
{
    private readonly IEmbeddingService _embedding;
    private readonly OllamaService? _ollama;

    // Rough chars-per-token estimate for English text
    private const double CharsPerToken = 3.5;

    public ContextCompressor(IEmbeddingService embedding, OllamaService? ollama)
    {
        _embedding = embedding;
        _ollama = ollama;
    }

    /// <summary>
    /// Compress conversation history into a context string for the synthesis prompt.
    /// </summary>
    public async Task<string> CompressAsync(
        List<(string question, string answer)> history,
        string currentQuery,
        int targetTokens = 2000,
        CancellationToken ct = default)
    {
        if (history.Count == 0)
            return "";

        // Build raw history text
        var raw = BuildRawHistory(history);
        var targetChars = (int)(targetTokens * CharsPerToken);

        // Tier 1: Small history - pass through
        if (raw.Length <= targetChars)
            return FormatContext(raw);

        // Tier 2: Medium history (1-3x target) - extractive
        if (raw.Length <= targetChars * 3)
            return await ExtractiveCompressAsync(history, currentQuery, targetChars, ct);

        // Tier 3: Large history (>3x target) - abstractive then extractive
        return await AbstractiveCompressAsync(history, currentQuery, targetChars, ct);
    }

    private static string BuildRawHistory(List<(string question, string answer)> history)
    {
        var sb = new StringBuilder();
        foreach (var (q, a) in history)
        {
            sb.AppendLine($"Q: {q}");
            sb.AppendLine($"A: {a}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatContext(string historyText)
    {
        return $"CONVERSATION CONTEXT:\n{historyText}\n";
    }

    /// <summary>
    /// Extractive compression: keep sentences most similar to the current query.
    /// Preserves chronological order.
    /// </summary>
    private async Task<string> ExtractiveCompressAsync(
        List<(string question, string answer)> history,
        string currentQuery,
        int targetChars,
        CancellationToken ct)
    {
        // Split all history into sentences with turn metadata
        var sentences = new List<(string text, int turnIndex)>();
        for (var i = 0; i < history.Count; i++)
        {
            var (q, a) = history[i];
            sentences.Add(($"Q: {q}", i));

            // Split answer into sentences
            var answerSentences = SplitSentences(a);
            foreach (var s in answerSentences)
            {
                if (s.Length > 10) // Skip very short fragments
                    sentences.Add(($"A: {s}", i));
            }
        }

        if (sentences.Count == 0)
            return "";

        // Embed query and all sentences
        float[] queryEmb;
        float[][] sentenceEmbs;
        try
        {
            queryEmb = await _embedding.EmbedAsync(currentQuery, ct);
            var texts = sentences.Select(s => s.text).ToArray();
            sentenceEmbs = await _embedding.EmbedBatchAsync(texts, ct);
        }
        catch
        {
            // Embedding failure: fall back to taking last N chars
            var raw = BuildRawHistory(history);
            return FormatContext(raw.Length > targetChars ? raw[^targetChars..] : raw);
        }

        // Score by similarity to current query
        var scored = sentences
            .Select((s, idx) => (s.text, s.turnIndex, sim: VectorMath.CosineSimilarity(queryEmb, sentenceEmbs[idx])))
            .OrderByDescending(x => x.sim)
            .ToList();

        // Take top sentences until budget filled, then re-sort by chronological order
        var selected = new List<(string text, int turnIndex, float sim)>();
        var charCount = 0;

        foreach (var item in scored)
        {
            if (charCount + item.text.Length > targetChars) break;
            selected.Add(item);
            charCount += item.text.Length + 1; // +1 for newline
        }

        // Re-sort by original turn order to maintain coherence
        selected.Sort((a, b) => a.turnIndex.CompareTo(b.turnIndex));

        var sb = new StringBuilder();
        foreach (var item in selected)
            sb.AppendLine(item.text);

        return FormatContext(sb.ToString());
    }

    /// <summary>
    /// Abstractive compression: LLM-summarize history chunks, then apply extractive.
    /// </summary>
    private async Task<string> AbstractiveCompressAsync(
        List<(string question, string answer)> history,
        string currentQuery,
        int targetChars,
        CancellationToken ct)
    {
        // If no LLM available, fall back to extractive on truncated history
        if (_ollama == null)
            return await ExtractiveCompressAsync(history, currentQuery, targetChars, ct);

        // Chunk history into ~2000 token windows
        var chunkSize = (int)(2000 * CharsPerToken);
        var chunks = ChunkHistory(history, chunkSize);

        // Summarize each chunk
        var summaries = new List<string>();
        foreach (var chunk in chunks)
        {
            try
            {
                var prompt = $"""
                    Summarize the key facts and topics from this conversation excerpt in 2-3 sentences.
                    Focus on information the user was asking about and what was learned.

                    EXCERPT:
                    {chunk}

                    SUMMARY:
                    """;

                var summary = await _ollama.SentinelGenerateAsync(prompt, temperature: 0.1, ct: ct);
                if (!string.IsNullOrWhiteSpace(summary))
                    summaries.Add(summary.Trim());
            }
            catch
            {
                // Skip failed chunks
            }
        }

        if (summaries.Count == 0)
            return await ExtractiveCompressAsync(history, currentQuery, targetChars, ct);

        var combined = string.Join("\n\n", summaries);

        // If summaries fit within budget, use them directly
        if (combined.Length <= targetChars)
            return FormatContext(combined);

        // Otherwise, apply extractive on the summaries (use "Summary" as question
        // placeholder since empty strings produce degenerate embeddings)
        var summaryTurns = summaries.Select(s => ("Summary", s)).ToList();
        return await ExtractiveCompressAsync(summaryTurns, currentQuery, targetChars, ct);
    }

    private static List<string> ChunkHistory(
        List<(string question, string answer)> history, int chunkSize)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var (q, a) in history)
        {
            var turn = $"Q: {q}\nA: {a}\n\n";

            if (current.Length + turn.Length > chunkSize && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.Append(turn);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString());

        return chunks;
    }

    private static List<string> SplitSentences(string text)
    {
        // Simple sentence splitter - split on sentence-ending punctuation
        return SentenceSplitRegex().Split(text)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceSplitRegex();
}
