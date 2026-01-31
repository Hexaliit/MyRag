using System.Text.Json;

namespace DoomSummarizer.Services;

/// <summary>
/// Result of document type detection with source attribution.
/// </summary>
public record DocumentTypeResult
{
    /// <summary>Detected type (fiction, nonfiction, academic, technical, anthology, play, collection, unknown).</summary>
    public required string Type { get; init; }

    /// <summary>Confidence (0-1) for the primary type.</summary>
    public required double Confidence { get; init; }

    /// <summary>Detection source: "heuristic" or "sentinel".</summary>
    public required string Source { get; init; }
}

/// <summary>
/// Core document type detector with LLM sentinel fallback.
/// Runs fast heuristic signals first (caller provides those), then
/// falls back to a sentinel LLM when confidence is below threshold.
/// Skips cover pages and front matter to find substantial prose for
/// the sentinel to classify.
/// </summary>
public class DocumentTypeDetector
{
    private readonly OllamaService? _ollama;

    /// <summary>
    /// Heuristic confidence at or above this value trusts the heuristic.
    /// Below this triggers sentinel fallback (if available).
    /// This is the single source of truth for the threshold — callers
    /// should NOT duplicate this check.
    /// </summary>
    public double LlmFallbackThreshold { get; init; } = 0.45;

    // Chunk finding parameters
    private const int ChunkTargetLength = 1500;
    private const int MinProseLineLength = 50;
    private const int ConsecutiveProseLines = 3;

    public DocumentTypeDetector(OllamaService? ollama = null)
    {
        _ollama = ollama;
    }

