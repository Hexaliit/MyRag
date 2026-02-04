using System.Text;
using System.Text.Json;
using DoomWriter.Models;
using Mostlylucid.DocSummarizer.Services;
using OllamaService = DoomSummarizer.Services.OllamaService;

namespace DoomWriter.Services;

/// <summary>
///     AI writing assistant using Ollama for intelligent text generation.
///     Uses DoomSummarizer.Core's long-form generation techniques:
///     running summary, entity continuity, negative prompts, drift detection.
/// </summary>
public class WritingAssistantService
{
    private readonly CorpusService _corpus;
    private readonly IEmbeddingService _embedding;
    private readonly OllamaService _ollama;
    private readonly WriterSettingsService _settings;

    public WritingAssistantService(
        OllamaService ollama,
        IEmbeddingService embedding,
        CorpusService corpus,
        WriterSettingsService settings)
    {
        _ollama = ollama;
        _embedding = embedding;
        _corpus = corpus;
        _settings = settings;
    }

    public event EventHandler<string>? GenerationStarted;
    public event EventHandler<string>? GenerationCompleted;
    public event EventHandler<string>? GenerationFailed;

    /// <summary>
    ///     Generate the next paragraph based on document context.
    ///     Uses running summary + negative prompts + entity continuity + corpus evidence.
    /// </summary>
    public async Task<string> GenerateNextParagraphAsync(
        DocumentSignals signals,
        string fullContent,
        int cursorPosition,
        CancellationToken ct = default)
    {
        GenerationStarted?.Invoke(this, "Generating next paragraph...");

        try
        {
            var context = BuildContext(signals, fullContent, cursorPosition);
            var evidence = await GatherEvidence(signals, ct);
            var prompt = BuildNextParagraphPrompt(context, evidence);

            var result = await _ollama.GenerateAsync(
                prompt,
                "You are a skilled writer continuing a markdown document. Write exactly one paragraph that flows naturally from the preceding text. Match the document's tone and style. Do not repeat information already covered. Output ONLY the paragraph text, no meta-commentary.",
                0.7,
                ct);

            var cleaned = CleanGeneratedText(result);
            GenerationCompleted?.Invoke(this, cleaned);
            return cleaned;
        }
        catch (Exception ex)
        {
            GenerationFailed?.Invoke(this, ex.Message);
            return "";
        }
    }

    /// <summary>
    ///     Expand selected text with more detail.
    /// </summary>
    public async Task<string> ExpandTextAsync(
        string selectedText,
        DocumentSignals signals,
        string fullContent,
        CancellationToken ct = default)
    {
        GenerationStarted?.Invoke(this, "Expanding text...");

        try
        {
            var context = BuildContext(signals, fullContent, 0);
            var prompt = $"""
                          DOCUMENT CONTEXT:
                          {context.RunningSummary}

                          SELECTED TEXT TO EXPAND:
                          {selectedText}

                          Write an expanded version of the selected text. Add detail, examples, or explanation.
                          Keep the same tone. Output ONLY the expanded text.
                          """;

            var result = await _ollama.GenerateAsync(
                prompt,
                "You are expanding a section of a markdown document. Add depth and detail while maintaining the original meaning and style.",
                0.6,
                ct);

            var cleaned = CleanGeneratedText(result);
            GenerationCompleted?.Invoke(this, cleaned);
            return cleaned;
        }
        catch (Exception ex)
        {
            GenerationFailed?.Invoke(this, ex.Message);
            return selectedText;
        }
    }

    /// <summary>
    ///     Rewrite selected text with alternative phrasing.
    /// </summary>
    public async Task<string> RewriteTextAsync(
        string selectedText,
        DocumentSignals signals,
        string fullContent,
        CancellationToken ct = default)
    {
        GenerationStarted?.Invoke(this, "Rewriting text...");

        try
        {
            var prompt = $"""
                          ORIGINAL TEXT:
                          {selectedText}

                          Rewrite this text with alternative phrasing. Keep the same meaning but improve clarity and flow.
                          Output ONLY the rewritten text.
                          """;

            var result = await _ollama.GenerateAsync(
                prompt,
                "You are rewriting a passage from a markdown document. Maintain meaning while improving style and clarity.",
                0.7,
                ct);

            var cleaned = CleanGeneratedText(result);
            GenerationCompleted?.Invoke(this, cleaned);
            return cleaned;
        }
        catch (Exception ex)
        {
            GenerationFailed?.Invoke(this, ex.Message);
            return selectedText;
        }
    }

