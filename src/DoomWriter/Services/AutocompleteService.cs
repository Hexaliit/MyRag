using System.Text;
using DoomSummarizer.Services;
using DoomWriter.Models;

namespace DoomWriter.Services;

/// <summary>
/// Context-aware autocomplete using signals + corpus + sentinel LLM.
/// Provides three types of completion:
/// 1. Entity/link completion — from document entities + corpus entities
/// 2. Sentence completion — after 5+ words, sentinel model suggests the rest
/// 3. Link URL completion — after [text]( suggests from corpus URLs
/// </summary>
public class AutocompleteService
{
    private readonly OllamaService _ollama;
    private readonly CorpusService _corpus;
    private readonly EmbeddingService _embedding;
    private readonly WriterSettingsService _settings;

    private CancellationTokenSource? _pendingCts;

    public event EventHandler<AutocompleteResult>? SuggestionReady;

    public AutocompleteService(
        OllamaService ollama,
        CorpusService corpus,
        EmbeddingService embedding,
        WriterSettingsService settings)
    {
        _ollama = ollama;
        _corpus = corpus;
        _embedding = embedding;
        _settings = settings;
    }

    /// <summary>
    /// Request autocomplete for the current typing context.
    /// Called on every keystroke (internally debounced).
    /// </summary>
    public async Task RequestCompletionAsync(
        string textBeforeCursor,
        DocumentSignals signals,
        CancellationToken ct = default)
    {
        // Cancel any pending completion
        _pendingCts?.Cancel();
        _pendingCts = new CancellationTokenSource();
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _pendingCts.Token).Token;

