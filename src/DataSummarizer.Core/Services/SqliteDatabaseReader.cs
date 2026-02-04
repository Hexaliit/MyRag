using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.DocSummarizer.Data.Models;

namespace Mostlylucid.DocSummarizer.Data.Services;

/// <summary>
///     Database reader implementation for SQLite files.
///     Extracts schema, relationships (foreign keys), and data.
/// </summary>
public class SqliteDatabaseReader : IDatabaseReader
{
    private readonly ILogger<SqliteDatabaseReader>? _logger;

    public SqliteDatabaseReader(ILogger<SqliteDatabaseReader>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => [".db", ".sqlite", ".sqlite3"];

    /// <inheritdoc />
    public bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return SupportedExtensions.Contains(ext);
    }

    /// <inheritdoc />
    public async Task<DatabaseSchema> GetSchemaAsync(string filePath, CancellationToken ct = default)
    {
        var connectionString = $"Data Source={filePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        var schema = new DatabaseSchema
        {
            FilePath = filePath,
            Type = DatabaseType.SQLite,
            Tables = [],
            Relationships = [],
            Indexes = []
        };

        // Get all tables (excluding SQLite internal tables)
        var tableNames = await GetTableNamesAsync(connection, ct);
        _logger?.LogDebug("Found {TableCount} tables in {FilePath}", tableNames.Count, filePath);

        foreach (var tableName in tableNames)
        {
            var tableInfo = await GetTableInfoAsync(connection, tableName, ct);
            schema.Tables.Add(tableInfo);

            // Get foreign keys for this table
            var relationships = await GetForeignKeysAsync(connection, tableName, ct);
            schema.Relationships.AddRange(relationships);

            // Get indexes for this table
            var indexes = await GetIndexesAsync(connection, tableName, ct);
            schema.Indexes.AddRange(indexes);
        }

        _logger?.LogInformation(
            "Schema extracted: {TableCount} tables, {RelationshipCount} relationships, {TotalRows:N0} total rows",
            schema.Tables.Count, schema.Relationships.Count, schema.TotalRowCount);

        return schema;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TableData> StreamTablesAsync(
        string filePath,
        int maxRowsPerTable = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connectionString = $"Data Source={filePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        var tableNames = await GetTableNamesAsync(connection, ct);

        foreach (var tableName in tableNames)
        {
            ct.ThrowIfCancellationRequested();

            var tableData = new TableData
            {
                TableName = tableName,
                Columns = [],
                Rows = []
            };

            // Get column names
            var columnQuery = $"PRAGMA table_info(\"{EscapeTableName(tableName)}\")";
            await using var columnCmd = new SqliteCommand(columnQuery, connection);
            await using var columnReader = await columnCmd.ExecuteReaderAsync(ct);

            while (await columnReader.ReadAsync(ct))
                tableData.Columns.Add(columnReader.GetString(1)); // name is at index 1

            // Read rows
            var limitClause = maxRowsPerTable > 0 ? $" LIMIT {maxRowsPerTable}" : "";
            var dataQuery = $"SELECT * FROM \"{EscapeTableName(tableName)}\"{limitClause}";
            await using var dataCmd = new SqliteCommand(dataQuery, connection);
            await using var dataReader = await dataCmd.ExecuteReaderAsync(ct);

            while (await dataReader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < dataReader.FieldCount; i++)
                {
                    var columnName = dataReader.GetName(i);
                    row[columnName] = dataReader.IsDBNull(i) ? null : dataReader.GetValue(i);
                }

                tableData.Rows.Add(row);
            }

            yield return tableData;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamTableRowsAsync(
        string filePath,
        string tableName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connectionString = $"Data Source={filePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        var query = $"SELECT * FROM \"{EscapeTableName(tableName)}\"";
        await using var cmd = new SqliteCommand(query, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                row[columnName] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return row;
        }
    }

    private static async Task<List<string>> GetTableNamesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var tables = new List<string>();
        const string query = """
                             SELECT name FROM sqlite_master
                             WHERE type='table'
                             AND name NOT LIKE 'sqlite_%'
                             ORDER BY name
                             """;

        await using var cmd = new SqliteCommand(query, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));

        return tables;
    }

    private static async Task<DatabaseTableInfo> GetTableInfoAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var columns = new List<DatabaseColumnInfo>();
        var primaryKeyColumns = new List<string>();

        // Get column info using PRAGMA table_info
        var pragmaQuery = $"PRAGMA table_info(\"{EscapeTableName(tableName)}\")";
        await using var pragmaCmd = new SqliteCommand(pragmaQuery, connection);
        await using var pragmaReader = await pragmaCmd.ExecuteReaderAsync(ct);

