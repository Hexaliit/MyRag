using System.Security.Cryptography;

namespace Mostlylucid.Summarizer.Core.FileAnalysis;

/// <summary>
/// Universal file metadata extracted from any file type.
/// This is the starting point for all pipeline processing.
/// </summary>
public sealed class FileMetadata
{
    // ===== IDENTITY =====

    /// <summary>
    /// Full absolute path to the file.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// File name with extension (e.g., "document.pdf").
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// File name without extension (e.g., "document").
    /// </summary>
    public required string FileNameWithoutExtension { get; init; }

    /// <summary>
    /// File extension including the dot, lowercase (e.g., ".pdf").
    /// Empty string if no extension.
    /// </summary>
    public required string Extension { get; init; }

    /// <summary>
    /// Parent directory path.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// Parent directory name only (e.g., "Documents" from "/home/user/Documents").
    /// </summary>
    public required string DirectoryName { get; init; }

    /// <summary>
    /// Depth in directory tree from root (e.g., "/foo/bar/file.txt" = 3).
    /// </summary>
    public required int DirectoryDepth { get; init; }

    // ===== SIZE =====

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Human-readable file size (e.g., "1.5 MB").
    /// </summary>
    public string SizeFormatted => FormatBytes(SizeBytes);

    // ===== HASHES =====

    /// <summary>
    /// XxHash64 content hash (16-character lowercase hex).
    /// Fast hash for deduplication. Empty if file cannot be read.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// SHA-256 content hash (64-character lowercase hex).
    /// Cryptographic hash for integrity verification. Empty if not computed.
    /// </summary>
    public string Sha256Hash { get; init; } = string.Empty;

    // ===== CONTENT ANALYSIS =====

    /// <summary>
    /// Detected MIME type (e.g., "application/pdf").
    /// "application/octet-stream" if unknown.
    /// </summary>
    public required string MimeType { get; init; }

    /// <summary>
    /// Primary MIME category (e.g., "application", "image", "text").
    /// </summary>
    public string MimeCategory => MimeType.Split('/')[0];

    /// <summary>
    /// First 16 bytes of file as hex string (magic bytes).
    /// Useful for file type detection.
    /// </summary>
    public string MagicBytesHex { get; init; } = string.Empty;

    /// <summary>
    /// Shannon entropy of file content (0-8 scale).
    /// High entropy (>7.5) suggests encrypted/compressed data.
    /// Low entropy (&lt;4) suggests repetitive/text data.
    /// </summary>
    public double Entropy { get; init; }

    /// <summary>
    /// Entropy classification: "random/encrypted", "compressed", "binary", "text", "empty".
    /// </summary>
    public string EntropyClassification => ClassifyEntropy(Entropy, SizeBytes);

    /// <summary>
    /// Whether the file appears to be binary (contains null bytes or high entropy).
    /// </summary>
    public required bool IsBinary { get; init; }

    /// <summary>
    /// Whether the file appears to be text (valid UTF-8/ASCII, low entropy).
    /// </summary>
    public bool IsText => !IsBinary && Exists;

    /// <summary>
    /// Percentage of printable ASCII characters in first 8KB (0-100).
    /// High percentage suggests text file.
    /// </summary>
    public double PrintableAsciiPercent { get; init; }

    /// <summary>
    /// Whether the file contains null bytes (strong binary indicator).
    /// </summary>
    public bool ContainsNullBytes { get; init; }

    // ===== TIMESTAMPS =====

    /// <summary>
    /// File creation time (UTC).
    /// </summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// File last modified time (UTC).
    /// </summary>
    public required DateTimeOffset ModifiedAtUtc { get; init; }

    /// <summary>
    /// File last accessed time (UTC).
    /// </summary>
    public required DateTimeOffset AccessedAtUtc { get; init; }

    /// <summary>
    /// Age of file since last modification.
    /// </summary>
    public TimeSpan Age => DateTimeOffset.UtcNow - ModifiedAtUtc;

    // ===== ATTRIBUTES =====

    /// <summary>
    /// Whether the file exists at the time of metadata extraction.
    /// </summary>
    public required bool Exists { get; init; }

    /// <summary>
    /// Whether the file is read-only.
    /// </summary>
    public required bool IsReadOnly { get; init; }

    /// <summary>
    /// Whether the file is hidden.
    /// </summary>
    public required bool IsHidden { get; init; }

    /// <summary>
    /// Whether the file is a system file.
    /// </summary>
    public required bool IsSystem { get; init; }

    /// <summary>
    /// Whether the file is a symbolic link.
    /// </summary>
    public required bool IsSymbolicLink { get; init; }

    /// <summary>
    /// Target path if this is a symbolic link. Null otherwise.
    /// </summary>
    public string? SymbolicLinkTarget { get; init; }

