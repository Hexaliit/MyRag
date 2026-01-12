using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services;

/// <summary>
/// Database reader implementation for Microsoft Access files (.mdb, .accdb).
/// Note: Access support is currently limited. Cross-platform reading of Access files
/// requires a stable C# library. Options include:
/// - MMKiwi.MdbReader (pure C#, but pre-alpha)
/// - System.Data.Odbc with ACE driver (Windows only)
/// - EntityFrameworkCore.Jet (requires ACE driver)
///
/// This implementation provides the interface for future completion.
/// </summary>
public class AccessDatabaseReader : IDatabaseReader
{
    private readonly ILogger<AccessDatabaseReader>? _logger;

    public AccessDatabaseReader(ILogger<AccessDatabaseReader>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => [".accdb", ".mdb"];

    /// <inheritdoc />
    public bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    /// <inheritdoc />
    public Task<DatabaseSchema> GetSchemaAsync(string filePath, CancellationToken ct = default)
    {
        _logger?.LogWarning("Access database support is not yet fully implemented. File: {FilePath}", filePath);

        // Return a minimal schema with file info
        var schema = new DatabaseSchema
        {
            FilePath = filePath,
            Type = DatabaseType.Access,
            Tables = [],
            Relationships = [],
            Indexes = []
        };

        // Add a placeholder table indicating the file was detected
        schema.Tables.Add(new DatabaseTableInfo
        {
            Name = "__access_file_detected__",
            Columns =
            [
                new DatabaseColumnInfo
                {
                    Name = "message",
                    DataType = "text",
                    IsNullable = false,
                    Ordinal = 0
                }
            ],
            RowCount = 1,
            PrimaryKey = [],
            IsSystemTable = true
        });

        return Task.FromResult(schema);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TableData> StreamTablesAsync(
        string filePath,
        int maxRowsPerTable = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger?.LogWarning("Access database streaming is not yet implemented. File: {FilePath}", filePath);

        // Return a single placeholder table
        yield return new TableData
        {
            TableName = "__access_file_detected__",
            Columns = ["message", "file_path", "file_size"],
            Rows =
            [
                new Dictionary<string, object?>
                {
                    ["message"] = "Access database detected but cross-platform reading not yet implemented",
                    ["file_path"] = filePath,
                    ["file_size"] = File.Exists(filePath) ? new FileInfo(filePath).Length : 0
                }
            ]
        };

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamTableRowsAsync(
        string filePath,
        string tableName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger?.LogWarning("Access table streaming is not yet implemented. File: {FilePath}, Table: {TableName}",
            filePath, tableName);

        yield return new Dictionary<string, object?>
        {
            ["message"] = $"Access database table '{tableName}' detected but reading not yet implemented",
            ["file_path"] = filePath
        };

        await Task.CompletedTask;
    }
}
