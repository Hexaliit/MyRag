using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Services;
using Mostlylucid.Summarizer.Core.FileAnalysis;
using Mostlylucid.Summarizer.Core.Pipeline;

namespace Mostlylucid.DocSummarizer.Pipeline;

/// <summary>
///     Pipeline implementation for document files (PDF, DOCX, MD, TXT, HTML).
///     Extracts content and chunks it for embedding.
///     Entity extraction is handled by the application layer (LucidRAG) which has access to GraphRag.
/// </summary>
public class DocumentPipeline : PipelineBase
{
    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".md", ".txt", ".html", ".htm", ".rtf"
    };

    private readonly DocumentChunker _chunker;
    private readonly IFileSummarizer _fileSummarizer;
    private readonly IDocumentHandlerRegistry _handlerRegistry;
    private readonly ILogger<DocumentPipeline> _logger;

    public DocumentPipeline(
        IDocumentHandlerRegistry handlerRegistry,
        IFileSummarizer fileSummarizer,
        ILogger<DocumentPipeline> logger)
    {
        _handlerRegistry = handlerRegistry;
        _fileSummarizer = fileSummarizer;
        _chunker = new DocumentChunker();
        _logger = logger;
    }

    /// <inheritdoc />
    public override string PipelineId => "doc";

    /// <inheritdoc />
    public override string Name => "Document Pipeline";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions
    {
        get
        {
            var extensions = _handlerRegistry.GetSupportedExtensions();
            return extensions.Count > 0
                ? extensions.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : DefaultExtensions;
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ContentChunk>> ProcessCoreAsync(
        string filePath,
        PipelineOptions options,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing document: {FilePath}", filePath);

        // Extract universal file metadata first
        var fileMetadata = await _fileSummarizer.ExtractMetadataAsync(filePath, ct);
        var fileMetadataDict = fileMetadata.ToSearchMetadata();

        var handler = _handlerRegistry.GetHandlerForFile(filePath);
        if (handler == null) throw new NotSupportedException($"No handler found for file: {filePath}");

        progress?.Report(new PipelineProgress("Extracting", $"Using {handler.HandlerName}", 10));

        // Extract content from document
        var content = await handler.ProcessAsync(filePath, new DocumentHandlerOptions
        {
            Verbose = false,
            CancellationToken = ct
        });

        progress?.Report(new PipelineProgress("Chunking", "Breaking into segments", 50));

        // Chunk the markdown content
        var docChunks = _chunker.ChunkByStructure(content.Markdown);

        progress?.Report(new PipelineProgress("Converting", "Creating content chunks", 80, docChunks.Count,
            docChunks.Count));

        // Convert DocumentChunk to ContentChunk
        var chunks = docChunks.Select(chunk =>
        {
            // Start with file metadata
            var metadata = new Dictionary<string, object?>(fileMetadataDict)
            {
                // Add document-specific metadata
                ["heading"] = chunk.Heading,
                ["headingLevel"] = chunk.HeadingLevel,
                ["pageStart"] = chunk.PageStart,
                ["pageEnd"] = chunk.PageEnd,
                ["title"] = content.Title,
                ["contentType"] = content.ContentType
            };

            return new ContentChunk
            {
                Id = GenerateChunkId(filePath, chunk.Order),
                Text = chunk.Content,
                ContentType = ContentType.DocumentText,
                SourcePath = filePath,
                Index = chunk.Order,
                ContentHash = chunk.Hash,
                Metadata = metadata
            };
        }).ToList();

        progress?.Report(new PipelineProgress("Complete", "Processing complete", 100, chunks.Count, chunks.Count));

        _logger.LogInformation("Processed {ChunkCount} chunks from document (file: {Size})",
            chunks.Count, fileMetadata.SizeFormatted);

        return chunks;
    }
}