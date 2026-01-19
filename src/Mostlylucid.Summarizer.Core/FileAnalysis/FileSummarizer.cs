using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Mostlylucid.Summarizer.Core.Utilities;

namespace Mostlylucid.Summarizer.Core.FileAnalysis;

/// <summary>
/// Extracts universal file metadata from any file type.
/// Provides the foundation for all pipeline processing.
/// </summary>
public sealed class FileSummarizer : IFileSummarizer
{
    private readonly ILogger<FileSummarizer> _logger;
    private const int SampleSize = 8192; // 8KB sample for content analysis

    public FileSummarizer(ILogger<FileSummarizer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FileMetadata> ExtractMetadataAsync(string filePath, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<string>();

        try
        {
            var fileInfo = new FileInfo(filePath);

            if (!fileInfo.Exists)
            {
                stopwatch.Stop();
                return CreateNonExistentMetadata(filePath, stopwatch.Elapsed, errors);
            }

            // Read file sample for content analysis
            var (contentHash, sha256Hash, magicBytesHex, entropy, isBinary, printablePercent, containsNulls) =
                await Task.Run(() => AnalyzeContent(filePath, fileInfo.Length), ct);

            // Detect MIME type
            var mimeType = DetectMimeType(filePath);

            // Calculate directory info
            var directoryName = Path.GetFileName(fileInfo.DirectoryName ?? string.Empty);
            var directoryDepth = CalculateDirectoryDepth(filePath);

            // Get symlink target if applicable
            string? symlinkTarget = fileInfo.LinkTarget;

            stopwatch.Stop();

            return new FileMetadata
            {
                // Identity
                FilePath = fileInfo.FullName,
                FileName = fileInfo.Name,
                FileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath),
                Extension = fileInfo.Extension.ToLowerInvariant(),
                Directory = fileInfo.DirectoryName ?? string.Empty,
                DirectoryName = directoryName,
                DirectoryDepth = directoryDepth,

                // Size
                SizeBytes = fileInfo.Length,

                // Hashes
                ContentHash = contentHash,
                Sha256Hash = sha256Hash,

                // Content analysis
                MimeType = mimeType,
                MagicBytesHex = magicBytesHex,
                Entropy = entropy,
                IsBinary = isBinary,
                PrintableAsciiPercent = printablePercent,
                ContainsNullBytes = containsNulls,

                // Timestamps
                CreatedAtUtc = new DateTimeOffset(fileInfo.CreationTimeUtc),
                ModifiedAtUtc = new DateTimeOffset(fileInfo.LastWriteTimeUtc),
                AccessedAtUtc = new DateTimeOffset(fileInfo.LastAccessTimeUtc),

                // Attributes
                Exists = true,
                IsReadOnly = fileInfo.IsReadOnly,
                IsHidden = (fileInfo.Attributes & FileAttributes.Hidden) != 0,
                IsSystem = (fileInfo.Attributes & FileAttributes.System) != 0,
                IsSymbolicLink = symlinkTarget != null,
                SymbolicLinkTarget = symlinkTarget,
                Attributes = fileInfo.Attributes,

                // Processing
                ExtractionTime = stopwatch.Elapsed,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting metadata from {FilePath}", filePath);
            errors.Add(ex.Message);
            stopwatch.Stop();

            return CreateNonExistentMetadata(filePath, stopwatch.Elapsed, errors);
        }
    }

    /// <inheritdoc />
    public async Task<FileMetadata> ExtractMetadataAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<string>();

        try
        {
            // Analyze stream content
            var (contentHash, sha256Hash, magicBytesHex, entropy, isBinary, printablePercent, containsNulls) =
                await Task.Run(() => AnalyzeStreamContent(stream), ct);

            // Detect MIME type
            var mimeType = MimeTypeDetector.Detect(stream, Path.GetExtension(fileName));

            var sizeBytes = stream.CanSeek ? stream.Length : 0;

            stopwatch.Stop();

            return new FileMetadata
            {
                FilePath = fileName,
                FileName = Path.GetFileName(fileName),
                FileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName),
                Extension = Path.GetExtension(fileName).ToLowerInvariant(),
                Directory = string.Empty,
                DirectoryName = string.Empty,
                DirectoryDepth = 0,
                SizeBytes = sizeBytes,
                ContentHash = contentHash,
                Sha256Hash = sha256Hash,
                MimeType = mimeType,
                MagicBytesHex = magicBytesHex,
                Entropy = entropy,
                IsBinary = isBinary,
                PrintableAsciiPercent = printablePercent,
                ContainsNullBytes = containsNulls,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ModifiedAtUtc = DateTimeOffset.UtcNow,
                AccessedAtUtc = DateTimeOffset.UtcNow,
                Exists = true,
                IsReadOnly = false,
                IsHidden = false,
                IsSystem = false,
                IsSymbolicLink = false,
                SymbolicLinkTarget = null,
                Attributes = FileAttributes.Normal,
                ExtractionTime = stopwatch.Elapsed,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting metadata from stream for {FileName}", fileName);
            errors.Add(ex.Message);
            stopwatch.Stop();

            return CreateNonExistentMetadata(fileName, stopwatch.Elapsed, errors);
        }
    }

