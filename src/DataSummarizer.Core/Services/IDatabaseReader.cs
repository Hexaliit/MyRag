using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services;

/// <summary>
///     Interface for reading database files (SQLite, Access, etc.).
///     Provides schema extraction, relationship detection, and data streaming.
/// </summary>
public interface IDatabaseReader
{
    /// <summary>
    ///     File extensions this reader supports.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    ///     Check if a file is supported by this reader.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <returns>True if supported</returns>
    bool IsSupported(string filePath);

    /// <summary>
    ///     Get the complete schema of the database including tables, columns, and relationships.
    /// </summary>
    /// <param name="filePath">Path to the database file</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Database schema information</returns>
    Task<DatabaseSchema> GetSchemaAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    ///     Stream tables from the database with their data.
    /// </summary>
    /// <param name="filePath">Path to the database file</param>
    /// <param name="maxRowsPerTable">Maximum rows to read per table (0 = all)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async stream of table data</returns>
    IAsyncEnumerable<TableData> StreamTablesAsync(
        string filePath,
        int maxRowsPerTable = 0,
        CancellationToken ct = default);

    /// <summary>
    ///     Stream rows from a specific table.
    /// </summary>
    /// <param name="filePath">Path to the database file</param>
    /// <param name="tableName">Name of the table to read</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async stream of rows as dictionaries</returns>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamTableRowsAsync(
        string filePath,
        string tableName,
        CancellationToken ct = default);
}