    /// <summary>
    ///     Simplify selected text — reduce complexity while preserving meaning.
    /// </summary>
    public async Task<string> SimplifyTextAsync(
        string selectedText,
        CancellationToken ct = default)
    {
        GenerationStarted?.Invoke(this, "Simplifying text...");

        try
        {
            var prompt = $"""
                          ORIGINAL TEXT:
                          {selectedText}

                          Simplify this text. Use shorter sentences and plainer language.
                          Keep the core meaning. Output ONLY the simplified text.
                          """;

            var result = await _ollama.GenerateAsync(
                prompt,
                "You are simplifying a passage. Make it easier to read without losing meaning.",
                0.4,
                ct);

            var cleaned = CleanGeneratedText(result);
            GenerationCompleted?.Invoke(this, cleaned);
            return cleaned;
        }
        catch (Exception ex)
        {
            GenerationFailed?.Invoke(this, ex.Message);
            return selectedText;
        }
    }

    /// <summary>
    ///     Grammar check using sentinel (small/fast) model.
    ///     Returns corrected text or empty string if no issues found.
    /// </summary>
    public async Task<GrammarCheckResult> CheckGrammarAsync(
        string text,
        CancellationToken ct = default)
    {
        try
        {
            var prompt = $$"""
                           Check the following text for grammar, spelling, and style issues.
                           If there are issues, respond with a JSON object:
                           {"has_issues": true, "corrected": "the corrected text", "issues": ["issue 1", "issue 2"]}
                           If there are no issues, respond with:
                           {"has_issues": false}

                           TEXT:
                           {{text}}
                           """;

            var result = await _ollama.SentinelGenerateAsync(
                prompt,
                "You are a precise grammar checker. Respond only with the requested JSON format.",
                0.1,
                ct);

            return ParseGrammarResult(result, text);
        }
        catch
        {
            return new GrammarCheckResult { HasIssues = false, OriginalText = text };
        }
    }

    // --- Context building ---

    private GenerationContext BuildContext(DocumentSignals signals, string fullContent, int cursorPosition)
    {
        var context = new GenerationContext();
        var lines = fullContent.Split('\n');

        // Find current section heading
        HeadingItem? currentHeading = null;
        for (var i = signals.Headings.Count - 1; i >= 0; i--)
            if (signals.Headings[i].CharOffset <= cursorPosition)
            {
                currentHeading = signals.Headings[i];
                break;
            }

        context.CurrentSection = currentHeading?.Text ?? "";

        // Previous 2 paragraphs (immediate context)
        var textBeforeCursor = cursorPosition < fullContent.Length
            ? fullContent[..cursorPosition]
            : fullContent;
        var paragraphs = textBeforeCursor.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        context.PreviousParagraphs = paragraphs
            .TakeLast(2)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        // Running summary (heading-based)
        var summaryBuilder = new StringBuilder();
        summaryBuilder.AppendLine("DOCUMENT SO FAR:");
        foreach (var heading in signals.Headings)
        {
            if (heading.CharOffset >= cursorPosition) break;
            summaryBuilder.AppendLine($"- [{heading.Text}]");
        }

        context.RunningSummary = summaryBuilder.ToString();

        // Negative prompts (what's already covered — entity names that have been mentioned enough)
        context.CoveredConcepts = signals.Entities
            .Where(e => e.MentionCount >= 3)
            .Select(e => e.Name)
            .ToList();

        // Active entities (recently mentioned, might need re-introduction)
        context.ActiveEntities = signals.Entities
            .Where(e => e.MentionCount >= 1)
            .OrderByDescending(e => e.MentionCount)
            .Take(10)
            .Select(e => e.Name)
            .ToList();

        return context;
    }

