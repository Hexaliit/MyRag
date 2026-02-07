namespace DoomSummarizer.Models;

/// <summary>
///     Resolved metadata for a citation (from CrossRef API for DOIs, arXiv API for arXiv IDs).
/// </summary>
public record CitationMetadata(
    string Title,
    List<string> Authors,
    int? Year,
    string? Venue,
    string? Abstract,
    string? Doi,
    string? ArxivId,
    string? PdfUrl);
