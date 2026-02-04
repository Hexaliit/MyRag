using System.Text;

namespace Mostlylucid.DocSummarizer.Images.Models;

/// <summary>
///     Result from a single OCR system in the benchmark comparison.
/// </summary>
public record OcrSystemResult
{
    /// <summary>
    ///     Name of the OCR system (e.g., "Tesseract", "Nanonets", "OlmOCR-2").
    /// </summary>
    public required string SystemName { get; init; }

    /// <summary>
    ///     Extracted text from this OCR system. Null if no text was extracted.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    ///     Number of characters in the extracted text.
    /// </summary>
    public int CharCount { get; init; }

    /// <summary>
    ///     Number of words in the extracted text.
    /// </summary>
    public int WordCount { get; init; }

    /// <summary>
    ///     Accuracy score from spell-check (0.0 - 1.0).
    ///     Represents the ratio of correctly spelled words.
    /// </summary>
    public double Accuracy { get; init; }

    /// <summary>
    ///     Time taken to process in milliseconds.
    /// </summary>
    public long TimingMs { get; init; }

    /// <summary>
    ///     Whether this system was the winner (highest accuracy).
    /// </summary>
    public bool IsWinner { get; init; }

    /// <summary>
    ///     The signal key where this text was sourced from.
    /// </summary>
    public string? SourceSignal { get; init; }

    /// <summary>
    ///     Whether this system actually ran (vs being skipped by escalation).
    /// </summary>
    public bool DidRun { get; init; }

    /// <summary>
    ///     Whether the text appears garbled (< 50% correct words).
    /// </summary>
    public bool IsGarbled { get; init; }
}

/// <summary>
///     Complete benchmark result for a single image comparing all OCR systems.
/// </summary>
public record OcrBenchmarkResult
{
    /// <summary>
    ///     Path to the image that was benchmarked.
    /// </summary>
    public required string ImagePath { get; init; }

    /// <summary>
    ///     Filename of the image.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    ///     Timestamp when the benchmark was run.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    ///     Results from each OCR system that participated.
    /// </summary>
    public IReadOnlyList<OcrSystemResult> SystemResults { get; init; } = Array.Empty<OcrSystemResult>();

    /// <summary>
    ///     Name of the winning system (highest accuracy).
    /// </summary>
    public string? WinnerName { get; init; }

    /// <summary>
    ///     Accuracy of the winning system.
    /// </summary>
    public double WinnerAccuracy { get; init; }

    /// <summary>
    ///     Consensus text agreed upon by multiple systems (if any).
    /// </summary>
    public string? ConsensusText { get; init; }

    /// <summary>
    ///     Total time for all OCR systems combined.
    /// </summary>
    public long TotalTimingMs { get; init; }

    /// <summary>
    ///     Number of systems that actually ran.
    /// </summary>
    public int SystemsRan => SystemResults.Count(r => r.DidRun);

    /// <summary>
    ///     Generate a Markdown summary table for this benchmark result.
    /// </summary>
    public string ToMarkdownTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Image: {FileName}");
        sb.AppendLine($"**Date:** {Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("| System | Time (ms) | Chars | Words | Accuracy | Winner |");
        sb.AppendLine("|--------|-----------|-------|-------|----------|--------|");

        foreach (var result in SystemResults.OrderByDescending(r => r.Accuracy))
        {
            var winner = result.IsWinner ? "\u2605" : "";
            var accuracy = result.DidRun ? $"{result.Accuracy:P0}" : "N/A";
            var timing = result.DidRun ? result.TimingMs.ToString() : "-";
            var chars = result.DidRun ? result.CharCount.ToString() : "-";
            var words = result.DidRun ? result.WordCount.ToString() : "-";

            sb.AppendLine($"| {result.SystemName} | {timing} | {chars} | {words} | {accuracy} | {winner} |");
        }

        sb.AppendLine();
        if (!string.IsNullOrEmpty(WinnerName))
            sb.AppendLine($"**Winner:** {WinnerName} ({WinnerAccuracy:P0} accuracy)");
        else
            sb.AppendLine("**Winner:** No winner determined");

        return sb.ToString();
    }

    /// <summary>
    ///     Generate Markdown with full text details in collapsible sections.
    /// </summary>
    public string ToMarkdownWithDetails()
    {
        var sb = new StringBuilder();
        sb.Append(ToMarkdownTable());
        sb.AppendLine();

        foreach (var result in SystemResults.Where(r => r.DidRun && !string.IsNullOrWhiteSpace(r.Text))
                     .OrderByDescending(r => r.Accuracy))
        {
            var winner = result.IsWinner ? " \u2605 Winner" : "";
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{result.SystemName} Output ({result.Accuracy:P0}){winner}</summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(result.Text?.Trim() ?? "(empty)");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();

        return sb.ToString();
    }
}