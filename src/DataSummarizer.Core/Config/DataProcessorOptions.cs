namespace Mostlylucid.DocSummarizer.Data.Config;

/// <summary>
///     Configuration options for the data processor.
/// </summary>
public class DataProcessorOptions
{
    /// <summary>
    ///     Number of rows to include in each chunk for embedding.
    /// </summary>
    public int ChunkSize { get; set; } = 50;

    /// <summary>
    ///     Number of rows to overlap between chunks.
    /// </summary>
    public int ChunkOverlap { get; set; } = 5;

    /// <summary>
    ///     Maximum text length per chunk (truncate if exceeded).
    /// </summary>
    public int MaxChunkTextLength { get; set; } = 4000;

    /// <summary>
    ///     CSV options.
    /// </summary>
    public CsvOptions Csv { get; set; } = new();

    /// <summary>
    ///     JSON options.
    /// </summary>
    public JsonOptions Json { get; set; } = new();

    /// <summary>
    ///     Excel options.
    /// </summary>
    public ExcelOptions Excel { get; set; } = new();

    // Analysis options

    /// <summary>
    ///     Maximum number of rows to sample for analysis (0 = all rows).
    ///     Larger samples give more accurate statistics but use more memory.
    /// </summary>
    public int SampleSize { get; set; } = 10000;

    /// <summary>
    ///     Enable PII detection wave (emails, phones, SSNs, etc.).
    /// </summary>
    public bool EnablePiiDetection { get; set; } = true;

    /// <summary>
    ///     Enable outlier detection wave (IQR-based anomaly detection).
    /// </summary>
    public bool EnableOutlierDetection { get; set; } = true;

    /// <summary>
    ///     Enable correlation analysis wave (computes pairwise correlations).
    ///     Can be expensive for datasets with many numeric columns.
    /// </summary>
    public bool EnableCorrelation { get; set; } = false;

    /// <summary>
    ///     Use DuckDB for streaming analytics (recommended for large files).
    ///     When false, falls back to in-memory processing.
    /// </summary>
    public bool UseDuckDbAnalytics { get; set; } = true;
}

public class CsvOptions
{
    /// <summary>
    ///     Whether the first row contains headers.
    /// </summary>
    public bool HasHeaderRow { get; set; } = true;

    /// <summary>
    ///     Delimiter character.
    /// </summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>
    ///     Encoding for the CSV file.
    /// </summary>
    public string Encoding { get; set; } = "utf-8";
}

public class JsonOptions
{
    /// <summary>
    ///     Property path to array of records (e.g., "data.items").
    ///     If null, assumes root is array or single object.
    /// </summary>
    public string? RecordsPath { get; set; }

    /// <summary>
    ///     Whether to flatten nested objects.
    /// </summary>
    public bool FlattenNested { get; set; } = true;

    /// <summary>
    ///     Maximum nesting depth when flattening.
    /// </summary>
    public int MaxFlattenDepth { get; set; } = 3;
}

public class ExcelOptions
{
    /// <summary>
    ///     Sheet name to process (null = first sheet).
    /// </summary>
    public string? SheetName { get; set; }

    /// <summary>
    ///     Whether the first row contains headers.
    /// </summary>
    public bool HasHeaderRow { get; set; } = true;

    /// <summary>
    ///     Starting row (1-based, default = 1).
    /// </summary>
    public int StartRow { get; set; } = 1;

    /// <summary>
    ///     Starting column (1-based, default = 1).
    /// </summary>
    public int StartColumn { get; set; } = 1;
}