        try
        {
            // Debounce — wait 300ms after last keystroke
            await Task.Delay(300, linkedCt);

            // Determine completion type
            var result = await DetermineAndComplete(textBeforeCursor, signals, linkedCt);
            if (result != null)
                SuggestionReady?.Invoke(this, result);
        }
        catch (OperationCanceledException) { }
    }

    private async Task<AutocompleteResult?> DetermineAndComplete(
        string textBeforeCursor,
        DocumentSignals signals,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(textBeforeCursor))
            return null;

        var lastLine = GetLastLine(textBeforeCursor);

        // 1. Link URL completion: [text]( → suggest from corpus
        if (lastLine.Contains("](") && !lastLine.EndsWith(')'))
        {
            return await CompleteLinkUrlAsync(lastLine, ct);
        }

        // 2. Entity mention completion: typing a capitalized word
        var lastWord = GetLastWord(lastLine);
        if (lastWord.Length >= 2 && char.IsUpper(lastWord[0]))
        {
            var entityMatch = CompleteEntityName(lastWord, signals);
            if (entityMatch != null)
                return entityMatch;
        }

        // 3. Sentence completion: after 5+ words on current line, use sentinel LLM
        var wordCount = lastLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount >= 5)
        {
            return await CompleteSentenceAsync(textBeforeCursor, signals, ct);
        }

        return null;
    }

    /// <summary>
    /// Complete a link URL from corpus articles.
    /// When user types [some text]( → suggest matching corpus URLs.
    /// </summary>
    private async Task<AutocompleteResult?> CompleteLinkUrlAsync(string line, CancellationToken ct)
    {
        // Extract the link text between [ and ](
        var bracketStart = line.LastIndexOf('[');
        var bracketEnd = line.LastIndexOf("](", StringComparison.Ordinal);
        if (bracketStart < 0 || bracketEnd < 0) return null;

        var linkText = line[(bracketStart + 1)..bracketEnd];
        if (string.IsNullOrWhiteSpace(linkText)) return null;

        // Search corpus for matching content
        var matches = await _corpus.SearchAsync(linkText, topK: 5);
        if (matches.Count == 0) return null;

        var suggestions = matches.Select(m => new AutocompleteSuggestion
        {
            Text = m.Source, // The file path / URL
            DisplayText = $"{m.Title} ({m.Score:F2})",
            Kind = AutocompleteKind.Link
        }).ToList();

        return new AutocompleteResult
        {
            Kind = AutocompleteKind.Link,
            Suggestions = suggestions,
            ReplaceFrom = line.Length // Insert after the (
        };
    }

    /// <summary>
    /// Complete an entity name from document + corpus entities.
    /// </summary>
    private AutocompleteResult? CompleteEntityName(string partialName, DocumentSignals signals)
    {
        var matches = signals.Entities
            .Where(e => e.Name.StartsWith(partialName, StringComparison.OrdinalIgnoreCase)
                        && e.Name.Length > partialName.Length)
            .OrderByDescending(e => e.MentionCount)
            .Take(5)
            .Select(e => new AutocompleteSuggestion
            {
                Text = e.Name[partialName.Length..], // Only the remaining characters
                DisplayText = $"{e.Name} ({e.Type}, {e.MentionCount} mentions)",
                Kind = AutocompleteKind.Entity
            })
            .ToList();

        if (matches.Count == 0) return null;

        return new AutocompleteResult
        {
            Kind = AutocompleteKind.Entity,
            Suggestions = matches,
            ReplaceFrom = 0 // Append to current word
        };
    }

    /// <summary>
    /// Complete a sentence using the sentinel (fast/small) LLM.
    /// Context-aware: uses document signals to ground the completion.
    /// </summary>
    private async Task<AutocompleteResult?> CompleteSentenceAsync(
        string textBeforeCursor,
        DocumentSignals signals,
        CancellationToken ct)
    {
        // Build minimal context for fast completion
        var lastParagraph = GetLastParagraph(textBeforeCursor);
        var currentSection = signals.DominantTopic;
        var entityHints = string.Join(", ", signals.Entities
            .OrderByDescending(e => e.MentionCount)
            .Take(5)
            .Select(e => e.Name));

        var prompt = $"""
            Continue this text naturally. Write only the completion of the current sentence (10-30 words max).
            Topic: {currentSection}
            Key terms: {entityHints}

            Text to continue:
            {lastParagraph}
            """;

        try
        {
            var completion = await _ollama.SentinelGenerateAsync(
                prompt,
                systemPrompt: "Complete the sentence naturally. Output ONLY the completion words, nothing else. No quotes, no explanation.",
                temperature: 0.3,
                ct: ct);

            completion = completion.Trim().TrimStart('.', ',', ' ');

            if (string.IsNullOrEmpty(completion) || completion.Length < 3)
                return null;

            // Limit to one sentence
            var sentenceEnd = completion.IndexOfAny(['.', '!', '?']);
            if (sentenceEnd > 0)
                completion = completion[..(sentenceEnd + 1)];

            return new AutocompleteResult
            {
                Kind = AutocompleteKind.Sentence,
                Suggestions =
                [
                    new AutocompleteSuggestion
                    {
                        Text = completion,
                        DisplayText = completion,
                        Kind = AutocompleteKind.Sentence
                    }
                ],
                ReplaceFrom = 0
            };
        }
        catch
        {
            return null;
        }
    }

    // --- Helpers ---

    private static string GetLastLine(string text)
    {
        var lastNewline = text.LastIndexOf('\n');
        return lastNewline >= 0 ? text[(lastNewline + 1)..] : text;
    }

    private static string GetLastWord(string line)
    {
        var lastSpace = line.LastIndexOf(' ');
        return lastSpace >= 0 ? line[(lastSpace + 1)..] : line;
    }

    private static string GetLastParagraph(string text)
    {
        var lastBlank = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        var paragraph = lastBlank >= 0 ? text[(lastBlank + 2)..] : text;
        // Limit to last 500 chars for speed
        return paragraph.Length > 500 ? paragraph[^500..] : paragraph;
    }
}

// --- Result types ---

public class AutocompleteResult
{
    public required AutocompleteKind Kind { get; init; }
    public required List<AutocompleteSuggestion> Suggestions { get; init; }
    public int ReplaceFrom { get; init; }
}

public class AutocompleteSuggestion
{
    public required string Text { get; init; }
    public required string DisplayText { get; init; }
    public required AutocompleteKind Kind { get; init; }
}

public enum AutocompleteKind
{
    Sentence,
    Entity,
    Link
}