    private async Task<string> GatherEvidence(DocumentSignals signals, CancellationToken ct)
    {
        if (!_corpus.IsInitialized) return "";

        try
        {
            // Search corpus for content relevant to current document topic
            var query = signals.DominantTopic;
            if (string.IsNullOrEmpty(query) && signals.Segments.Count > 0)
                query = signals.Segments[0].FirstLine;

            if (string.IsNullOrEmpty(query)) return "";

            var matches = await _corpus.SearchAsync(query, 5);
            if (matches.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("RELEVANT EVIDENCE FROM YOUR CORPUS:");
            foreach (var match in matches.Take(3)) sb.AppendLine($"[From \"{match.Title}\"]: {match.Text}");
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string BuildNextParagraphPrompt(GenerationContext context, string evidence)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(context.RunningSummary))
        {
            sb.AppendLine(context.RunningSummary);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(context.CurrentSection))
        {
            sb.AppendLine($"CURRENT SECTION: {context.CurrentSection}");
            sb.AppendLine();
        }

        if (context.PreviousParagraphs.Count > 0)
        {
            sb.AppendLine("PREVIOUS PARAGRAPHS:");
            foreach (var p in context.PreviousParagraphs)
                sb.AppendLine(p);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(evidence))
        {
            sb.AppendLine(evidence);
            sb.AppendLine();
        }

        if (context.CoveredConcepts.Count > 0)
        {
            sb.AppendLine($"ALREADY COVERED (do not repeat): {string.Join(", ", context.CoveredConcepts)}");
            sb.AppendLine();
        }

        if (context.ActiveEntities.Count > 0)
        {
            sb.AppendLine($"ACTIVE ENTITIES (available to reference): {string.Join(", ", context.ActiveEntities)}");
            sb.AppendLine();
        }

        sb.AppendLine("Write the next paragraph (approximately 100-150 words).");

        return sb.ToString();
    }

    private static string CleanGeneratedText(string text)
    {
        // Remove common LLM artifacts
        var cleaned = text.Trim();

        // Remove wrapping quotes
        if (cleaned.StartsWith('"') && cleaned.EndsWith('"'))
            cleaned = cleaned[1..^1].Trim();

        // Remove "Here's the next paragraph:" preamble
        var prefixes = new[] { "Here's ", "Here is ", "Next paragraph:", "The next paragraph:" };
        foreach (var prefix in prefixes)
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..].TrimStart('\n', '\r', ' ');
                break;
            }

        return cleaned;
    }

    private static GrammarCheckResult ParseGrammarResult(string json, string originalText)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var hasIssues = root.GetProperty("has_issues").GetBoolean();
            if (!hasIssues)
                return new GrammarCheckResult { HasIssues = false, OriginalText = originalText };

            var corrected = root.TryGetProperty("corrected", out var corrProp)
                ? corrProp.GetString() ?? originalText
                : originalText;

            var issues = new List<string>();
            if (root.TryGetProperty("issues", out var issuesProp))
                foreach (var issue in issuesProp.EnumerateArray())
                    issues.Add(issue.GetString() ?? "");

            return new GrammarCheckResult
            {
                HasIssues = true,
                OriginalText = originalText,
                CorrectedText = corrected,
                Issues = issues
            };
        }
        catch
        {
            return new GrammarCheckResult { HasIssues = false, OriginalText = originalText };
        }
    }
}

// --- Supporting types ---

internal class GenerationContext
{
    public string CurrentSection { get; set; } = "";
    public List<string> PreviousParagraphs { get; set; } = [];
    public string RunningSummary { get; set; } = "";
    public List<string> CoveredConcepts { get; set; } = [];
    public List<string> ActiveEntities { get; set; } = [];
}

public class GrammarCheckResult
{
    public bool HasIssues { get; init; }
    public string OriginalText { get; init; } = "";
    public string? CorrectedText { get; init; }
    public List<string> Issues { get; init; } = [];
}