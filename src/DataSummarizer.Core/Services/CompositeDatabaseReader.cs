using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services;

/// <summary>
/// Composite database reader that routes to the appropriate reader based on file extension.
/// Supports both SQLite (.db, .sqlite, .sqlite3) and Access (.accdb, .mdb) files.
/// </summary>
public class CompositeDatabaseReader : IDatabaseReader
{
    private readonly SqliteDatabaseReader _sqliteReader;
    private readonly AccessDatabaseReader _accessReader;
    private readonly ILogger<CompositeDatabaseReader>? _logger;

    public CompositeDatabaseReader(
        SqliteDatabaseReader sqliteReader,
        AccessDatabaseReader accessReader,
        ILogger<CompositeDatabaseReader>? logger = null)
    {
        _sqliteReader = sqliteReader;
        _accessReader = accessReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions =>
        _sqliteReader.SupportedExtensions
            .Concat(_accessReader.SupportedExtensions)
            .Distinct()
            .ToList();

    /// <inheritdoc />
    public bool IsSupported(string filePath)
    {
        return _sqliteReader.IsSupported(filePath) || _accessReader.IsSupported(filePath);
    }

    /// <inheritdoc />
    public Task<DatabaseSchema> GetSchemaAsync(string filePath, CancellationToken ct = default)
    {
        var reader = GetReaderForFile(filePath);
        _logger?.LogDebug("Using {ReaderType} for {FilePath}", reader.GetType().Name, filePath);
        return reader.GetSchemaAsync(filePath, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TableData> StreamTablesAsync(
        string filePath,
        int maxRowsPerTable = 0,
        CancellationToken ct = default)
    {
        var reader = GetReaderForFile(filePath);
        return reader.StreamTablesAsync(filePath, maxRowsPerTable, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamTableRowsAsync(
        string filePath,
        string tableName,
        CancellationToken ct = default)
    {
        var reader = GetReaderForFile(filePath);
        return reader.StreamTableRowsAsync(filePath, tableName, ct);
    }

    private IDatabaseReader GetReaderForFile(string filePath)
    {
        if (_sqliteReader.IsSupported(filePath))
            return _sqliteReader;

        if (_accessReader.IsSupported(filePath))
            return _accessReader;

        throw new NotSupportedException($"No database reader available for file: {filePath}");
    }
}