    /// <summary>
    /// Classify a document using pre-computed heuristic results,
    /// falling back to sentinel LLM when confidence is low.
    /// Callers should always call this — the threshold decision is internal.
    /// </summary>
    /// <param name="content">Full document text.</param>
    /// <param name="heuristicType">Best-guess type from heuristic detection.</param>
    /// <param name="heuristicConfidence">Heuristic confidence (0-1).</param>
    /// <param name="heuristicSignals">Summary of heuristic signals (for sentinel context).</param>
    /// <param name="fileName">Original filename (helps sentinel classify).</param>
    /// <param name="wordCount">Document word count.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DocumentTypeResult> ClassifyAsync(
        string content,
        string heuristicType,
        double heuristicConfidence,
        IReadOnlyList<string>? heuristicSignals = null,
        string? fileName = null,
        int? wordCount = null,
        CancellationToken ct = default)
    {
        // High confidence → trust the heuristic
        if (heuristicConfidence >= LlmFallbackThreshold || _ollama == null)
        {
            return new DocumentTypeResult
            {
                Type = heuristicType,
                Confidence = heuristicConfidence,
                Source = "heuristic"
            };
        }

        // Low confidence → find substantial text and ask sentinel
        try
        {
            var chunk = FindSubstantialChunk(content);
            return await ClassifyWithSentinelAsync(
                chunk, heuristicType, heuristicConfidence,
                heuristicSignals, fileName, wordCount, ct);
        }
        catch
        {
            // Sentinel failed — return heuristic as-is
            return new DocumentTypeResult
            {
                Type = heuristicType,
                Confidence = heuristicConfidence,
                Source = "heuristic"
            };
        }
    }

    /// <summary>
    /// Find the first substantial prose chunk, skipping cover pages,
    /// title pages, copyright blocks, and other front matter confounders.
    /// Looks for 3+ consecutive lines of real prose (sentences, dialogue,
    /// narrative) before extracting a chunk.
    /// </summary>
    public static string FindSubstantialChunk(string content, int targetLength = ChunkTargetLength)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content ?? "";

        var lines = content.Split('\n');
        var candidateStart = -1;
        var consecutiveCount = 0;
        var currentPos = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var isProse = trimmed.Length >= MinProseLineLength
                          && !IsFrontMatterLine(trimmed)
                          && !IsMetadataLine(trimmed)
                          && HasSentenceStructure(trimmed);

            if (isProse)
            {
                if (consecutiveCount == 0)
                    candidateStart = currentPos;
                consecutiveCount++;

                if (consecutiveCount >= ConsecutiveProseLines && candidateStart >= 0)
                {
                    var end = Math.Min(content.Length, candidateStart + targetLength);
                    return content[candidateStart..end];
                }
            }
            else
            {
                consecutiveCount = 0;
            }

            currentPos += line.Length + 1;
        }

        // Fallback: take content from ~20% in (skip likely front matter)
        var skipAmount = Math.Min(content.Length / 5, 2000);
        var fallbackEnd = Math.Min(content.Length, skipAmount + targetLength);
        if (skipAmount >= content.Length)
            return content;
        return content[skipAmount..fallbackEnd];
    }

    private async Task<DocumentTypeResult> ClassifyWithSentinelAsync(
        string textChunk,
        string heuristicType,
        double heuristicConfidence,
        IReadOnlyList<string>? signals,
        string? fileName,
        int? wordCount,
        CancellationToken ct)
    {
        var signalSummary = signals is { Count: > 0 }
            ? string.Join(", ", signals.Take(8))
            : "none";

        var prompt = $$"""
            Classify this document excerpt into one document type.
            Respond with ONLY a JSON object, no other text.

            Metadata:
            - Filename: {{fileName ?? "unknown"}}
            - Word count: {{wordCount?.ToString("N0") ?? "unknown"}}
            - Heuristic guess: {{heuristicType}} ({{heuristicConfidence:P0}} confidence)
            - Signals: {{signalSummary}}

            Valid types: fiction, nonfiction, academic, technical, anthology, play, collection

            Text excerpt (after skipping front matter):
            ---
            {{textChunk}}
            ---

            Respond ONLY with: {"type": "one_of_the_types_above", "confidence": 0.0_to_1.0}
            """;

        var response = await _ollama!.SentinelGenerateAsync(prompt, null, 0.1, ct);

        // Parse JSON from response
        var jsonStart = response.IndexOf('{');
        var jsonEnd = response.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var jsonStr = response[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString()?.ToLowerInvariant() ?? heuristicType
                : heuristicType;

            var confidence = root.TryGetProperty("confidence", out var confProp)
                           && confProp.TryGetDouble(out var confVal)
                ? confVal
                : 0.6;

            // Validate known type
            if (!IsKnownType(type))
                type = heuristicType;

            return new DocumentTypeResult
            {
                Type = type,
                Confidence = Math.Clamp(confidence, 0.0, 1.0),
                Source = "sentinel"
            };
        }

        // Couldn't parse — fall back to heuristic
        return new DocumentTypeResult
        {
            Type = heuristicType,
            Confidence = heuristicConfidence,
            Source = "heuristic"
        };
    }

    private static bool IsKnownType(string type) =>
        type is "fiction" or "nonfiction" or "academic"
            or "technical" or "anthology" or "play"
            or "collection";

    /// <summary>
    /// Detect lines that look like front matter / cover page content.
    /// Uses specific phrases to avoid false positives on prose that
    /// merely mentions these topics.
    /// </summary>
    private static bool IsFrontMatterLine(string line)
    {
        var lower = line.ToLowerInvariant();

        // Copyright, publisher, ISBN
        if (lower.Contains("copyright") || lower.Contains("isbn") ||
            lower.Contains("published by") || lower.Contains("all rights reserved") ||
            lower.Contains("printed in") || lower.Contains("first edition") ||
            lower.Contains("first published"))
            return true;

        // Dedication, acknowledgments, TOC (exact-match headings only)
        if (lower is "dedication" or "acknowledgments" or "acknowledgements"
                   or "table of contents" or "contents" or "foreword" or "preface")
            return true;

        // Project Gutenberg boilerplate (specific phrases, not bare "ebook")
        if (lower.Contains("project gutenberg") || lower.StartsWith("***"))
            return true;

        // eBook licensing boilerplate (specific multi-word phrases)
        if (lower.Contains("this ebook") || lower.Contains("free distribution") ||
            lower.Contains("terms of use") || lower.Contains("use of this ebook") ||
            lower.Contains("gutenberg license"))
            return true;

        return false;
    }

    /// <summary>
    /// Detect metadata-style lines (short labels, all-caps headings, mostly non-alpha).
    /// </summary>
    private static bool IsMetadataLine(string line)
    {
        // Very short lines are likely titles/labels, not prose
        if (line.Length < 15) return true;

        // All-caps lines under 80 chars (headings, titles)
        if (line == line.ToUpperInvariant() && line.Length < 80) return true;

        // Lines that are mostly non-alphabetic (dates, numbers, page markers)
        var alphaRatio = (double)line.Count(char.IsLetter) / line.Length;
        if (alphaRatio < 0.5) return true;

        return false;
    }

    /// <summary>
    /// Check if a line looks like a real sentence or prose continuation
    /// (multi-word, not single-case labels). Allows all-lowercase continuation
    /// lines that are common in word-wrapped text.
    /// </summary>
    private static bool HasSentenceStructure(string line)
    {
        // Must contain spaces (multiple words)
        var wordCount = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 3) return false;

        // All-caps lines are headings/labels, not prose
        if (line == line.ToUpperInvariant()) return false;

        // Must have at least some lowercase (prose has lowercase words)
        return line.Any(char.IsLower);
    }
}
