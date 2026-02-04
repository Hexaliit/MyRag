namespace Mostlylucid.Summarizer.Core.FileAnalysis;

/// <summary>
///     Detects MIME types using both file extension and magic bytes.
/// </summary>
public static class MimeTypeDetector
{
    private const string DefaultMimeType = "application/octet-stream";

    /// <summary>
    ///     Magic byte signatures for common file types.
    /// </summary>
    private static readonly (byte[] Signature, int Offset, string MimeType)[] MagicSignatures =
    [
        // Images
        ([0xFF, 0xD8, 0xFF], 0, "image/jpeg"),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], 0, "image/png"),
        ([0x47, 0x49, 0x46, 0x38, 0x37, 0x61], 0, "image/gif"), // GIF87a
        ([0x47, 0x49, 0x46, 0x38, 0x39, 0x61], 0, "image/gif"), // GIF89a
        ([0x52, 0x49, 0x46, 0x46], 0, "image/webp"), // RIFF (WebP starts with RIFF)
        ([0x42, 0x4D], 0, "image/bmp"),
        ([0x49, 0x49, 0x2A, 0x00], 0, "image/tiff"), // Little-endian TIFF
        ([0x4D, 0x4D, 0x00, 0x2A], 0, "image/tiff"), // Big-endian TIFF
        ([0x00, 0x00, 0x01, 0x00], 0, "image/x-icon"),

        // Documents
        ([0x25, 0x50, 0x44, 0x46], 0, "application/pdf"), // %PDF
        ([0x50, 0x4B, 0x03, 0x04], 0, "application/zip"), // ZIP (DOCX, XLSX, PPTX, etc.)
        ([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], 0, "application/msword"), // OLE (DOC, XLS, PPT)

        // Archives
        ([0x1F, 0x8B], 0, "application/gzip"),
        ([0x42, 0x5A, 0x68], 0, "application/x-bzip2"),
        ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, "application/x-7z-compressed"),
        ([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], 0, "application/x-rar-compressed"),
        ([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], 0, "application/x-xz"),
        ([0x75, 0x73, 0x74, 0x61, 0x72], 257, "application/x-tar"), // ustar at offset 257

        // Audio
        ([0x49, 0x44, 0x33], 0, "audio/mpeg"), // ID3 tag (MP3)
        ([0xFF, 0xFB], 0, "audio/mpeg"), // MP3 sync
        ([0xFF, 0xFA], 0, "audio/mpeg"),
        ([0xFF, 0xF3], 0, "audio/mpeg"),
        ([0xFF, 0xF2], 0, "audio/mpeg"),
        ([0x66, 0x4C, 0x61, 0x43], 0, "audio/flac"), // fLaC
        ([0x4F, 0x67, 0x67, 0x53], 0, "audio/ogg"), // OggS

        // Video
        ([0x00, 0x00, 0x00], 0, "video/mp4"), // ftyp box (rough check)
        ([0x1A, 0x45, 0xDF, 0xA3], 0, "video/webm"), // EBML (WebM/MKV)

        // Executables
        ([0x4D, 0x5A], 0, "application/x-msdownload"), // MZ (PE/EXE/DLL)
        ([0x7F, 0x45, 0x4C, 0x46], 0, "application/x-elf"), // ELF

        // Data formats
        ([0x50, 0x41, 0x52, 0x31], 0, "application/x-parquet"), // PAR1
        ([0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33], 0,
            "application/x-sqlite3"),

        // Text with BOM
        ([0xEF, 0xBB, 0xBF], 0, "text/plain"), // UTF-8 BOM
        ([0xFF, 0xFE], 0, "text/plain"), // UTF-16 LE BOM
        ([0xFE, 0xFF], 0, "text/plain") // UTF-16 BE BOM
    ];

    /// <summary>
    ///     Extension to MIME type mapping.
    /// </summary>
    private static readonly Dictionary<string, string> ExtensionMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".rtf"] = "application/rtf",
        [".epub"] = "application/epub+zip",

        // Text
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xml"] = "application/xml",
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",

        // Code
        [".js"] = "application/javascript",
        [".mjs"] = "application/javascript",
        [".ts"] = "application/typescript",
        [".tsx"] = "application/typescript",
        [".jsx"] = "application/javascript",
        [".json"] = "application/json",
        [".jsonl"] = "application/x-ndjson",
        [".yaml"] = "application/x-yaml",
        [".yml"] = "application/x-yaml",
        [".cs"] = "text/x-csharp",
        [".py"] = "text/x-python",
        [".java"] = "text/x-java",
        [".c"] = "text/x-c",
        [".cpp"] = "text/x-c++",
        [".h"] = "text/x-c",
        [".hpp"] = "text/x-c++",
        [".go"] = "text/x-go",
        [".rs"] = "text/x-rust",
        [".rb"] = "text/x-ruby",
        [".php"] = "text/x-php",
        [".sh"] = "application/x-sh",
        [".bash"] = "application/x-sh",
        [".ps1"] = "application/x-powershell",
        [".sql"] = "application/sql",

        // Images
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tiff"] = "image/tiff",
        [".tif"] = "image/tiff",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
        [".avif"] = "image/avif",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",

        // Audio
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/opus",
        [".wma"] = "audio/x-ms-wma",

        // Video
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
        [".mov"] = "video/quicktime",
        [".wmv"] = "video/x-ms-wmv",
        [".flv"] = "video/x-flv",
        [".mpeg"] = "video/mpeg",
        [".mpg"] = "video/mpeg",

        // Archives
        [".zip"] = "application/zip",
        [".rar"] = "application/x-rar-compressed",
        [".7z"] = "application/x-7z-compressed",
        [".tar"] = "application/x-tar",
        [".gz"] = "application/gzip",
        [".bz2"] = "application/x-bzip2",
        [".xz"] = "application/x-xz",
        [".zst"] = "application/zstd",

        // Data
        [".parquet"] = "application/x-parquet",
        [".db"] = "application/x-sqlite3",
        [".sqlite"] = "application/x-sqlite3",
        [".sqlite3"] = "application/x-sqlite3",
        [".accdb"] = "application/msaccess",
        [".mdb"] = "application/msaccess",

        // Executables
        [".exe"] = "application/x-msdownload",
        [".dll"] = "application/x-msdownload",
        [".msi"] = "application/x-msi",
        [".app"] = "application/x-apple-diskimage",
        [".dmg"] = "application/x-apple-diskimage",
        [".deb"] = "application/x-deb",
        [".rpm"] = "application/x-rpm",

        // Fonts
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".eot"] = "application/vnd.ms-fontobject",

        // Other
        [".wasm"] = "application/wasm",
        [".onnx"] = "application/x-onnx",
        [".safetensors"] = "application/x-safetensors",
        [".bin"] = "application/octet-stream"
    };

    /// <summary>
    ///     Detect MIME type from file extension.
    /// </summary>
    public static string FromExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return DefaultMimeType;

        var ext = extension.StartsWith('.') ? extension : $".{extension}";
        return ExtensionMimeTypes.TryGetValue(ext, out var mimeType)
            ? mimeType
            : DefaultMimeType;
    }

    /// <summary>
    ///     Detect MIME type from magic bytes.
    /// </summary>
    public static string? FromMagicBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return null;

        foreach (var (signature, offset, mimeType) in MagicSignatures)
        {
            if (bytes.Length < offset + signature.Length)
                continue;

            var slice = bytes.Slice(offset, signature.Length);
            if (slice.SequenceEqual(signature))
                return mimeType;
        }

        return null;
    }

    /// <summary>
    ///     Detect MIME type from file, using magic bytes first, then extension fallback.
    /// </summary>
    public static string Detect(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return FromExtension(Path.GetExtension(filePath));

        try
        {
            // Read first 512 bytes for magic detection
            var buffer = new byte[512];
            using var stream = File.OpenRead(filePath);
            var bytesRead = stream.Read(buffer, 0, buffer.Length);

            if (bytesRead > 0)
            {
                var magicMime = FromMagicBytes(buffer.AsSpan(0, bytesRead));
                if (magicMime != null)
                {
                    // Special handling for ZIP-based formats
                    if (magicMime == "application/zip") return RefineZipMimeType(filePath);
                    return magicMime;
                }
            }
        }
        catch
        {
            // Fall through to extension-based detection
        }

        return FromExtension(Path.GetExtension(filePath));
    }

    /// <summary>
    ///     Detect MIME type from stream and extension.
    /// </summary>
    public static string Detect(Stream stream, string? extension)
    {
        if (stream.CanRead && stream.Length > 0)
        {
            var buffer = new byte[Math.Min(512, stream.Length)];
            var position = stream.Position;

            try
            {
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var magicMime = FromMagicBytes(buffer.AsSpan(0, bytesRead));
                    if (magicMime != null && magicMime != "application/zip")
                        return magicMime;
                }
            }
            finally
            {
                if (stream.CanSeek)
                    stream.Position = position;
            }
        }

        return FromExtension(extension ?? string.Empty);
    }

    /// <summary>
    ///     Get magic bytes from file (first N bytes as hex string).
    /// </summary>
    public static (byte[] Bytes, string Hex) GetMagicBytes(string filePath, int count = 16)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return ([], string.Empty);

        try
        {
            var buffer = new byte[count];
            using var stream = File.OpenRead(filePath);
            var bytesRead = stream.Read(buffer, 0, buffer.Length);

            if (bytesRead == 0)
                return ([], string.Empty);

            var bytes = buffer.AsSpan(0, bytesRead).ToArray();
#if NET9_0_OR_GREATER
            var hex = Convert.ToHexStringLower(bytes);
#else
            var hex = Convert.ToHexString(bytes).ToLowerInvariant();
#endif
            return (bytes, hex);
        }
        catch
        {
            return ([], string.Empty);
        }
    }

    /// <summary>
    ///     Refine ZIP MIME type based on file extension (for OOXML documents).
    /// </summary>
    private static string RefineZipMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".odt" => "application/vnd.oasis.opendocument.text",
            ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
            ".odp" => "application/vnd.oasis.opendocument.presentation",
            ".epub" => "application/epub+zip",
            ".jar" => "application/java-archive",
            ".apk" => "application/vnd.android.package-archive",
            ".xpi" => "application/x-xpinstall",
            _ => "application/zip"
        };
    }
}