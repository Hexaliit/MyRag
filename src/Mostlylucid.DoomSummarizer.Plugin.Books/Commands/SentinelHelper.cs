using DoomSummarizer.Services;
using Mostlylucid.DoomSummarizer.Plugin.Books.Detection;

namespace Mostlylucid.DoomSummarizer.Plugin.Books.Commands;

/// <summary>
/// Shared helper for sentinel-backed document type detection in CLI commands.
/// Loads config, creates OllamaService, and delegates to DocumentTypeDetector.
/// </summary>
internal static class SentinelHelper
{
    /// <summary>
    /// Run document type detection with optional sentinel fallback.
    /// The DocumentTypeDetector owns the threshold decision — callers
    /// should always call this regardless of heuristic confidence.
    /// Returns null only if config/Ollama is completely unavailable.
    /// </summary>
    internal static async Task<DocumentTypeResult> ClassifyAsync(
        string content,
        BookTypeResult heuristic,
        int wordCount,
        string fileName,
        bool noLlm,
        CancellationToken ct)
    {
        OllamaService? ollama = null;

        if (!noLlm)
        {
            try
            {
                var config = await ConfigService.LoadAsync();
                ollama = new OllamaService(config.Ollama);
            }
            catch
            {
                // Config missing or invalid — proceed without sentinel
            }
        }

        var detector = new DocumentTypeDetector(ollama);
        var signals = heuristic.Signals
            .Select(s => $"{s.Name}→{s.VotedType}")
            .ToList();

        return await detector.ClassifyAsync(
            content,
            heuristic.Type,
            heuristic.Confidence,
            signals,
            fileName,
            wordCount,
            ct);
    }
}
