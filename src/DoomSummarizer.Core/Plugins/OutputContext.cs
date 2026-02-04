using DoomSummarizer.Models;

namespace DoomSummarizer.Plugins;

/// <summary>
///     Parameter bag for output plugins.
/// </summary>
public record OutputContext
{
    /// <summary>The generated content (Markdown, HTML, plain text).</summary>
    public required string Content { get; init; }

    /// <summary>Content format (e.g. "markdown", "html", "text", "json").</summary>
    public string Format { get; init; } = "markdown";

    /// <summary>Subject line (for email) or title (for file output).</summary>
    public string? Subject { get; init; }

    /// <summary>Recipient address(es) for email-based outputs.</summary>
    public IReadOnlyList<string> Recipients { get; init; } = [];

    /// <summary>Output file path (for file-based outputs).</summary>
    public string? OutputPath { get; init; }

    /// <summary>The source items used to generate the content.</summary>
    public IReadOnlyList<ContentItem> SourceItems { get; init; } = [];

    /// <summary>Full configuration.</summary>
    public DoomConfig? Config { get; init; }
}