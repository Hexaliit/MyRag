namespace DoomSummarizer.Plugins;

/// <summary>
///     A reader converts a file or stream into structured Markdown.
///     Reader packages are thin NuGet libraries with minimal dependencies.
/// </summary>
public interface IDocumentReader
{
    /// <summary>Unique reader name (e.g. "pdf", "docx", "gutenberg").</summary>
    string ReaderName { get; }

    /// <summary>File extensions this reader handles (e.g. ".pdf", ".docx").</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Higher priority wins when multiple readers match the same extension.</summary>
    int Priority { get; }

    /// <summary>Read a file from disk and return structured Markdown.</summary>
    Task<ReaderResult> ReadAsync(string filePath, ReaderOptions? options = null, CancellationToken ct = default);

    /// <summary>Read from a stream and return structured Markdown.</summary>
    Task<ReaderResult> ReadAsync(Stream stream, string fileName, ReaderOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>
///     Options controlling reader behavior.
/// </summary>
public record ReaderOptions
{
    /// <summary>Max pages to extract (PDFs).</summary>
    public int? MaxPages { get; init; }

    /// <summary>Preserve page markers in output (e.g. &lt;!-- PAGE:N --&gt;).</summary>
    public bool IncludePageMarkers { get; init; } = true;

    /// <summary>Extract tables as markdown.</summary>
    public bool ExtractTables { get; init; } = true;
}

/// <summary>
///     Result of reading a document.
/// </summary>
public record ReaderResult
{
    /// <summary>Extracted content as Markdown.</summary>
    public required string Markdown { get; init; }

    /// <summary>Document title (from metadata or first heading).</summary>
    public required string Title { get; init; }

    /// <summary>Arbitrary metadata extracted from the document.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Non-fatal issues encountered during reading.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Estimated word count of the extracted content.</summary>
    public int WordCount { get; init; }
}