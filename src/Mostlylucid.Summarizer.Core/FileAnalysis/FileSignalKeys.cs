namespace Mostlylucid.Summarizer.Core.FileAnalysis;

/// <summary>
///     Standard signal keys for file metadata.
///     All keys use the "file." namespace prefix for consistency.
/// </summary>
public static class FileSignalKeys
{
    /// <summary>
    ///     Namespace prefix for all file signals.
    /// </summary>
    public const string Prefix = "file.";

    // ===== IDENTITY =====
    public const string FilePath = "file.path";
    public const string FileName = "file.name";
    public const string FileNameWithoutExtension = "file.name_no_ext";
    public const string Extension = "file.extension";
    public const string Directory = "file.directory";
    public const string DirectoryName = "file.directory_name";
    public const string DirectoryDepth = "file.directory_depth";

    // ===== SIZE =====
    public const string SizeBytes = "file.size_bytes";
    public const string SizeFormatted = "file.size_formatted";

    // ===== HASHES =====
    public const string ContentHash = "file.content_hash";
    public const string Sha256Hash = "file.sha256_hash";

    // ===== CONTENT ANALYSIS =====
    public const string MimeType = "file.mime_type";
    public const string MimeCategory = "file.mime_category";
    public const string MagicBytes = "file.magic_bytes";
    public const string MagicBytesHex = "file.magic_bytes_hex";
    public const string DetectedType = "file.detected_type";
    public const string Entropy = "file.entropy";
    public const string EntropyClassification = "file.entropy_class";
    public const string IsBinary = "file.is_binary";
    public const string IsText = "file.is_text";
    public const string PrintableAsciiPercent = "file.printable_ascii_pct";
    public const string ContainsNullBytes = "file.contains_null_bytes";

    // ===== TIMESTAMPS =====
    public const string CreatedAtUtc = "file.created_at_utc";
    public const string ModifiedAtUtc = "file.modified_at_utc";
    public const string AccessedAtUtc = "file.accessed_at_utc";
    public const string AgeDays = "file.age_days";

    // ===== ATTRIBUTES =====
    public const string Exists = "file.exists";
    public const string IsReadOnly = "file.is_readonly";
    public const string IsHidden = "file.is_hidden";
    public const string IsSystem = "file.is_system";
    public const string IsSymbolicLink = "file.is_symlink";
    public const string SymbolicLinkTarget = "file.symlink_target";
    public const string Attributes = "file.attributes";
    public const string IsExecutable = "file.is_executable";
    public const string IsArchive = "file.is_archive";
    public const string IsCompressed = "file.is_compressed";
    public const string IsEncrypted = "file.is_encrypted";
    public const string IsTemporary = "file.is_temporary";

    // ===== PROCESSING =====
    public const string ExtractionTimeMs = "file.extraction_time_ms";

    // ===== PE FILES (exe, dll) =====
    public const string PeArchitecture = "file.pe.architecture";
    public const string PeSubsystem = "file.pe.subsystem";
    public const string PeIsDotNet = "file.pe.is_dotnet";
    public const string PeProductVersion = "file.pe.product_version";
    public const string PeFileVersion = "file.pe.file_version";
    public const string PeCompanyName = "file.pe.company_name";
    public const string PeProductName = "file.pe.product_name";
    public const string PeOriginalFilename = "file.pe.original_filename";
    public const string PeDescription = "file.pe.description";
    public const string PeCopyright = "file.pe.copyright";
    public const string PeImportCount = "file.pe.import_count";
    public const string PeExportCount = "file.pe.export_count";
    public const string PeSectionCount = "file.pe.section_count";
    public const string PeIsSigned = "file.pe.is_signed";

    // ===== ARCHIVE FILES =====
    public const string ArchiveEntryCount = "file.archive.entry_count";
    public const string ArchiveUncompressedSize = "file.archive.uncompressed_size";
    public const string ArchiveCompressionRatio = "file.archive.compression_ratio";
    public const string ArchiveFormat = "file.archive.format";

    // ===== TEXT FILES =====
    public const string TextEncoding = "file.text.encoding";
    public const string TextLineCount = "file.text.line_count";
    public const string TextHasBom = "file.text.has_bom";
    public const string TextLongestLine = "file.text.longest_line";
    public const string TextAvgLineLength = "file.text.avg_line_length";
}