    /// <summary>
    /// Raw file attributes.
    /// </summary>
    public required FileAttributes Attributes { get; init; }

    // ===== PROCESSING =====

    /// <summary>
    /// Time taken to extract metadata.
    /// </summary>
    public required TimeSpan ExtractionTime { get; init; }

    /// <summary>
    /// Any errors encountered during extraction.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Convert to a dictionary suitable for ContentChunk.Metadata.
    /// Uses the FileSignalKeys namespace.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var dict = new Dictionary<string, object?>
        {
            // Identity
            [FileSignalKeys.FilePath] = FilePath,
            [FileSignalKeys.FileName] = FileName,
            [FileSignalKeys.FileNameWithoutExtension] = FileNameWithoutExtension,
            [FileSignalKeys.Extension] = Extension,
            [FileSignalKeys.Directory] = Directory,
            [FileSignalKeys.DirectoryName] = DirectoryName,
            [FileSignalKeys.DirectoryDepth] = DirectoryDepth,

            // Size
            [FileSignalKeys.SizeBytes] = SizeBytes,
            [FileSignalKeys.SizeFormatted] = SizeFormatted,

            // Hashes
            [FileSignalKeys.ContentHash] = ContentHash,
            [FileSignalKeys.Sha256Hash] = Sha256Hash,

            // Content analysis
            [FileSignalKeys.MimeType] = MimeType,
            [FileSignalKeys.MimeCategory] = MimeCategory,
            [FileSignalKeys.MagicBytesHex] = MagicBytesHex,
            [FileSignalKeys.Entropy] = Entropy,
            [FileSignalKeys.EntropyClassification] = EntropyClassification,
            [FileSignalKeys.IsBinary] = IsBinary,
            [FileSignalKeys.IsText] = IsText,
            [FileSignalKeys.PrintableAsciiPercent] = PrintableAsciiPercent,
            [FileSignalKeys.ContainsNullBytes] = ContainsNullBytes,

            // Timestamps
            [FileSignalKeys.CreatedAtUtc] = CreatedAtUtc,
            [FileSignalKeys.ModifiedAtUtc] = ModifiedAtUtc,
            [FileSignalKeys.AccessedAtUtc] = AccessedAtUtc,
            [FileSignalKeys.AgeDays] = Age.TotalDays,

            // Attributes
            [FileSignalKeys.Exists] = Exists,
            [FileSignalKeys.IsReadOnly] = IsReadOnly,
            [FileSignalKeys.IsHidden] = IsHidden,
            [FileSignalKeys.IsSystem] = IsSystem,
            [FileSignalKeys.IsSymbolicLink] = IsSymbolicLink,
            [FileSignalKeys.SymbolicLinkTarget] = SymbolicLinkTarget,
            [FileSignalKeys.Attributes] = Attributes.ToString(),

            // Processing
            [FileSignalKeys.ExtractionTimeMs] = ExtractionTime.TotalMilliseconds
        };

        return dict;
    }

    /// <summary>
    /// Get a subset of metadata suitable for embedding in search results.
    /// Compact version with essential fields only.
    /// </summary>
    public Dictionary<string, object?> ToSearchMetadata()
    {
        return new Dictionary<string, object?>
        {
            [FileSignalKeys.FileName] = FileName,
            [FileSignalKeys.Extension] = Extension,
            [FileSignalKeys.SizeFormatted] = SizeFormatted,
            [FileSignalKeys.MimeType] = MimeType,
            [FileSignalKeys.ContentHash] = ContentHash,
            [FileSignalKeys.ModifiedAtUtc] = ModifiedAtUtc,
            [FileSignalKeys.IsBinary] = IsBinary,
            [FileSignalKeys.EntropyClassification] = EntropyClassification
        };
    }

    /// <summary>
    /// Generate a text summary suitable for embedding.
    /// </summary>
    public string ToSummaryText()
    {
        var contentType = IsBinary ? "Binary" : "Text";
        return $"File: {FileName}\n" +
               $"Type: {MimeType} ({contentType})\n" +
               $"Size: {SizeFormatted}\n" +
               $"Entropy: {EntropyClassification}\n" +
               $"Modified: {ModifiedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{size:N0} {suffixes[suffixIndex]}"
            : $"{size:N2} {suffixes[suffixIndex]}";
    }

    private static string ClassifyEntropy(double entropy, long sizeBytes)
    {
        if (sizeBytes == 0) return "empty";
        if (entropy >= 7.9) return "random/encrypted";
        if (entropy >= 7.0) return "compressed";
        if (entropy >= 5.0) return "binary";
        if (entropy >= 3.0) return "text";
        return "repetitive";
    }
}