    /// <summary>
    /// Analyze file content: compute hashes, entropy, and binary detection.
    /// </summary>
    private (string ContentHash, string Sha256Hash, string MagicBytesHex, double Entropy, bool IsBinary, double PrintablePercent, bool ContainsNulls) AnalyzeContent(string filePath, long fileSize)
    {
        if (fileSize == 0)
            return (string.Empty, string.Empty, string.Empty, 0, false, 100, false);

        try
        {
            using var stream = System.IO.File.OpenRead(filePath);
            return AnalyzeStreamContent(stream);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty, 0, false, 0, false);
        }
    }

    /// <summary>
    /// Analyze stream content: compute hashes, entropy, and binary detection.
    /// </summary>
    private (string ContentHash, string Sha256Hash, string MagicBytesHex, double Entropy, bool IsBinary, double PrintablePercent, bool ContainsNulls) AnalyzeStreamContent(Stream stream)
    {
        if (!stream.CanRead || (stream.CanSeek && stream.Length == 0))
            return (string.Empty, string.Empty, string.Empty, 0, false, 100, false);

        var startPosition = stream.CanSeek ? stream.Position : 0;

        try
        {
            // Read sample for analysis
            var sample = new byte[Math.Min(SampleSize, stream.CanSeek ? stream.Length : SampleSize)];
            var sampleBytesRead = ReadFully(stream, sample);

            if (sampleBytesRead == 0)
                return (string.Empty, string.Empty, string.Empty, 0, false, 100, false);

            // Extract magic bytes (first 16 bytes)
            var magicLength = Math.Min(16, sampleBytesRead);
#if NET9_0_OR_GREATER
            var magicBytesHex = Convert.ToHexStringLower(sample.AsSpan(0, magicLength));
#else
            var magicBytesHex = Convert.ToHexString(sample.AsSpan(0, magicLength)).ToLowerInvariant();
#endif

            // Calculate entropy and binary detection from sample
            var sampleSpan = sample.AsSpan(0, sampleBytesRead);
            var entropy = CalculateEntropy(sampleSpan);
            var (printablePercent, containsNulls) = AnalyzePrintable(sampleSpan);
            var isBinary = containsNulls || printablePercent < 85 || entropy > 6.5;

            // Reset stream and compute full hashes
            if (stream.CanSeek)
                stream.Position = startPosition;

            var contentHash = ContentHasher.ComputeHash(stream);

            if (stream.CanSeek)
                stream.Position = startPosition;

            var sha256Hash = ComputeSha256(stream);

            return (contentHash, sha256Hash, magicBytesHex, entropy, isBinary, printablePercent, containsNulls);
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = startPosition;
        }
    }

    /// <summary>
    /// Calculate Shannon entropy of data (0-8 scale).
    /// </summary>
    private static double CalculateEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;

        Span<int> frequency = stackalloc int[256];
        frequency.Clear();

        foreach (var b in data)
            frequency[b]++;

        double entropy = 0;
        var length = (double)data.Length;

        for (int i = 0; i < 256; i++)
        {
            if (frequency[i] == 0) continue;

            var p = frequency[i] / length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    /// <summary>
    /// Analyze printable ASCII percentage and null byte presence.
    /// </summary>
    private static (double PrintablePercent, bool ContainsNulls) AnalyzePrintable(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return (100, false);

        int printable = 0;
        bool hasNulls = false;

        foreach (var b in data)
        {
            if (b == 0)
            {
                hasNulls = true;
            }
            // Printable ASCII: 0x20-0x7E, plus common whitespace (tab, newline, carriage return)
            else if ((b >= 0x20 && b <= 0x7E) || b == 0x09 || b == 0x0A || b == 0x0D)
            {
                printable++;
            }
        }

        return (printable * 100.0 / data.Length, hasNulls);
    }

    /// <summary>
    /// Compute SHA-256 hash of stream.
    /// </summary>
    private static string ComputeSha256(Stream stream)
    {
        try
        {
            var hash = SHA256.HashData(stream);
#if NET9_0_OR_GREATER
            return Convert.ToHexStringLower(hash);
#else
            return Convert.ToHexString(hash).ToLowerInvariant();
#endif
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Calculate directory depth from file path.
    /// </summary>
    private static int CalculateDirectoryDepth(string filePath)
    {
        try
        {
            var normalized = Path.GetFullPath(filePath);
            var separators = normalized.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
            return separators;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Read stream fully into buffer, handling partial reads.
    /// </summary>
    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// Create metadata for non-existent or error files.
    /// </summary>
    private FileMetadata CreateNonExistentMetadata(string filePath, TimeSpan elapsed, List<string> errors)
    {
        return new FileMetadata
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            FileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath),
            Extension = Path.GetExtension(filePath).ToLowerInvariant(),
            Directory = Path.GetDirectoryName(filePath) ?? string.Empty,
            DirectoryName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty),
            DirectoryDepth = 0,
            SizeBytes = 0,
            ContentHash = string.Empty,
            Sha256Hash = string.Empty,
            MimeType = MimeTypeDetector.FromExtension(Path.GetExtension(filePath)),
            MagicBytesHex = string.Empty,
            Entropy = 0,
            IsBinary = false,
            PrintableAsciiPercent = 0,
            ContainsNullBytes = false,
            CreatedAtUtc = DateTimeOffset.MinValue,
            ModifiedAtUtc = DateTimeOffset.MinValue,
            AccessedAtUtc = DateTimeOffset.MinValue,
            Exists = false,
            IsReadOnly = false,
            IsHidden = false,
            IsSystem = false,
            IsSymbolicLink = false,
            SymbolicLinkTarget = null,
            Attributes = FileAttributes.Normal,
            ExtractionTime = elapsed,
            Errors = errors
        };
    }

    /// <inheritdoc />
    public string GetContentHash(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            return string.Empty;

        try
        {
            using var stream = System.IO.File.OpenRead(filePath);
            return ContentHasher.ComputeHash(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error computing hash for {FilePath}", filePath);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public string GetContentHash(Stream stream)
    {
        if (stream == null || !stream.CanRead)
            return string.Empty;

        try
        {
            return ContentHasher.ComputeHash(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error computing hash from stream");
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public string DetectMimeType(string filePath)
    {
        return MimeTypeDetector.Detect(filePath);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object?>> GetExtendedMetadataAsync(string filePath, CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, object?>();

        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            return metadata;

        var mimeType = DetectMimeType(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            // Get magic bytes
            var (magicBytes, magicHex) = MimeTypeDetector.GetMagicBytes(filePath);
            if (magicBytes.Length > 0)
            {
                metadata[FileSignalKeys.MagicBytes] = magicBytes;
                metadata[FileSignalKeys.MagicBytesHex] = magicHex;
            }

            // PE file extended metadata (exe, dll) - Windows only
            if (extension is ".exe" or ".dll" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await ExtractPeMetadataAsync(filePath, metadata, ct);
            }

            // Archive inspection (cross-platform)
            if (IsArchiveExtension(extension))
            {
                await ExtractArchiveMetadataAsync(filePath, extension, metadata, ct);
            }

            // Text file extended metadata
            if (mimeType.StartsWith("text/") || IsLikelyTextFile(extension))
            {
                await ExtractTextMetadataAsync(filePath, metadata, ct);
            }

            // Archive attributes
            var fileInfo = new FileInfo(filePath);
            if ((fileInfo.Attributes & FileAttributes.Compressed) != 0)
            {
                metadata[FileSignalKeys.IsCompressed] = true;
            }

            if ((fileInfo.Attributes & FileAttributes.Encrypted) != 0)
            {
                metadata[FileSignalKeys.IsEncrypted] = true;
            }

            if ((fileInfo.Attributes & FileAttributes.Temporary) != 0)
            {
                metadata[FileSignalKeys.IsTemporary] = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting extended metadata from {FilePath}", filePath);
        }

        return metadata;
    }

    private async Task ExtractPeMetadataAsync(string filePath, Dictionary<string, object?> metadata, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(filePath);
                metadata[FileSignalKeys.PeIsDotNet] = true;

                if (assemblyName.Version != null)
                {
                    metadata[FileSignalKeys.PeFileVersion] = assemblyName.Version.ToString();
                }
            }
            catch
            {
                // Not a .NET assembly
                metadata[FileSignalKeys.PeIsDotNet] = false;
            }

            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
                    metadata[FileSignalKeys.PeProductVersion] = versionInfo.ProductVersion;
                if (!string.IsNullOrEmpty(versionInfo.FileVersion))
                    metadata[FileSignalKeys.PeFileVersion] = versionInfo.FileVersion;
                if (!string.IsNullOrEmpty(versionInfo.CompanyName))
                    metadata[FileSignalKeys.PeCompanyName] = versionInfo.CompanyName;
                if (!string.IsNullOrEmpty(versionInfo.ProductName))
                    metadata[FileSignalKeys.PeProductName] = versionInfo.ProductName;
                if (!string.IsNullOrEmpty(versionInfo.OriginalFilename))
                    metadata[FileSignalKeys.PeOriginalFilename] = versionInfo.OriginalFilename;
                if (!string.IsNullOrEmpty(versionInfo.FileDescription))
                    metadata[FileSignalKeys.PeDescription] = versionInfo.FileDescription;
                if (!string.IsNullOrEmpty(versionInfo.LegalCopyright))
                    metadata[FileSignalKeys.PeCopyright] = versionInfo.LegalCopyright;
            }
            catch
            {
                // Ignore version info errors
            }
        }, ct);
    }

    private async Task ExtractTextMetadataAsync(string filePath, Dictionary<string, object?> metadata, CancellationToken ct)
    {
        const int maxBytesToRead = 1024 * 1024; // 1MB sample for encoding detection

        await Task.Run(() =>
        {
            try
            {
                using var stream = System.IO.File.OpenRead(filePath);

                // Check for BOM
                var bom = new byte[4];
                var bomRead = stream.Read(bom, 0, 4);
                stream.Position = 0;

                Encoding? encoding = null;
                var hasBom = false;

                if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                {
                    encoding = Encoding.UTF8;
                    hasBom = true;
                }
                else if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                {
                    encoding = Encoding.Unicode;
                    hasBom = true;
                }
                else if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                {
                    encoding = Encoding.BigEndianUnicode;
                    hasBom = true;
                }
                else if (bomRead >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
                {
                    encoding = Encoding.UTF32;
                    hasBom = true;
                }
                else
                {
                    // Try to detect UTF-8 by scanning
                    var bufferSize = (int)Math.Min(maxBytesToRead, stream.Length);
                    var buffer = new byte[bufferSize];
                    var totalRead = ReadFully(stream, buffer);
                    stream.Position = 0;

                    encoding = IsValidUtf8(buffer.AsSpan(0, totalRead)) ? Encoding.UTF8 : Encoding.Default;
                }

                metadata[FileSignalKeys.TextEncoding] = encoding?.WebName ?? "unknown";
                metadata[FileSignalKeys.TextHasBom] = hasBom;

                // Count lines and calculate stats
                int lineCount = 0;
                int longestLine = 0;
                long totalLineLength = 0;

                using var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineCount++;
                    longestLine = Math.Max(longestLine, line.Length);
                    totalLineLength += line.Length;
                }

                metadata[FileSignalKeys.TextLineCount] = lineCount;
                metadata[FileSignalKeys.TextLongestLine] = longestLine;
                metadata[FileSignalKeys.TextAvgLineLength] = lineCount > 0 ? totalLineLength / (double)lineCount : 0;
            }
            catch
            {
                // Ignore text analysis errors
            }
        }, ct);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            if (bytes[i] <= 0x7F)
            {
                i++;
            }
            else if (bytes[i] >= 0xC2 && bytes[i] <= 0xDF)
            {
                if (i + 1 >= bytes.Length || bytes[i + 1] < 0x80 || bytes[i + 1] > 0xBF)
                    return false;
                i += 2;
            }
            else if (bytes[i] >= 0xE0 && bytes[i] <= 0xEF)
            {
                if (i + 2 >= bytes.Length)
                    return false;
                if (bytes[i + 1] < 0x80 || bytes[i + 1] > 0xBF)
                    return false;
                if (bytes[i + 2] < 0x80 || bytes[i + 2] > 0xBF)
                    return false;
                i += 3;
            }
            else if (bytes[i] >= 0xF0 && bytes[i] <= 0xF4)
            {
                if (i + 3 >= bytes.Length)
                    return false;
                if (bytes[i + 1] < 0x80 || bytes[i + 1] > 0xBF)
                    return false;
                if (bytes[i + 2] < 0x80 || bytes[i + 2] > 0xBF)
                    return false;
                if (bytes[i + 3] < 0x80 || bytes[i + 3] > 0xBF)
                    return false;
                i += 4;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLikelyTextFile(string extension)
    {
        return extension switch
        {
            ".txt" or ".md" or ".markdown" or ".html" or ".htm" or ".xml" or
            ".css" or ".js" or ".ts" or ".jsx" or ".tsx" or ".json" or ".jsonl" or
            ".yaml" or ".yml" or ".cs" or ".py" or ".java" or ".c" or ".cpp" or
            ".h" or ".hpp" or ".go" or ".rs" or ".rb" or ".php" or ".sh" or
            ".bash" or ".ps1" or ".sql" or ".csv" or ".tsv" or ".log" or
            ".ini" or ".cfg" or ".conf" or ".toml" or ".env" => true,
            _ => false
        };
    }

    private static bool IsArchiveExtension(string extension)
    {
        return extension switch
        {
            ".zip" or ".jar" or ".nupkg" or ".docx" or ".xlsx" or ".pptx" or ".odt" or
            ".epub" or ".apk" or ".ipa" or ".xpi" or ".crx" or ".vsix" or ".war" or
            ".gz" or ".tgz" or ".tar.gz" => true,
            _ => false
        };
    }

    /// <summary>
    /// Extract archive metadata (entry count, total uncompressed size, compression ratio).
    /// Cross-platform using System.IO.Compression for ZIP-based formats.
    /// </summary>
    private async Task ExtractArchiveMetadataAsync(
        string filePath,
        string extension,
        Dictionary<string, object?> metadata,
        CancellationToken ct)
    {
        await Task.Run(() =>
        {
            try
            {
                // ZIP-based formats (ZIP, JAR, Office, EPUB, APK, etc.)
                if (extension is ".zip" or ".jar" or ".nupkg" or ".docx" or ".xlsx" or ".pptx" or
                    ".odt" or ".epub" or ".apk" or ".ipa" or ".xpi" or ".crx" or ".vsix" or ".war")
                {
                    ExtractZipMetadata(filePath, metadata);
                }
                // GZip single-file compression
                else if (extension is ".gz" or ".tgz" or ".tar.gz")
                {
                    ExtractGzipMetadata(filePath, metadata);
                }

                // Detect archive format from magic bytes
                metadata[FileSignalKeys.ArchiveFormat] = DetectArchiveFormat(extension);
            }
            catch (InvalidDataException)
            {
                // Corrupted or not actually an archive
                metadata[FileSignalKeys.ArchiveFormat] = "invalid";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to extract archive metadata from {FilePath}", filePath);
            }
        }, ct);
    }

    private static void ExtractZipMetadata(string filePath, Dictionary<string, object?> metadata)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entryCount = archive.Entries.Count;
        var compressedSize = archive.Entries.Sum(e => e.CompressedLength);
        var uncompressedSize = archive.Entries.Sum(e => e.Length);

        metadata[FileSignalKeys.ArchiveEntryCount] = entryCount;
        metadata[FileSignalKeys.ArchiveUncompressedSize] = uncompressedSize;

        if (compressedSize > 0)
        {
            metadata[FileSignalKeys.ArchiveCompressionRatio] = Math.Round((double)uncompressedSize / compressedSize, 2);
        }

        // Collect unique extensions in archive
        var extensions = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.EndsWith('/'))
            .Select(e => Path.GetExtension(e.Name).ToLowerInvariant())
            .Where(ext => !string.IsNullOrEmpty(ext))
            .Distinct()
            .OrderBy(e => e)
            .ToList();

        if (extensions.Count > 0)
        {
            metadata["file.archive.extensions"] = extensions;
        }
    }

    private static void ExtractGzipMetadata(string filePath, Dictionary<string, object?> metadata)
    {
        using var stream = System.IO.File.OpenRead(filePath);
        using var gzipStream = new GZipStream(stream, CompressionMode.Decompress);

        // Read to determine uncompressed size (for small files only)
        var compressedSize = new FileInfo(filePath).Length;
        long uncompressedSize = 0;
        var buffer = new byte[8192];
        int read;

        // Limit to 100MB to prevent memory issues
        const long maxDecompressSize = 100 * 1024 * 1024;
        while ((read = gzipStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            uncompressedSize += read;
            if (uncompressedSize > maxDecompressSize)
            {
                // Estimate based on what we've read
                break;
            }
        }

        metadata[FileSignalKeys.ArchiveEntryCount] = 1; // GZip is single-file
        metadata[FileSignalKeys.ArchiveUncompressedSize] = uncompressedSize;

        if (compressedSize > 0)
        {
            metadata[FileSignalKeys.ArchiveCompressionRatio] = Math.Round((double)uncompressedSize / compressedSize, 2);
        }
    }

    private static string DetectArchiveFormat(string extension)
    {
        return extension switch
        {
            ".zip" => "zip",
            ".jar" or ".war" => "jar",
            ".nupkg" => "nupkg",
            ".docx" or ".xlsx" or ".pptx" => "office-openxml",
            ".odt" or ".ods" or ".odp" => "opendocument",
            ".epub" => "epub",
            ".apk" => "apk",
            ".ipa" => "ipa",
            ".xpi" or ".crx" => "browser-extension",
            ".vsix" => "vsix",
            ".gz" => "gzip",
            ".tgz" or ".tar.gz" => "tar+gzip",
            ".7z" => "7zip",
            ".rar" => "rar",
            ".tar" => "tar",
            ".bz2" => "bzip2",
            ".xz" => "xz",
            _ => "unknown"
        };
    }
}
