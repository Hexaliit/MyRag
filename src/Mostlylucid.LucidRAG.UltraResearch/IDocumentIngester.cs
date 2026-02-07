namespace LucidRAG.UltraResearch;

/// <summary>
///     Abstraction for document ingestion. Implemented by CLI (CliDocumentProcessor)
///     and web (DocumentProcessingQueue) consumers to decouple the orchestrator from
///     environment-specific ingestion infrastructure.
/// </summary>
public interface IDocumentIngester
{
    /// <summary>
    ///     Index a file into the RAG pipeline (chunk, embed, entity extract, graph, link extract).
    /// </summary>
    /// <returns>True if ingestion succeeded, false otherwise.</returns>
    Task<DocumentIngestResult> IngestAsync(string filePath, Guid collectionId, CancellationToken ct = default);
}

/// <summary>
///     Result of a document ingestion operation.
/// </summary>
public record DocumentIngestResult(
    bool Success,
    string Message,
    Guid? DocumentId = null,
    int SegmentCount = 0);
