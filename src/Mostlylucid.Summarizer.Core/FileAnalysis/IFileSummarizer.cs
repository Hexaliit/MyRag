namespace Mostlylucid.Summarizer.Core.FileAnalysis;

/// <summary>
/// Service for extracting universal file metadata.
/// This should be the starting point for all pipeline processing.
/// </summary>
public interface IFileSummarizer
{
    /// <summary>
    /// Extract metadata from a file path.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File metadata with all available information.</returns>
    Task<FileMetadata> ExtractMetadataAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Extract metadata from a stream.
    /// Note: Some metadata (timestamps, attributes) won't be available.
    /// </summary>
    /// <param name="stream">File stream.</param>
    /// <param name="fileName">Original file name (for extension detection).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File metadata with available information.</returns>
    Task<FileMetadata> ExtractMetadataAsync(Stream stream, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Get the content hash for a file.
    /// </summary>
    string GetContentHash(string filePath);

    /// <summary>
    /// Get the content hash for a stream.
    /// </summary>
    string GetContentHash(Stream stream);

    /// <summary>
    /// Detect the MIME type for a file.
    /// </summary>
    string DetectMimeType(string filePath);

    /// <summary>
    /// Get extended metadata for specific file types (PE files, archives, etc.).
    /// Returns additional signals beyond the base FileMetadata.
    /// </summary>
    Task<Dictionary<string, object?>> GetExtendedMetadataAsync(string filePath, CancellationToken ct = default);
}
