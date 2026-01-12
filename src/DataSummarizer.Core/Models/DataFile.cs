namespace Mostlylucid.DocSummarizer.Data.Models;

/// <summary>
/// Represents a data file to be analyzed.
/// Wraps file path with format detection and metadata.
/// </summary>
public class DataFile
{
    public DataFile(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Extension = Path.GetExtension(filePath).ToLowerInvariant();
        Format = DetectFormat(Extension);
    }

    /// <summary>
    /// Full path to the data file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// File name without path.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// File extension (lowercase, with dot).
    /// </summary>
    public string Extension { get; }

    /// <summary>
    /// Detected data format.
    /// </summary>
    public DataFormat Format { get; }

    /// <summary>
    /// File size in bytes (lazy loaded).
    /// </summary>
    public long SizeBytes => _sizeBytes ??= new FileInfo(FilePath).Length;
    private long? _sizeBytes;

    /// <summary>
    /// Whether the file exists.
    /// </summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Get the DuckDB read expression for this file.
    /// </summary>
    public string GetDuckDbReadExpression()
    {
        return Format switch
        {
            DataFormat.Csv => $"read_csv('{EscapePath(FilePath)}', auto_detect=true, ignore_errors=true)",
            DataFormat.Tsv => $"read_csv('{EscapePath(FilePath)}', auto_detect=true, delim='\\t', ignore_errors=true)",
            DataFormat.Json => $"read_json('{EscapePath(FilePath)}', auto_detect=true)",
            DataFormat.JsonLines => $"read_json('{EscapePath(FilePath)}', format='nd', auto_detect=true)",
            DataFormat.Parquet => $"read_parquet('{EscapePath(FilePath)}')",
            DataFormat.Excel => throw new NotSupportedException("Excel files require special handling - use ClosedXML"),
            DataFormat.SQLite => throw new NotSupportedException("SQLite files require special handling - use SqliteDatabaseReader"),
            DataFormat.Access => throw new NotSupportedException("Access files require special handling - use IDatabaseReader"),
            _ => throw new NotSupportedException($"Unsupported format: {Format}")
        };
    }

    private static string EscapePath(string path)
    {
        // DuckDB uses single quotes and needs backslash escaping on Windows
        return path.Replace("\\", "/").Replace("'", "''");
    }

    private static DataFormat DetectFormat(string extension)
    {
        return extension switch
        {
            ".csv" => DataFormat.Csv,
            ".tsv" => DataFormat.Tsv,
            ".json" => DataFormat.Json,
            ".jsonl" => DataFormat.JsonLines,
            ".parquet" => DataFormat.Parquet,
            ".xlsx" or ".xls" => DataFormat.Excel,
            ".db" or ".sqlite" or ".sqlite3" => DataFormat.SQLite,
            ".accdb" or ".mdb" => DataFormat.Access,
            _ => DataFormat.Unknown
        };
    }
}

/// <summary>
/// Supported data file formats.
/// </summary>
public enum DataFormat
{
    Unknown,
    Csv,
    Tsv,
    Json,
    JsonLines,
    Parquet,
    Excel,
    SQLite,
    Access
}