        while (await pragmaReader.ReadAsync(ct))
        {
            var columnName = pragmaReader.GetString(1);
            var dataType = pragmaReader.GetString(2);
            var notNull = pragmaReader.GetInt32(3) != 0;
            var defaultValue = pragmaReader.IsDBNull(4) ? null : pragmaReader.GetString(4);
            var isPrimaryKey = pragmaReader.GetInt32(5) != 0;

            columns.Add(new DatabaseColumnInfo
            {
                Name = columnName,
                DataType = dataType,
                IsNullable = !notNull,
                IsPrimaryKey = isPrimaryKey,
                DefaultValue = defaultValue,
                Ordinal = columns.Count,
                IsAutoIncrement = isPrimaryKey && dataType.ToUpperInvariant() == "INTEGER"
            });

            if (isPrimaryKey)
                primaryKeyColumns.Add(columnName);
        }

        // Get row count
        var countQuery = $"SELECT COUNT(*) FROM \"{EscapeTableName(tableName)}\"";
        await using var countCmd = new SqliteCommand(countQuery, connection);
        var rowCount = (long)(await countCmd.ExecuteScalarAsync(ct) ?? 0);

        return new DatabaseTableInfo
        {
            Name = tableName,
            Columns = columns,
            RowCount = rowCount,
            PrimaryKey = primaryKeyColumns,
            IsSystemTable = false
        };
    }

    private static async Task<List<RelationshipInfo>> GetForeignKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var relationships = new List<RelationshipInfo>();

        var fkQuery = $"PRAGMA foreign_key_list(\"{EscapeTableName(tableName)}\")";
        await using var fkCmd = new SqliteCommand(fkQuery, connection);
        await using var fkReader = await fkCmd.ExecuteReaderAsync(ct);

        while (await fkReader.ReadAsync(ct))
        {
            // PRAGMA foreign_key_list returns:
            // id, seq, table, from, to, on_update, on_delete, match
            var toTable = fkReader.GetString(2);
            var fromColumn = fkReader.GetString(3);
            var toColumn = fkReader.GetString(4);
            var onUpdate = fkReader.IsDBNull(5) ? null : fkReader.GetString(5);
            var onDelete = fkReader.IsDBNull(6) ? null : fkReader.GetString(6);

            relationships.Add(new RelationshipInfo
            {
                Name = $"fk_{tableName}_{fromColumn}",
                FromTable = tableName,
                FromColumn = fromColumn,
                ToTable = toTable,
                ToColumn = toColumn,
                Type = RelationshipType.ManyToOne,
                OnUpdate = onUpdate,
                OnDelete = onDelete,
                Confidence = 1.0 // Explicit FK = high confidence
            });
        }

        return relationships;
    }

    private static async Task<List<IndexInfo>> GetIndexesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var indexes = new List<IndexInfo>();

        var indexListQuery = $"PRAGMA index_list(\"{EscapeTableName(tableName)}\")";
        await using var indexListCmd = new SqliteCommand(indexListQuery, connection);
        await using var indexListReader = await indexListCmd.ExecuteReaderAsync(ct);

        var indexNames = new List<(string Name, bool IsUnique, string Origin)>();
        while (await indexListReader.ReadAsync(ct))
        {
            var indexName = indexListReader.GetString(1);
            var isUnique = indexListReader.GetInt32(2) != 0;
            var origin =
                indexListReader.GetString(3); // 'c' = CREATE INDEX, 'u' = UNIQUE constraint, 'pk' = PRIMARY KEY

            indexNames.Add((indexName, isUnique, origin));
        }

        foreach (var (indexName, isUnique, origin) in indexNames)
        {
            var columns = new List<string>();

            var indexInfoQuery = $"PRAGMA index_info(\"{EscapeTableName(indexName)}\")";
            await using var indexInfoCmd = new SqliteCommand(indexInfoQuery, connection);
            await using var indexInfoReader = await indexInfoCmd.ExecuteReaderAsync(ct);

            while (await indexInfoReader.ReadAsync(ct)) columns.Add(indexInfoReader.GetString(2)); // name is at index 2

            indexes.Add(new IndexInfo
            {
                Name = indexName,
                TableName = tableName,
                Columns = columns,
                IsUnique = isUnique,
                IsPrimaryKey = origin == "pk"
            });
        }

        return indexes;
    }

    private static string EscapeTableName(string tableName)
    {
        // Double any double-quotes in the table name to escape them
        return tableName.Replace("\"", "\"\"");
    }
}